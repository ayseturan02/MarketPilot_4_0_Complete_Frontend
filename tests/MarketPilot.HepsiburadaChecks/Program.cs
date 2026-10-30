using System.Net;
using System.Text.Json;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Hepsiburada;
using Microsoft.Extensions.Configuration;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
var merchant = "12345678-1234-1234-1234-123456789abc";
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
    ["Marketplaces:Hepsiburada:stage:MerchantId"] = merchant,
    ["Marketplaces:Hepsiburada:stage:Username"] = "fixture-user",
    ["Marketplaces:Hepsiburada:stage:Password"] = "fixture-password",
    ["Marketplaces:Hepsiburada:stage:UserAgent"] = "fixture-integrator"
}).Build();
var handler = new Fixture(); var adapter = new HepsiburadaAdapter(new HttpClient(handler), configuration);
var settings = new ConnectionSettings("Fixture", "hepsiburada", "real", "stage");
Check(HepsiburadaAdapter.IsConfigured(configuration,"stage") && !HepsiburadaAdapter.IsConfigured(configuration,"production"),"Environment isolation failed");
Check(HepsiburadaAdapter.Host("listing","production")=="listing-external.hepsiburada.com","Production host failed");
handler.Body = """{"totalCount":201,"listings":[{"listingId":"listing-1","hepsiburadaSku":"HBV000TEST","merchantSku":"TSH-EKR-S","availableStock":0,"price":449.90,"isSalable":false}]}""";
var p = await adapter.ProductsAsync(settings,new(Page:2,Size:25,StockCode:"TSH-EKR-S"),default);
Check(p.Items.Count==1&&p.Items[0].Stock==0&&p.Items[0].SalePrice==449.90m&&p.Items[0].Barcode=="","Listing mapping must preserve unknown barcode and zero stock");
Check(p.TotalPages==9&&p.TotalElements==201,"Pagination totals failed");
Check(handler.Uri!.Host=="listing-external-sit.hepsiburada.com"&&handler.Uri.Query.Contains("offset=50")&&handler.Uri.Query.Contains("merchantSkuList=TSH-EKR-S"),"Native offset/SKU query failed");
Check(handler.Authorization=="Basic "+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("fixture-user:fixture-password"))&&handler.UserAgent=="fixture-integrator","Authentication contract failed");
var identity = await adapter.TestAsync(settings,default);Check(identity.SellerId==merchant&&identity.Addresses.Count==0,"Connection test fabricated address data");
var calls=handler.Calls;
try { await adapter.ProductsAsync(settings,new(Barcode:"8691000000001"),default);throw new Exception("Unsupported barcode accepted"); } catch(IntegrationException e) { Check(e.Status==400,"Unsupported barcode was not rejected"); }
Check(calls==handler.Calls,"Unsupported query reached provider");
handler.Body = """{"totalCount":76,"items":[{"id":"line-1","orderNumber":"1234567890","merchantSku":"TSH-EKR-S","sku":"HBV000TEST","name":"Örnek tişört","quantity":2,"unitPrice":{"amount":449.9,"currency":"TRY"},"totalPrice":{"amount":899.8,"currency":"TRY"},"status":"Open","customerName":"Örnek Müşteri","shippingAddress":{"address":"Örnek adres","city":"İstanbul","town":"Kadıköy"},"orderDate":"2026-10-20T12:00:00Z","hasInvoice":false}]}""";
var orders=await adapter.OrdersAsync(settings,new(Page:1,Size:25),default);
Check(handler.Uri!.AbsolutePath.EndsWith("/orders/merchantid/"+merchant)&&handler.Uri.Query.Contains("offset=25"),"Paid order endpoint failed");
Check(orders.TotalPages==4&&orders.Items[0].Lines[0].UnitPrice==449.9m&&orders.Items[0].Total==899.8m&&orders.Items[0].Status=="Created"&&orders.Items[0].ShipmentAddress!.City=="İstanbul","Order line/amount/address mapping failed");
await adapter.OrdersAsync(settings,new(Size:25,Status:"Awaiting"),default);Check(handler.Uri!.AbsolutePath.EndsWith("/paymentawaiting"),"Payment-awaiting endpoint failed");
handler.Body="""[{"packageNumber":"PK-1","orderNumber":"1234567890","status":"Open","barcode":"CARGO-ONLY","items":[{"id":"package-item-not-order-line","lineItemId":"line-1","merchantSku":"TSH-EKR-S","quantity":2,"price":{"amount":449.9,"currency":"TRY"}}]}]""";
handler.TotalCount="21";
var packages=await adapter.OrdersAsync(settings,new(Page:1,Size:25,Status:"Picking"),default);
Check(handler.Uri!.AbsolutePath.EndsWith("/packages/merchantid/"+merchant)&&handler.Uri.Query.Contains("limit=10")&&handler.Uri.Query.Contains("offset=10"),"Packages endpoint/limit failed");
Check(packages.Items[0].Lines[0].Id==orders.Items[0].Lines[0].Id&&packages.Items[0].Lines[0].Barcode==""&&packages.Items[0].Status=="Picking","Paid/package identity changed or cargo barcode treated as product");
Check(packages.TotalElements==21&&packages.TotalPages==3,"Package response header pagination lost");
var beforeLongWindow=handler.Calls;
try{await adapter.OrdersAsync(settings,new(Status:"Picking",StartDate:0,EndDate:172800000),default);throw new Exception("Long package window accepted");}catch(IntegrationException error){Check(error.Code=="invalid_dates"&&handler.Calls==beforeLongWindow,"Long package window reached provider");}
await adapter.OrdersAsync(settings,new(Size:25,Status:"MissingInvoice"),default);Check(handler.Uri!.AbsolutePath.EndsWith("/missing-invoice"),"Missing invoice endpoint failed");
handler.TotalCount=null;
handler.Body="""[{"packageNumber":"PK-1","items":[{"id":"unverified-package-id","merchantSku":"TSH-EKR-S","quantity":2}]}]""";
try{await adapter.OrdersAsync(settings,new(Status:"Picking"),default);throw new Exception("Missing line identity accepted");}catch(IntegrationException error){Check(error.Code=="package_identity_missing","Missing identity was not blocked");}
handler.Body = """{"orderNumber":"1234567890","items":[{"merchantSku":"X","quantity":1,"unitPrice":{"amount":12,"currency":"TRY"}}]}""";
var detail=await adapter.OrdersAsync(settings,new(Size:25,OrderNumber:"1234567890"),default);Check(detail.Items.Count==1&&detail.Items[0].OrderNumber=="1234567890","Order detail object mapping failed");
Check(handler.Uri!.AbsolutePath.EndsWith("/ordernumber/1234567890"),"Order detail endpoint failed");
handler.Body = """{"totalCount":1,"items":[{"claimNumber":"C-1","orderNumber":"1234567890","customerName":"Örnek","explanation":"Beden uymadı","quantity":1,"status":"AwaitingAction"}]}""";
var claims = JsonSerializer.SerializeToElement(await adapter.ClaimsAsync(settings,new(),default),new JsonSerializerOptions(JsonSerializerDefaults.Web));
Check(claims.GetProperty("items")[0].GetProperty("reason").GetString()=="Beden uymadı"&&handler.Uri!.AbsolutePath.EndsWith("/status/AwaitingAction"),"Claim mapping/endpoint failed");
handler.Status=HttpStatusCode.TooManyRequests;handler.Body="secret provider body must not escape";
try{await adapter.TestAsync(settings,default);throw new Exception("Rate limit accepted");}catch(IntegrationException e){Check(e.Code=="rate_limited"&&e.Status==429&&!e.Message.Contains("secret"),"Safe provider errors failed");}
handler.Status=HttpStatusCode.OK;handler.Body="{\"unexpected\":true}";
try{await adapter.TestAsync(settings,default);throw new Exception("Invalid response accepted");}catch(IntegrationException e){Check(e.Code=="invalid_response","Invalid response classification failed");}
Console.WriteLine("PASS Hepsiburada fixtures: environment/authentication, native paging, exact SKU, unknown fields, paid/unpaid orders, packages, missing invoices, claims, safe errors; no external writes.");

sealed class Fixture : HttpMessageHandler
{
    public string Body="{}"; public HttpStatusCode Status=HttpStatusCode.OK;public Uri? Uri;public string? Authorization,UserAgent,TotalCount;public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
    {
        if(r.Method!=HttpMethod.Get)throw new Exception("Unexpected live write");
        Calls++;Uri=r.RequestUri;Authorization=r.Headers.Authorization?.ToString();UserAgent=r.Headers.GetValues("User-Agent").Single();
        var response=new HttpResponseMessage(Status){Content=new StringContent(Body)};
        if(TotalCount!=null)response.Headers.TryAddWithoutValidation("totalcount",TotalCount);
        return Task.FromResult(response);
    }
}
