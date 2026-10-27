using System.Text.Json;

namespace MarketPilot.Web.Integrations.Trendyol;

// Salt okunur ürün/sipariş/adres okuması (Satış Kanalları ekranı). Diğer Trendyol servisleri panel klasörlerindeki operasyonlardadır.
public sealed class TrendyolAdapter(HttpClient http, IConfiguration configuration) : IMarketplaceAdapter
{
    private readonly TrendyolApiClient client = new(http, configuration);
    private static readonly HashSet<string> ProductStatuses = new(StringComparer.Ordinal) { "archived", "blacklisted", "locked", "onSale", "notOnSale" };
    private static readonly HashSet<string> OrderStatuses = new(StringComparer.Ordinal) { "Created", "Picking", "Invoiced", "Shipped", "Cancelled", "Delivered", "UnDelivered", "Returned", "AtCollectionPoint", "UnPacked", "UnSupplied", "Awaiting", "Verified" };
    public string Marketplace => "trendyol";

    public async Task<MarketplaceAccount> TestAsync(ConnectionSettings settings, CancellationToken ct)
    {
        await ProductsAsync(settings, new ProductQuery(Size: 1), ct);
        return AccountIdentity(settings);
    }

    public async Task<MarketplaceAccount> AccountAsync(ConnectionSettings settings, CancellationToken ct)
    {
        var (seller, _, _) = Credentials(settings);
        using var doc = await GetAsync(settings, $"sellers/{seller}/addresses", [], true, ct);
        var root = doc.RootElement;
        if (!root.TryGetProperty("supplierAddresses", out var addresses) || addresses.ValueKind != JsonValueKind.Array)
            throw new IntegrationException("invalid_response", "Trendyol satıcı adresleri beklenen yapıda değil.");
        var result = addresses.EnumerateArray().Select(a => new MarketplaceAddress(
            Str(a, "id"), Str(a, "addressType"), Str(a, "country"), Str(a, "city"), Str(a, "district"),
            Str(a, "postCode"), First(a, "fullAddress", "address"), Bool(a, "isDefault") ?? false)).ToArray();
        return new("trendyol", seller, settings.Environment, "TR", result, DateTimeOffset.UtcNow);
    }

    public async Task<MarketplacePage<MarketplaceProduct>> ProductsAsync(ConnectionSettings settings, ProductQuery query, CancellationToken ct)
    {
        var (seller, _, _) = Credentials(settings);
        var parameters = string.IsNullOrWhiteSpace(query.NextPageToken) ? PageParameters(query.Page, query.Size) : new List<KeyValuePair<string, string>> { new("size", query.Size.ToString()) };
        Add(parameters, "barcode", query.Barcode);
        Add(parameters, "stockCode", query.StockCode);
        Add(parameters, "status", query.Approval == "unapproved" || ProductStatuses.Contains(query.Status ?? "") ? query.Status : null);
        Add(parameters, "nextPageToken", query.NextPageToken);
        using var doc = await GetAsync(settings, $"product/sellers/{seller}/products/{(query.Approval == "unapproved" ? "unapproved" : "approved")}", parameters, false, ct);
        if (query.Approval == "unapproved") return MapUnapprovedProducts(doc.RootElement, query);
        var items = new List<MarketplaceProduct>();
        foreach (var product in Content(doc.RootElement))
        {
            var category = Object(product, "category");
            var brand = Object(product, "brand");
            var images = Array(product, "images").Select(x => Str(x, "url")).Where(IsHttpUrl).Distinct().ToArray();
            foreach (var variant in Array(product, "variants"))
            {
                items.Add(new(
                    Str(variant, "variantId"), Str(product, "contentId"), Str(product, "productMainId"), Str(variant, "stockCode"),
                    Str(variant, "barcode"), Str(product, "title"), Str(category, "id"), Str(category, "name"), Str(brand, "name"),
                    NestedNumber(variant, "price", "listPrice"), NestedNumber(variant, "price", "salePrice"), NestedNumber(variant, "price", "priceSeenByCustomer"),
                    ToInt(NestedNumber(variant, "stock", "quantity")), "TRY", Bool(variant, "onSale"), Str(variant, "productUrl"), images, "approved", []));
            }
        }
        return Page(items, doc.RootElement, query.Page, query.Size);
    }

    public async Task<MarketplacePage<MarketplaceOrder>> OrdersAsync(ConnectionSettings settings, OrderQuery query, CancellationToken ct)
    {
        var (seller, _, _) = Credentials(settings);
        var parameters = query.Stream ? new List<KeyValuePair<string, string>> { new("size", query.Size.ToString()) } : PageParameters(query.Page, query.Size);
        Add(parameters, query.Stream ? "packageItemStatuses" : "status", OrderStatuses.Contains(query.Status ?? "") ? query.Status : null);
        if (query.Stream)
        {
            Add(parameters, "lastModifiedStartDate", query.StartDate?.ToString());
            Add(parameters, "lastModifiedEndDate", query.EndDate?.ToString());
            Add(parameters, "nextCursor", query.NextCursor);
        }
        else
        {
            Add(parameters, "orderNumber", query.OrderNumber);
            Add(parameters, "barcode", query.Barcode);
            Add(parameters, "startDate", query.StartDate?.ToString());
            Add(parameters, "endDate", query.EndDate?.ToString());
            Add(parameters, "orderByField", "PackageLastModifiedDate");
            Add(parameters, "orderByDirection", query.OrderByDirection == "ASC" ? "ASC" : "DESC");
        }
        using var doc = await GetAsync(settings, query.Stream ? $"order/sellers/{seller}/orders/stream" : $"order/sellers/{seller}/v2/orders", parameters, false, ct);
        var items = Content(doc.RootElement).Select(MapOrder).ToArray();
        return query.Stream
            ? new(items, query.Page, query.Size, null, null, null, Bool(doc.RootElement, "hasMore"), Str(doc.RootElement, "nextCursor") is { Length: > 0 } cursor ? cursor : null)
            : Page(items, doc.RootElement, query.Page, query.Size);
    }

    private static MarketplacePage<MarketplaceProduct> MapUnapprovedProducts(JsonElement root, ProductQuery query)
    {
        var items = Content(root).Select(product =>
        {
            var category = Object(product, "category");
            var brand = Object(product, "brand");
            var images = Array(product, "media").Select(x => Str(x, "url")).Where(IsHttpUrl).Distinct().ToArray();
            var reasons = Array(product, "rejectReasonDetails").Select(x => $"{Str(x, "rejectReason")}: {Str(x, "rejectReasonDetail")}".Trim(' ', ':')).Where(x => x.Length > 0).ToArray();
            return new MarketplaceProduct(
                Str(product, "barcode"), "", Str(product, "productMainId"), Str(product, "stockCode"), Str(product, "barcode"), Str(product, "title"),
                Str(category, "id"), Str(category, "name"), Str(brand, "name"), Number(product, "listPrice"), Number(product, "salePrice"), null,
                ToInt(Number(product, "quantity")), "TRY", false, "", images, Str(product, "status"), reasons);
        }).ToArray();
        return Page(items, root, query.Page, query.Size);
    }

    private MarketplaceAccount AccountIdentity(ConnectionSettings settings)
    {
        var (seller, _, _) = Credentials(settings);
        return new("trendyol", seller, settings.Environment, "TR", [], DateTimeOffset.UtcNow);
    }

    private (string SellerId, string ApiKey, string ApiSecret) Credentials(ConnectionSettings settings) => client.Credentials(settings.Environment);

    private Task<JsonDocument> GetAsync(ConnectionSettings settings, string path, IEnumerable<KeyValuePair<string, string>> parameters, bool storeFrontHeader, CancellationToken ct) =>
        client.GetJsonAsync(settings.Environment, path, parameters, storeFrontHeader, ct);

    private static MarketplaceOrder MapOrder(JsonElement order)
    {
        var shipment = Object(order, "shipmentAddress");
        var invoiceAddress = Object(order, "invoiceAddress");
        var shipmentAddress = shipment.ValueKind == JsonValueKind.Object ? new MarketplaceAddress(
            Str(shipment, "id"), "Shipment", Str(shipment, "countryCode"), Str(shipment, "city"), Str(shipment, "district"),
            Str(shipment, "postalCode"), Str(shipment, "fullAddress"), false) : null;
        var lines = Array(order, "lines").Select(line => new MarketplaceOrderLine(
            Str(line, "lineId"), Str(line, "stockCode"), Str(line, "barcode"), Str(line, "productName"), ToInt(Number(line, "quantity")) ?? 0,
            Number(line, "lineUnitPrice"), Number(line, "lineGrossAmount"), Number(line, "vatRate"), Str(line, "currencyCode"),
            Str(line, "orderLineItemStatusName"), Str(line, "productCategoryId"))).ToArray();
        var cargo = new MarketplaceCargo(Str(order, "cargoProviderName"), Str(order, "cargoTrackingNumber"), Str(order, "cargoTrackingLink"),
            Str(order, "cargoSenderNumber"), Str(order, "shipmentNumber"), Number(order, "deci"), ToInt(Number(order, "boxQuantity")));
        var invoice = new MarketplaceInvoice(Str(order, "invoiceNumber"), Str(order, "invoiceStatus"), Str(order, "invoiceLink"), Bool(order, "commercial") ?? false,
            Str(invoiceAddress, "company"), Str(invoiceAddress, "taxOffice"), Str(invoiceAddress, "taxNumber"), Bool(invoiceAddress, "eInvoiceAvailable"), StringArray(order, "invoiceRejectedReasonKeys"));
        return new(
            First(order, "shipmentPackageId", "id"), Str(order, "orderNumber"), First(order, "shipmentPackageStatus", "status"), UnixDate(Number(order, "orderDate")),
            ($"{Str(order, "customerFirstName")} {Str(order, "customerLastName")}").Trim(), Str(order, "customerEmail"),
            FirstNumber(order, "packageTotalPrice", "totalPrice"), Number(order, "packageGrossAmount"), Number(order, "packageTotalDiscount"), Str(order, "currencyCode"),
            Str(order, "paymentMethod"), Str(order, "deliveryAddressType"), shipmentAddress, lines, cargo, invoice);
    }

    private static MarketplacePage<T> Page<T>(IReadOnlyList<T> items, JsonElement root, int page, int size) =>
        new(items, page, size, ToLong(Number(root, "totalElements")), ToInt(Number(root, "totalPages")), Str(root, "nextPageToken") is { Length: > 0 } token ? token : null);
    private static List<KeyValuePair<string, string>> PageParameters(int page, int size) => [new("page", page.ToString()), new("size", size.ToString())];
    private static void Add(List<KeyValuePair<string, string>> list, string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) list.Add(new(name, value.Trim())); }
    private static string Str(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? v.ToString() : "";
    private static string First(JsonElement e, params string[] keys) => keys.Select(k => Str(e, k)).FirstOrDefault(x => x.Length > 0) ?? "";
    private static decimal? Number(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n) ? n : null;
    private static decimal? FirstNumber(JsonElement e, params string[] keys) => keys.Select(k => Number(e, k)).FirstOrDefault(x => x.HasValue);
    private static decimal? NestedNumber(JsonElement e, string parent, string key) => Number(Object(e, parent), key);
    private static bool? Bool(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    private static JsonElement Object(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
    private static JsonElement[] Array(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToArray() : [];
    private static JsonElement[] Content(JsonElement root) => root.TryGetProperty("content", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToArray() : throw new IntegrationException("invalid_response", "Trendyol yanıtı beklenen liste yapısında değil.");
    private static string[] StringArray(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return [];
        if (v.ValueKind == JsonValueKind.String) return string.IsNullOrWhiteSpace(v.GetString()) ? [] : [v.GetString()!];
        return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.ToString()).Where(x => x.Length > 0).ToArray() : [];
    }
    private static int? ToInt(decimal? value) => value is >= int.MinValue and <= int.MaxValue ? decimal.ToInt32(value.Value) : null;
    private static long? ToLong(decimal? value) => value is >= long.MinValue and <= long.MaxValue ? decimal.ToInt64(value.Value) : null;
    private static DateTimeOffset? UnixDate(decimal? value)
    {
        try { return value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(decimal.ToInt64(value.Value)) : null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    private static bool IsHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
}
