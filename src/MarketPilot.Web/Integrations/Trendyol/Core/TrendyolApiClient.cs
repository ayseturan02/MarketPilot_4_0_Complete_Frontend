using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MarketPilot.Web.Integrations.Trendyol;

public sealed class TrendyolCredentials
{
    public string SellerId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public bool AllowWrites { get; set; }
}

public sealed record TrendyolRawResponse(int Status, string ContentType, byte[] Body);

// Every Trendyol panel goes through this client: credentials, host, headers and error mapping live only here.
public sealed class TrendyolApiClient(HttpClient http, IConfiguration configuration)
{
    public const int MaxResponseBytes = 10 * 1024 * 1024;

    public static string Host(string environment) => environment == "stage" ? "stageapigw.trendyol.com" : "apigw.trendyol.com";

    public static bool IsConfigured(IConfiguration configuration, string environment)
    {
        var section = configuration.GetSection($"Marketplaces:Trendyol:{environment}");
        return long.TryParse(section["SellerId"], out var seller) && seller > 0 &&
            !string.IsNullOrWhiteSpace(section["ApiKey"]) && !string.IsNullOrWhiteSpace(section["ApiSecret"]);
    }

    public static bool WritesAllowed(IConfiguration configuration, string environment) =>
        IsConfigured(configuration, environment) && configuration.GetValue<bool>($"Marketplaces:Trendyol:{environment}:AllowWrites");

    public (string SellerId, string ApiKey, string ApiSecret) Credentials(string environment)
    {
        var credentials = configuration.GetSection($"Marketplaces:Trendyol:{environment}").Get<TrendyolCredentials>() ?? new();
        if (!long.TryParse(credentials.SellerId, out var seller) || seller <= 0 || string.IsNullOrWhiteSpace(credentials.ApiKey) || string.IsNullOrWhiteSpace(credentials.ApiSecret))
            throw new IntegrationException("configuration_missing", "Bu ortam için Trendyol Seller ID, API Key ve API Secret sunucuda yapılandırılmamış.", 409);
        return (seller.ToString(), credentials.ApiKey, credentials.ApiSecret);
    }

    public static string BuildUrl(string environment, string path, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var query = string.Join("&", parameters.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
        return $"https://{Host(environment)}/integration/{path}{(query.Length > 0 ? "?" + query : "")}";
    }

    /// <summary>Sends one request. When <paramref name="errorDetails"/> is true, Trendyol validation messages (not the raw body) are added to 4xx errors.</summary>
    public async Task<TrendyolRawResponse> SendAsync(string environment, HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>> parameters,
        HttpContent? content, IReadOnlyDictionary<string, string>? headers, bool errorDetails, CancellationToken ct)
    {
        var (seller, key, secret) = Credentials(environment);
        using var request = new HttpRequestMessage(method, BuildUrl(environment, path, parameters)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key}:{secret}")));
        request.Headers.TryAddWithoutValidation("User-Agent", $"{seller} - SelfIntegration");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        foreach (var header in headers ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(header.Key, header.Value == "{sellerId}" ? seller : header.Value);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await ReadLimitedAsync(response, ct);
        var code = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
            throw Error(code, errorDetails && code is >= 400 and < 500 and not 401 and not 403 ? ValidationMessages(body) : null);
        return new(code, response.Content.Headers.ContentType?.ToString() ?? "", body);
    }

    public async Task<JsonDocument> GetJsonAsync(string environment, string path, IEnumerable<KeyValuePair<string, string>> parameters, bool storeFrontHeader, CancellationToken ct)
    {
        var headers = storeFrontHeader ? new Dictionary<string, string> { ["storeFrontCode"] = "TR" } : null;
        var response = await SendAsync(environment, HttpMethod.Get, path, parameters, null, headers, false, ct);
        try { return JsonDocument.Parse(response.Body); }
        catch (JsonException) { throw new IntegrationException("invalid_response", "Trendyol geçersiz JSON yanıtı döndürdü."); }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) throw new IntegrationException("response_too_large", "Trendyol yanıtı 10 MB sınırını aşıyor.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static IntegrationException Error(int code, string? details)
    {
        var message = code switch
        {
            400 => "Trendyol isteği geçersiz buldu; filtreleri, tarih aralığını veya gönderilen alanları kontrol edin (400).",
            401 => "Trendyol kimlik doğrulaması başarısız. Seller ID, API Key ve API Secret değerlerini kontrol edin (401).",
            403 => "Trendyol erişimi reddetti. API yetkisini, User-Agent bilgisini ve ortam erişimini kontrol edin (403).",
            404 => "Trendyol istenen satıcı veya kaynağı bulamadı (404).",
            408 => "Trendyol isteği zaman aşımına uğradı (408).",
            409 => "Trendyol isteği mevcut kayıtla çakıştı (409).",
            415 => "Trendyol gönderilen içerik türünü kabul etmedi (415).",
            426 => "Trendyol bu API sürümünü artık kabul etmiyor (426).",
            429 => "Trendyol hız sınırına ulaşıldı. Belirtilen süre dolduktan sonra tekrar deneyin (429).",
            >= 500 => $"Trendyol servisinde geçici bir hata oluştu (HTTP {code}).",
            _ => $"Trendyol isteği başarısız (HTTP {code})."
        };
        if (!string.IsNullOrWhiteSpace(details)) message += " Trendyol: " + details;
        return new IntegrationException($"upstream_{code}", message, code is 400 or 401 or 403 or 404 or 409 or 429 ? code : 502);
    }

    // Only the human readable "message" fields are surfaced; the raw upstream body never reaches the client.
    private static string? ValidationMessages(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var messages = new List<string>();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                messages.AddRange(errors.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.ToString() : e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : ""));
            if (messages.Count == 0 && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var single) && single.ValueKind == JsonValueKind.String)
                messages.Add(single.GetString() ?? "");
            var text = string.Join(" | ", messages.Where(x => x.Length > 0).Take(5).Select(x => x.Length > 300 ? x[..300] : x));
            return text.Length > 0 ? text : null;
        }
        catch (JsonException) { return null; }
    }
}
