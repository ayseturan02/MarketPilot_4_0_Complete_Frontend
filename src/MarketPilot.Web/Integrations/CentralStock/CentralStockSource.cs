using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MarketPilot.Web.Integrations.CentralStock;

public sealed record SourceStockItem(string Sku, string Barcode, string Name, int Quantity);

// Client for the external stock application (ERP / warehouse software) that owns the real stock numbers.
// Contract: GET {BaseUrl}{StockPath}[?cursor=] -> [..] or { items:[{sku,barcode,name,quantity}], nextCursor }
//           POST {BaseUrl}{SalesPath} { requestId, channel, store, orderNumber, sku, barcode, quantity } (only when AllowWrites=true)
public sealed partial class CentralStockSource(IHttpClientFactory factory, IConfiguration configuration)
{
    public const string Section = "Marketplaces:StockSource";
    private const int MaxResponseBytes = 10 * 1024 * 1024, MaxPages = 200, MaxItems = 200_000;

    public static Uri? BaseUri(IConfiguration c)
    {
        if (!Uri.TryCreate(c[$"{Section}:BaseUrl"], UriKind.Absolute, out var uri)) return null;
        // Plain HTTP only when explicitly allowed (e.g. an ERP on the local network); the API key would otherwise travel unencrypted.
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && c.GetValue<bool>($"{Section}:AllowHttp"))) return null;
        return uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ? null : uri;
    }
    public static bool IsConfigured(IConfiguration c) => BaseUri(c) is not null && !string.IsNullOrWhiteSpace(c[$"{Section}:ApiKey"]) && !c[$"{Section}:ApiKey"]!.Any(char.IsControl);
    public static bool WritesAllowed(IConfiguration c) => IsConfigured(c) && c.GetValue<bool>($"{Section}:AllowWrites");

    public async Task<List<SourceStockItem>> ReadAsync(CancellationToken ct)
    {
        var items = new List<SourceStockItem>(); string? cursor = null;
        for (var page = 0; page < MaxPages; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(PathSetting("StockPath", "/stock")) + (cursor is null ? "" : "?cursor=" + Uri.EscapeDataString(cursor)));
            using var doc = JsonDocument.Parse(await SendAsync(request, ct));
            var root = doc.RootElement;
            var rows = root.ValueKind == JsonValueKind.Array ? root : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array ? list
                : throw new IntegrationException("invalid_response", "Stok uygulaması yanıtı beklenen yapıda değil (dizi veya items alanı).");
            foreach (var row in rows.EnumerateArray())
            {
                var sku = Text(row, "sku", "stockCode"); var barcode = Text(row, "barcode");
                if (sku.Length == 0 && barcode.Length == 0) continue;
                var quantity = Number(row, "quantity", "stock", "available");
                if (quantity is null || quantity < 0 || quantity != decimal.Truncate(quantity.Value) || quantity > 10_000_000)
                    throw new IntegrationException("invalid_response", $"Stok uygulaması {(sku.Length > 0 ? sku : barcode)} için geçersiz miktar döndürdü.");
                items.Add(new(sku, barcode, Text(row, "name", "title"), (int)quantity.Value));
                if (items.Count > MaxItems) throw new IntegrationException("response_too_large", "Stok uygulaması 200.000 kalem sınırını aşıyor.");
            }
            cursor = root.ValueKind == JsonValueKind.Object ? Text(root, "nextCursor") : "";
            if (cursor.Length == 0) return items;
            if (cursor.Length > 2048) throw new IntegrationException("invalid_response", "Stok uygulaması geçersiz sayfa imleci döndürdü.");
        }
        throw new IntegrationException("too_many_pages", "Stok uygulaması 200 sayfa sınırını aşıyor.");
    }

    public async Task ReportSaleAsync(CentralSale sale, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(PathSetting("SalesPath", "/stock/sales")))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { requestId = sale.Id, channel = sale.Marketplace, store = sale.StoreName, orderNumber = sale.OrderNumber, sku = sale.Sku, barcode = sale.Barcode, quantity = sale.Quantity }), Encoding.UTF8, "application/json")
        };
        // The same sale id is resent on retry, so the stock application can ignore duplicates.
        request.Headers.TryAddWithoutValidation("Idempotency-Key", sale.Id);
        await SendAsync(request, ct);
    }

    private string Url(string path) => BaseUri(configuration)!.AbsoluteUri.TrimEnd('/') + path;

    private string PathSetting(string name, string fallback)
    {
        var path = configuration[$"{Section}:{name}"] ?? fallback;
        if (!SafePath().IsMatch(path) || path.Contains("..") || path.Contains("//")) throw new IntegrationException("configuration_invalid", $"{Section}:{name} '/' ile başlayan basit bir yol olmalı.", 409);
        return path;
    }

    private async Task<byte[]> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!IsConfigured(configuration)) throw new IntegrationException("configuration_missing", "Stok uygulaması adresi ve API anahtarı sunucuda tanımlanmadı.", 409);
        var key = configuration[$"{Section}:ApiKey"]!; var header = configuration[$"{Section}:ApiKeyHeader"] ?? "X-Api-Key";
        if (!HeaderName().IsMatch(header)) throw new IntegrationException("configuration_invalid", $"{Section}:ApiKeyHeader geçersiz.", 409);
        if (header.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        else request.Headers.TryAddWithoutValidation(header, key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await factory.CreateClient("CentralStock").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var code = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
            throw new IntegrationException(code == 429 ? "rate_limited" : "source_error", code switch
            {
                401 or 403 => "Stok uygulaması API anahtarını reddetti.",
                404 => "Stok uygulamasında istenen servis yolu bulunamadı.",
                409 => "Stok uygulaması isteği mevcut kayıtla çakışma olarak reddetti.",
                429 => "Stok uygulaması hız sınırına ulaştı; sonraki çalıştırmada tekrar denenecek.",
                _ => $"Stok uygulaması isteği tamamlanamadı (HTTP {code})."
            }, code is 401 or 403 ? 409 : code == 429 ? 429 : 502);
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new IntegrationException("response_too_large", "Stok uygulaması yanıtı 10 MB sınırını aşıyor.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream(); var chunk = new byte[81920]; int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0) { if (buffer.Length + read > MaxResponseBytes) throw new IntegrationException("response_too_large", "Stok uygulaması yanıtı 10 MB sınırını aşıyor."); buffer.Write(chunk, 0, read); }
        return buffer.ToArray();
    }

    private static string Text(JsonElement e, params string[] keys) => keys.Select(k => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString().Trim() : "").FirstOrDefault(x => x.Length > 0) ?? "";
    private static decimal? Number(JsonElement e, params string[] keys) => keys.Select(k => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n) ? n : (decimal?)null).FirstOrDefault(x => x.HasValue);

    [GeneratedRegex(@"^/[A-Za-z0-9/_\-.]{0,200}$")] private static partial Regex SafePath();
    [GeneratedRegex(@"^[A-Za-z0-9-]{1,40}$")] private static partial Regex HeaderName();
}
