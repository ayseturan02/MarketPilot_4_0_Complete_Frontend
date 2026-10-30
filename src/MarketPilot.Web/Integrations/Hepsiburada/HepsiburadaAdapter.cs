using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MarketPilot.Web.Integrations.Hepsiburada;

// Only fixed, documented read endpoints are exposed. Credentials never reach browser/state files.
public sealed partial class HepsiburadaAdapter(HttpClient http, IConfiguration configuration) : IMarketplaceAdapter
{
    public string Marketplace => "hepsiburada";
    private const int MaxBytes = 10 * 1024 * 1024;
    public static bool IsConfigured(IConfiguration c, string environment)
    {
        var s = c.GetSection($"Marketplaces:Hepsiburada:{environment}");
        return Guid.TryParse(s["MerchantId"], out _) && new[] { "Username", "Password", "UserAgent" }.All(k => !string.IsNullOrWhiteSpace(s[k]) && !s[k]!.Any(char.IsControl));
    }
    private string Merchant(string environment)
    {
        if (environment is not ("stage" or "production") || !IsConfigured(configuration, environment))
            throw new IntegrationException("configuration_missing", "Hepsiburada mağaza bağlantısı henüz tanımlanmadı.", 409);
        return Guid.Parse(configuration[$"Marketplaces:Hepsiburada:{environment}:MerchantId"]!).ToString();
    }
    public static string Host(string service, string environment) => (service, environment) switch
    {
        ("listing", "stage") => "listing-external-sit.hepsiburada.com",
        ("listing", "production") => "listing-external.hepsiburada.com",
        ("orders", "stage") => "oms-external-sit.hepsiburada.com",
        ("orders", "production") => "oms-external.hepsiburada.com",
        _ => throw new IntegrationException("invalid_service", "Geçersiz mağaza ortamı veya servis.", 400)
    };
    private Task<JsonDocument> Get(string environment, string service, string path, Dictionary<string,string> query, CancellationToken ct) => Send(environment, service, path, query, null, ct);
    private async Task<JsonDocument> Send(string environment, string service, string path, Dictionary<string, string> query, HttpContent? body, CancellationToken ct)
    {
        Merchant(environment);
        var s = configuration.GetSection($"Marketplaces:Hepsiburada:{environment}");
        var qs = string.Join('&', query.Select(k => Uri.EscapeDataString(k.Key) + "=" + Uri.EscapeDataString(k.Value)));
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, $"https://{Host(service, environment)}/{path}?{qs}") { Content = body };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(s["Username"] + ":" + s["Password"])));
        request.Headers.TryAddWithoutValidation("User-Agent", s["UserAgent"]);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            throw new IntegrationException(code == 429 ? "rate_limited" : "hepsiburada_error", code switch
            {
                401 or 403 => "Hepsiburada mağaza bilgileri doğrulanamadı.",
                429 => "Hepsiburada istek sınırına ulaşıldı. Biraz sonra yeniden deneyin.",
                404 => "Hepsiburada kaydı bulunamadı.",
                _ => "Hepsiburada verileri alınamadı. Daha sonra yeniden deneyin."
            }, code is 401 or 403 ? 409 : code is 400 or 404 or 409 or 422 or 429 ? (code==422?400:code) : 502);
        }
        if (response.Content.Headers.ContentLength > MaxBytes) throw new IntegrationException("response_large", "Mağaza yanıtı boyut sınırını aştı.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var block = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(block, ct)) > 0) { if (buffer.Length + read > MaxBytes) throw new IntegrationException("response_large", "Mağaza yanıtı boyut sınırını aştı."); buffer.Write(block, 0, read); }
        return JsonDocument.Parse(buffer.ToArray());
    }
    private static JsonElement Field(JsonElement e, params string[] names)
    {
        if (e.ValueKind == JsonValueKind.Object) foreach (var name in names) foreach (var p in e.EnumerateObject()) if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return p.Value;
        return default;
    }
    private static string Text(JsonElement e, params string[] names) { var v = Field(e, names); return v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : ""; }
    private static decimal? Number(JsonElement e, params string[] names) { var v = Field(e, names); if (v.ValueKind == JsonValueKind.Object) v = Field(v, "amount"); return decimal.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : null; }
    private static bool? Flag(JsonElement e, string name) => Field(e, name).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    private static DateTimeOffset? Date(JsonElement e, params string[] names) => DateTimeOffset.TryParse(Text(e, names), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    private static JsonElement[] Rows(JsonElement e, params string[] names)
    {
        if (e.ValueKind == JsonValueKind.Array) return e.EnumerateArray().ToArray();
        var rows = Field(e, names.Length == 0 ? ["items", "listings", "data"] : names);
        if (rows.ValueKind == JsonValueKind.Array) return rows.EnumerateArray().ToArray();
        throw new IntegrationException("invalid_response", "Hepsiburada liste yanıtı beklenen biçimde değil.");
    }
    private static MarketplacePage<T> Page<T>(JsonElement root, T[] items, int page, int size)
    {
        var count = Number(root, "totalCount", "totalElements");
        return new(items, page, size, count.HasValue ? (long)count : null, count.HasValue ? (int)Math.Ceiling(count.Value / size) : null, null);
    }
    private static Dictionary<string, string> Paging(int page, int size)
    {
        if (page < 0 || size is < 1 or > 100 || (long)page * size > int.MaxValue) throw new IntegrationException("invalid_page", "Geçersiz sayfa.", 400);
        return new() { ["offset"] = ((long)page * size).ToString(CultureInfo.InvariantCulture), ["limit"] = size.ToString(CultureInfo.InvariantCulture) };
    }
    public async Task<MarketplaceAccount> TestAsync(ConnectionSettings s, CancellationToken ct) { await ProductsAsync(s, new(Size: 1), ct); return new(Marketplace, Merchant(s.Environment), s.Environment, "TR", [], DateTimeOffset.UtcNow); }
    public Task<MarketplaceAccount> AccountAsync(ConnectionSettings s, CancellationToken ct) => TestAsync(s, ct);
    public async Task<MarketplacePage<MarketplaceProduct>> ProductsAsync(ConnectionSettings s, ProductQuery q, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(q.Barcode)) throw new IntegrationException("unsupported_filter", "Hepsiburada ürünlerinde satıcı stok koduyla arayın. Barkodla tarama bu bağlantıda desteklenmiyor.", 400);
        if (q.Approval != "approved") throw new IntegrationException("unsupported_filter", "Bu liste mağazanın ilanlarını gösterir; katalog başvuru onayları ayrı bir süreçtir.", 400);
        var query = Paging(q.Page, q.Size);
        if (!string.IsNullOrWhiteSpace(q.StockCode)) query["merchantSkuList"] = q.StockCode;
        if (q.Status == "onSale") query["salable-listings"] = "true";
        else if (q.Status == "notOnSale") query["notsalable-listings"] = "true";
        else if (!string.IsNullOrWhiteSpace(q.Status)) throw new IntegrationException("unsupported_filter", "Desteklenmeyen Hepsiburada ilan durumu.", 400);
        using var doc = await Get(s.Environment, "listing", $"listings/merchantid/{Merchant(s.Environment)}", query, ct);
        var products = Rows(doc.RootElement).Select(e => new MarketplaceProduct(
            Text(e, "listingId", "id", "hepsiburadaSku"), Text(e, "hepsiburadaSku", "hbSku"), "", Text(e, "merchantSku"), Text(e, "barcode"),
            Text(e, "productName", "name") is { Length: > 0 } name ? name : Text(e, "merchantSku", "hepsiburadaSku"),
            Text(e, "categoryId"), Text(e, "categoryName"), Text(e, "brand"), Number(e, "listPrice"), Number(e, "price"), null,
            Number(e, "availableStock") is { } stock ? checked((int)stock) : null, "TRY", Flag(e, "isSalable"), Text(e, "productUrl"),
            Text(e, "imageUrl") is { Length: > 0 } image ? [image] : [], "listed", [], VatRate:Number(e,"vatRate"))).ToArray();
        return Page(doc.RootElement, products, q.Page, q.Size);
    }
    public async Task<MarketplacePage<MarketplaceOrder>> OrdersAsync(ConnectionSettings s, OrderQuery q, CancellationToken ct)
    {
        if (q.StartDate.HasValue != q.EndDate.HasValue || q.StartDate < 0 || q.EndDate > 253402300799999 || q.StartDate > q.EndDate || q.EndDate - q.StartDate > TimeSpan.FromDays(31).TotalMilliseconds)
            throw new IntegrationException("invalid_dates", "Tarihleri birlikte seçin; aralık en fazla 31 gün olabilir.", 400);
        if (q.Stream || !string.IsNullOrWhiteSpace(q.Barcode)) throw new IntegrationException("unsupported_filter", "Bu mağazada siparişleri durum veya sipariş numarasıyla arayın.", 400);
        if (q.Size > 50) throw new IntegrationException("invalid_page", "Hepsiburada sipariş sayfası en fazla 50 kayıt olabilir.", 400);
        var merchant = Merchant(s.Environment); var query = Paging(q.Page, q.Size);
        string path;
        if (!string.IsNullOrWhiteSpace(q.OrderNumber) && !System.Text.RegularExpressions.Regex.IsMatch(q.OrderNumber, @"^[A-Za-z0-9_-]{1,100}$"))
            throw new IntegrationException("invalid_order_number", "Sipariş numarası yalnızca harf, rakam, alt çizgi veya tire içerebilir.", 400);
        if (!string.IsNullOrWhiteSpace(q.OrderNumber)) { path = $"orders/merchantid/{merchant}/ordernumber/{Uri.EscapeDataString(q.OrderNumber)}"; query.Clear(); }
        else path = q.Status switch
        {
            null or "" or "Created" => $"orders/merchantid/{merchant}",
            "Awaiting" => $"orders/merchantid/{merchant}/paymentawaiting",
            "Picking" => $"packages/merchantid/{merchant}",
            "MissingInvoice" => $"packages/merchantid/{merchant}/missing-invoice",
            _ => throw new IntegrationException("unsupported_filter", "Bu Hepsiburada sipariş durumu henüz desteklenmiyor.", 400)
        };
        if (q.StartDate.HasValue && string.IsNullOrWhiteSpace(q.OrderNumber))
        {
            query["begindate"] = DateTimeOffset.FromUnixTimeMilliseconds(q.StartDate.Value).UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            query["enddate"] = DateTimeOffset.FromUnixTimeMilliseconds(q.EndDate!.Value).UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        using var doc = await Get(s.Environment, "orders", path, query, ct);
        var rows = !string.IsNullOrWhiteSpace(q.OrderNumber) && doc.RootElement.ValueKind == JsonValueKind.Object && Field(doc.RootElement,"orderNumber").ValueKind != JsonValueKind.Undefined ? new[] { doc.RootElement } : Rows(doc.RootElement);
        // The unpaid/paid endpoint pages line items, not whole orders. Do not group across page boundaries.
        return Page(doc.RootElement, rows.Select(e => MapOrder(e, q.Status)).ToArray(), q.Page, q.Size);
    }
    public static MarketplaceOrder MapOrder(JsonElement e, string? requestedStatus = null)
    {
        var inner = Field(e, "items", "lineItems");
        var lines = inner.ValueKind == JsonValueKind.Array ? inner.EnumerateArray().ToArray() : [e];
        var mapped = lines.Select(l => new MarketplaceOrderLine(Text(l, "id", "lineItemId"), Text(l, "merchantSku"), Text(l, "productBarcode", "barcode"), Text(l, "name", "productName"),
            checked((int)(Number(l, "quantity") ?? 0)), Number(l, "unitPrice", "price"), Number(l, "totalPrice"), Number(l, "vatRate"), "TRY", Text(l, "status"), Text(l, "categoryId"))).ToArray();
        var address = Field(e, "shippingAddress"); if (address.ValueKind != JsonValueKind.Object) address = e;
        var rawStatus = Text(e, "status");
        var status = rawStatus switch { "Open" => "Created", "Packaged" => "Picking", "InTransit" or "Intransit" => "Shipped", "CancelledByMerchant" or "CancelledByCustomer" or "CancelledBySap" => "Cancelled", "" => requestedStatus ?? "Created", _ => rawStatus };
        return new(Text(e, "packageNumber", "id"), Text(e, "orderNumber"), status, Date(e, "orderDate"), Text(e, "customerName", "recipientName"), Text(e, "email"),
            Number(e, "totalPrice", "totalAmount"), null, null, "TRY", "", "Shipment",
            new("", "Shipment", Text(address, "shippingCountryCode", "countryCode"), Text(address, "shippingCity", "city"), Text(address, "shippingTown", "town"), Text(address, "shippingPostalCode", "postalCode"), Text(address, "shippingAddressDetail", "address", "addressDetail"), false),
            mapped, new(Text(e, "cargoCompany"), Text(e, "trackingInfoCode", "barcode"), Text(e, "trackingInfoUrl"), "", Text(e, "packageNumber"), Number(e, "deci"), null),
            new(Text(e, "invoiceNumber"), Flag(e, "hasInvoice") switch { true => "Yüklendi", false => "Eksik", _ => "Bilinmiyor" }, "", false, Text(e, "companyName"), Text(e, "taxOffice"), Text(e, "taxNumber"), null, []));
    }
    public async Task<object> ClaimsAsync(ConnectionSettings s, ListingQuery q, CancellationToken ct)
    {
        var status = string.IsNullOrWhiteSpace(q.Status) ? "AwaitingAction" : q.Status;
        if (!new[] { "NewRequest", "AwaitingAction", "AwaitingPreApproval", "InDispute", "Accepted", "Rejected", "Refunded", "Cancelled" }.Contains(status)) throw new IntegrationException("invalid_status", "Geçersiz iade durumu.", 400);
        var query = Paging(q.Page, q.Size);
        using var doc = await Get(s.Environment, "orders", $"claims/merchantId/{Merchant(s.Environment)}/status/{status}", query, ct);
        var rows = Rows(doc.RootElement).Select(e => new { id = Text(e, "claimNumber", "number", "id"), orderNumber = Text(e, "orderNumber"), customerName = Text(e, "customerName"), reason = Text(e, "explanation", "reason"), status = Text(e, "status"), quantity = Number(e, "quantity"), claimDate = Date(e, "claimDate") }).ToArray();
        return Page(doc.RootElement, rows, q.Page, q.Size);
    }
}
