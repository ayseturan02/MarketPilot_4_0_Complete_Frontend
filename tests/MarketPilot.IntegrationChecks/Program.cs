using System.Net;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Amazon;
using MarketPilot.Web.Integrations.Demo;
using MarketPilot.Web.Integrations.Hepsiburada;
using MarketPilot.Web.Integrations.N11;
using MarketPilot.Web.Integrations.Trendyol;
using Microsoft.Extensions.Configuration;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Marketplaces:Trendyol:stage:SellerId"] = "123",
    ["Marketplaces:Trendyol:stage:ApiKey"] = "test-key",
    ["Marketplaces:Trendyol:stage:ApiSecret"] = "test-secret"
}).Build();
var handler = new FixtureHandler();
var adapter = new TrendyolAdapter(new HttpClient(handler), configuration);
var settings = new ConnectionSettings("Test", "trendyol", "real", "stage");

handler.Body = ProductJson();
var test = await adapter.TestAsync(settings, default);
Check(test.SellerId == "123" && test.Addresses.Count == 0, "Connection identity failed");
Check(handler.Uri!.AbsolutePath.EndsWith("/products/approved") && handler.Uri.Query.Contains("size=1"), "Connection test must use a read-only product request");

handler.Body = ProductJson();
var products = await adapter.ProductsAsync(settings, new(1, 20, "BC/1", "SKU 1", "onSale"), default);
Check(products.Items.Count == 2 && products.Items[0].SalePrice == 99.5m && products.Items[0].Stock == 7, "V2 nested price/stock mapping failed");
Check(products.Items[0].CategoryName == "Elektronik" && products.Items[0].BrandName == "Marka" && products.Items[0].Images.Count == 1, "Product detail mapping failed");
Check(products.Items[1].Stock is null && products.Items[1].SalePrice is null, "Missing product values must remain null");
Check(products.NextPageToken == "token" && products.TotalElements == 1 && products.TotalPages == 1, "Product pagination metadata lost");
Check(handler.Uri!.Query.Contains("barcode=BC%2F1") && handler.Uri.Query.Contains("stockCode=SKU%201") && handler.Uri.Query.Contains("status=onSale"), "Product filters were not safely encoded");
Check(handler.Uri.Host == "stageapigw.trendyol.com" && handler.Authorization!.StartsWith("Basic ") && handler.UserAgent == "123 - SelfIntegration", "Authentication or environment failed");

handler.Body = UnapprovedProductJson();
var drafts = await adapter.ProductsAsync(settings, new ProductQuery(Size: 100, Status: "rejected", NextPageToken: "opaque token", Approval: "unapproved"), default);
Check(drafts.Items[0].ApprovalStatus == "rejected" && drafts.Items[0].RejectReasons.Count == 1 && drafts.Items[0].Images.Count == 1, "Unapproved product mapping failed");
Check(handler.Uri!.AbsolutePath.EndsWith("/products/unapproved") && handler.Uri.Query.Contains("nextPageToken=opaque%20token") && !handler.Uri.Query.Contains("page="), "Product cursor request failed");

handler.Body = OrderJson();
var orders = await adapter.OrdersAsync(settings, new(1, 20, "Created", "ORDER", "BC", 1000, 2000, "ASC"), default);
var order = orders.Items[0];
Check(order.PackageId == "42" && order.CustomerName == "Ayşe Yılmaz" && order.Lines[0].Quantity == 2, "Order/customer/line mapping failed");
Check(order.Cargo.Provider == "Trendyol Express" && order.Cargo.TrackingNumber == "72800", "Cargo mapping failed");
Check(order.Invoice.Number == "FTR-1" && order.Invoice.Status == "Invoiced" && order.Invoice.TaxOffice == "Kadıköy" && order.Invoice.RejectedReasons.SequenceEqual(new[] { "REASON_A" }), "Invoice mapping failed");
Check(handler.Uri!.AbsolutePath.EndsWith("/v2/orders") && handler.Uri.Query.Contains("startDate=1000") && handler.Uri.Query.Contains("orderByDirection=ASC"), "Order V2 filters failed");

handler.Body = OrderJson().Replace("\"totalElements\":1,\"totalPages\":1", "\"hasMore\":true,\"nextCursor\":\"cursor-2\"");
var stream = await adapter.OrdersAsync(settings, new OrderQuery(Size: 200, Status: "Created", StartDate: 1000, EndDate: 2000, Stream: true, NextCursor: "cursor-1"), default);
Check(stream.HasMore == true && stream.NextCursor == "cursor-2" && stream.TotalElements is null, "Order stream metadata failed");
Check(handler.Uri!.AbsolutePath.EndsWith("/orders/stream") && handler.Uri.Query.Contains("packageItemStatuses=Created") && handler.Uri.Query.Contains("lastModifiedStartDate=1000") && handler.Uri.Query.Contains("nextCursor=cursor-1") && !handler.Uri.Query.Contains("page="), "Order stream request failed");

handler.Body = AddressJson();
var account = await adapter.AccountAsync(settings, default);
Check(account.SellerId == "123" && account.Addresses.Count == 2 && account.Addresses[0].IsDefault, "Seller account/address mapping failed");
Check(handler.Uri!.AbsolutePath.EndsWith("/sellers/123/addresses") && handler.StoreFront == "TR", "Seller address request headers failed");

handler.Status = HttpStatusCode.Unauthorized; handler.Body = "test-secret upstream private content";
try { await adapter.TestAsync(settings, default); throw new Exception("401 accepted"); }
catch (IntegrationException e) { Check(e.Code == "upstream_401" && !e.Message.Contains("test-secret"), "Unsafe 401 response"); }
handler.Status = HttpStatusCode.TooManyRequests;
try { await adapter.TestAsync(settings, default); throw new Exception("429 accepted"); }
catch (IntegrationException e) { Check(e.Code == "upstream_429", "Rate limit not classified"); }
handler.Status = HttpStatusCode.OK; handler.Body = "{}";
try { await adapter.TestAsync(settings, default); throw new Exception("Malformed response accepted"); }
catch (IntegrationException e) { Check(e.Code == "invalid_response", "Malformed schema not classified"); }
var calls = handler.Calls;
try { await adapter.TestAsync(settings with { Environment = "production" }, default); throw new Exception("Missing credentials accepted"); }
catch (IntegrationException e) { Check(e.Code == "configuration_missing" && handler.Calls == calls, "Missing credentials caused a network call"); }

var demo = new DemoMarketplaceAdapter();
Check((await demo.ProductsAsync(settings, new ProductQuery(), default)).Items[0].Images.Count == 1, "Demo product details failed");
Check((await demo.OrdersAsync(settings, new OrderQuery(Status: "Created"), default)).Items[0].Cargo.Provider.Length > 0, "Demo order details failed");
Check((await demo.AccountAsync(settings, default)).Addresses.Count == 1, "Demo account failed");
foreach (IMarketplaceAdapter planned in new IMarketplaceAdapter[] { new HepsiburadaAdapter(), new N11Adapter(), new AmazonSpApiAdapter() })
{
    try { await planned.TestAsync(settings, default); throw new Exception("Planned adapter reported success"); }
    catch (IntegrationException e) { Check(e.Code == "adapter_not_ready", "Planned adapter not classified"); }
}
// Trendyol panel operations: catalog, dry-run, write gate, path validation and request shape.
Check(TrendyolCatalog.Panels.Count == 13 && TrendyolCatalog.Operations.Count == TrendyolCatalog.Panels.Sum(p => p.Operations.Count), "Trendyol catalog keys must be unique");
Check(TrendyolCatalog.Panels.All(p => p.Operations.All(o => o.Panel == p.Key && o.Key.StartsWith(p.Key + "."))), "Every operation must belong to its own panel");
Check(TrendyolCatalog.Operations.Values.All(o => o.Method is "GET" or "POST" or "PUT" or "DELETE" && !o.Path.StartsWith('/') && (o.Method == "GET" ? !o.Write : true)), "Operation definitions invalid");
Check(TrendyolCatalog.Operations.Values.Where(o => o.SampleBody is not null).All(o => { try { System.Text.Json.JsonDocument.Parse(o.SampleBody!).Dispose(); return true; } catch { return false; } }), "Sample bodies must be valid JSON");
var writeConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Marketplaces:Trendyol:stage:SellerId"] = "123", ["Marketplaces:Trendyol:stage:ApiKey"] = "test-key",
    ["Marketplaces:Trendyol:stage:ApiSecret"] = "test-secret", ["Marketplaces:Trendyol:stage:AllowWrites"] = "true"
}).Build();
var opHandler = new FixtureHandler { AllowWrites = true };
var runner = new TrendyolOperationRunner(new TrendyolApiClient(new HttpClient(opHandler), configuration), configuration);
var writeRunner = new TrendyolOperationRunner(new TrendyolApiClient(new HttpClient(opHandler), writeConfig), writeConfig);
var updatePackage = TrendyolCatalog.Operations["orders.updatePackage"];
var pickingBody = System.Text.Json.JsonDocument.Parse(updatePackage.SampleBody!).RootElement;
var packageRequest = new TrendyolOperationRequest(Path: new() { ["packageId"] = "42" }, Body: pickingBody, Confirm: true);
var dry = await runner.RunAsync(updatePackage, settings with { Mode = "demo" }, packageRequest, default);
Check(!dry.Sent && dry.DryRun && dry.Url.Contains("/sellers/DEMO/shipment-packages/42") && opHandler.Calls == 0, "Demo operation must not call Trendyol");
var gated = await runner.RunAsync(updatePackage, settings, packageRequest, default);
Check(!gated.Sent && gated.Reason!.Contains("AllowWrites") && opHandler.Calls == 0, "Writes must stay disabled without AllowWrites");
var unconfirmed = await writeRunner.RunAsync(updatePackage, settings, packageRequest with { Confirm = false }, default);
Check(!unconfirmed.Sent && opHandler.Calls == 0, "Writes must require confirmation");
opHandler.Body = "";
var sent = await writeRunner.RunAsync(updatePackage, settings, packageRequest, default);
Check(sent.Sent && opHandler.Method == HttpMethod.Put && opHandler.Uri!.AbsolutePath == "/integration/order/sellers/123/shipment-packages/42" && opHandler.RequestBody!.Contains("\"Picking\""), "Confirmed write request shape failed");
opHandler.Body = """{"content":[],"totalElements":0}""";
var claims = await runner.RunAsync(TrendyolCatalog.Operations["claims.list"], settings, new(Query: new() { ["claimItemStatus"] = "WaitingInAction", ["page"] = "", ["size"] = "50" }), default);
Check(claims.Sent && claims.Data!.Value.GetProperty("totalElements").GetInt32() == 0 && opHandler.Uri!.Query == "?claimItemStatus=WaitingInAction&size=50", "Read operation or empty query filtering failed");
var callsBefore = opHandler.Calls;
try { await runner.RunAsync(TrendyolCatalog.Operations["questions.get"], settings, new(Path: new() { ["id"] = "../../x" }), default); throw new Exception("Path traversal accepted"); }
catch (IntegrationException e) { Check(e.Code == "invalid_request" && opHandler.Calls == callsBefore, "Path value not validated"); }
try { await runner.RunAsync(TrendyolCatalog.Operations["test.createOrder"], settings with { Environment = "production" }, new(), default); throw new Exception("Stage-only accepted on production"); }
catch (IntegrationException e) { Check(e.Code == "stage_only", "Stage-only not enforced"); }
opHandler.Status = HttpStatusCode.BadRequest; opHandler.Body = """{"errors":[{"message":"lineId zorunludur"}],"secret":"test-secret"}""";
try { await writeRunner.RunAsync(updatePackage, settings, packageRequest, default); throw new Exception("400 accepted"); }
catch (IntegrationException e) { Check(e.Code == "upstream_400" && e.Message.Contains("lineId zorunludur") && !e.Message.Contains("test-secret"), "Validation messages not surfaced safely"); }

Console.WriteLine("PASS: Trendyol 5.1 account, approved/unapproved Product V2, token pagination, Order V2 details, order stream cursor, sanitized errors, demo adapters and 13 Trendyol operation panels.");

static string ProductJson() => """
{"content":[{"contentId":10,"productMainId":"MAIN","title":"Ürün","category":{"id":5,"name":"Elektronik"},"brand":{"name":"Marka"},"images":[{"url":"https://cdn.example.com/1.jpg"},{"url":"javascript:bad"}],"variants":[{"variantId":11,"stockCode":"SKU","barcode":"BC","onSale":true,"productUrl":"https://www.trendyol.com/p","price":{"listPrice":120,"salePrice":99.5,"priceSeenByCustomer":95},"stock":{"quantity":7}},{"variantId":12,"barcode":"EMPTY","price":{},"stock":{}}]}],"totalElements":1,"totalPages":1,"nextPageToken":"token"}
""";
static string UnapprovedProductJson() => """
{"content":[{"productMainId":"DRAFT-MAIN","status":"rejected","brand":{"name":"Marka"},"category":{"id":5,"name":"Elektronik"},"barcode":"DRAFT-BC","title":"Reddedilen ürün","quantity":3,"listPrice":120,"salePrice":100,"stockCode":"DRAFT-SKU","media":[{"url":"https://cdn.example.com/draft.jpg"}],"rejectReasonDetails":[{"rejectReason":"Eksik bilgi","rejectReasonDetail":"Özellik tamamlanmalı"}]}],"totalElements":1,"totalPages":1,"nextPageToken":"next"}
""";
static string OrderJson() => """
{"content":[{"shipmentPackageId":42,"shipmentNumber":99,"orderNumber":"ORDER","shipmentPackageStatus":"Created","orderDate":1762253333685,"customerFirstName":"Ayşe","customerLastName":"Yılmaz","customerEmail":"a@example.com","packageTotalPrice":99.5,"packageGrossAmount":120,"packageTotalDiscount":20.5,"currencyCode":"TRY","paymentMethod":"Kredi Kartı","deliveryAddressType":"Shipment","shipmentAddress":{"id":1,"countryCode":"TR","city":"İstanbul","district":"Kadıköy","postalCode":"34710","fullAddress":"Adres"},"lines":[{"lineId":7,"stockCode":"SKU","barcode":"BC","productName":"Ürün","quantity":2,"lineUnitPrice":49.75,"lineGrossAmount":60,"vatRate":20,"currencyCode":"TRY","orderLineItemStatusName":"Created","productCategoryId":5}],"cargoProviderName":"Trendyol Express","cargoTrackingNumber":72800,"cargoTrackingLink":"https://tracking.trendyol.com/x","cargoSenderNumber":"S1","deci":2.5,"boxQuantity":1,"invoiceNumber":"FTR-1","invoiceStatus":"Invoiced","invoiceLink":"https://invoice.example.com/x","commercial":true,"invoiceRejectedReasonKeys":["REASON_A"],"invoiceAddress":{"company":"Firma","taxOffice":"Kadıköy","taxNumber":"123","eInvoiceAvailable":true}}],"totalElements":1,"totalPages":1}
""";
static string AddressJson() => """
{"supplierAddresses":[{"id":1,"addressType":"Shipment","country":"Türkiye","city":"İstanbul","district":"Kadıköy","postCode":"34710","fullAddress":"Adres 1","isDefault":true},{"id":2,"addressType":"Invoice","country":"Türkiye","city":"Ankara","district":"Çankaya","postCode":"06000","address":"Adres 2","isDefault":false}]}
""";

sealed class FixtureHandler : HttpMessageHandler
{
    public string Body = "{}";
    public HttpStatusCode Status = HttpStatusCode.OK;
    public Uri? Uri;
    public string? Authorization;
    public string? UserAgent;
    public string? StoreFront;
    public int Calls;
    public bool AllowWrites;
    public HttpMethod? Method;
    public string? RequestBody;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++; Uri = request.RequestUri; Authorization = request.Headers.Authorization?.ToString();
        UserAgent = request.Headers.TryGetValues("User-Agent", out var userAgents) ? string.Join(" ", userAgents) : null;
        StoreFront = request.Headers.TryGetValues("storeFrontCode", out var values) ? values.Single() : null;
        Method = request.Method; RequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        if (request.Method != HttpMethod.Get && !AllowWrites) throw new Exception("Unexpected marketplace write");
        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
    }
}
