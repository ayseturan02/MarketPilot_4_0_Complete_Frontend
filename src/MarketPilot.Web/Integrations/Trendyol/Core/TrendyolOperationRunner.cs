using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MarketPilot.Web.Integrations.Trendyol;

public sealed record TrendyolFileInput(string Field, string FileName, string ContentType, string Base64);
public sealed record TrendyolOperationRequest(
    Dictionary<string, string>? Path = null, Dictionary<string, string>? Query = null, JsonElement? Body = null,
    Dictionary<string, string>? Form = null, List<TrendyolFileInput>? Files = null, bool Confirm = false);
public sealed record TrendyolOperationResult(
    string Operation, bool Sent, bool DryRun, string? Reason, string Method, string Url,
    int? Status = null, string? ContentType = null, JsonElement? Data = null, string? Text = null, string? Base64 = null, object? RequestBody = null);

// Validates a catalog operation request and either sends it to Trendyol or returns a dry-run preview.
public sealed partial class TrendyolOperationRunner(TrendyolApiClient client, IConfiguration configuration)
{
    private const int MaxFileBytes = 10 * 1024 * 1024, MaxTotalFileBytes = 20 * 1024 * 1024, MaxJsonChars = 5 * 1024 * 1024;
    private static readonly HashSet<string> FileTypes = new(StringComparer.OrdinalIgnoreCase) { "application/pdf", "image/jpeg", "image/png", "image/webp" };

    public async Task<TrendyolOperationResult> RunAsync(TrendyolOperation op, ConnectionSettings settings, TrendyolOperationRequest request, CancellationToken ct)
    {
        if(settings.Mode!="demo"&&op.Write&&request.Body is {} body&&MarketPilot.Web.Pricing.RealPriceWrites.HasPrice(body))MarketPilot.Web.Pricing.RealPriceWrites.Deny(1);
        var environment = settings.Environment;
        if (op.StageOnly && environment != "stage") throw Invalid("Bu servis yalnızca stage (test) ortamında çalışır.", "stage_only");
        var demo = settings.Mode == "demo";
        var seller = demo ? "DEMO" : TrySeller(environment);
        var path = ResolvePath(op, request.Path, seller);
        var query = ResolveQuery(request.Query);
        var (content, preview) = BuildContent(op, request);
        var url = TrendyolApiClient.BuildUrl(environment, path, query);

        string? reason = demo ? "Demo mod: istek Trendyol'a gönderilmedi; yalnızca hazırlanan istek gösteriliyor."
            : op.Write && !TrendyolApiClient.WritesAllowed(configuration, environment) ? $"Yazma servisleri bu ortamda kapalı. Açmak için sunucuda Marketplaces:Trendyol:{environment}:AllowWrites=true tanımlayın."
            : op.Write && !request.Confirm ? "Yazma işlemi kullanıcı onayı olmadan gönderilmez."
            : null;
        if (reason is not null)
        {
            content?.Dispose();
            return new(op.Key, false, true, reason, op.Method, url, RequestBody: preview);
        }

        var headers = new Dictionary<string, string>();
        if (op.StoreFrontHeader) headers["storeFrontCode"] = "TR";
        if (op.SellerIdHeader) headers["SellerID"] = "{sellerId}";
        using (content)
        {
            var response = await client.SendAsync(environment, new HttpMethod(op.Method), path, query, content, headers, true, ct);
            var (data, text, base64) = Decode(response);
            return new(op.Key, true, false, null, op.Method, url, response.Status, response.ContentType, data, text, base64, preview);
        }
    }

    private string TrySeller(string environment)
    {
        try { return client.Credentials(environment).SellerId; }
        catch (IntegrationException) { return "{sellerId}"; }
    }

    private static string ResolvePath(TrendyolOperation op, Dictionary<string, string>? values, string seller)
    {
        var path = op.Path.Replace("{sellerId}", seller);
        foreach (var name in op.PathParameters)
        {
            var value = values?.GetValueOrDefault(name)?.Trim() ?? "";
            // "." and ".." survive EscapeDataString and would be normalised into a different Trendyol endpoint.
            if (!PathValue().IsMatch(value) || value.All(c => c == '.')) throw Invalid($"'{name}' alanı zorunludur ve yalnızca harf, rakam, nokta, iki nokta, alt çizgi veya tire içerebilir (en fazla 120 karakter).");
            path = path.Replace("{" + name + "}", Uri.EscapeDataString(value));
        }
        return path;
    }

    private static List<KeyValuePair<string, string>> ResolveQuery(Dictionary<string, string>? values)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var (name, raw) in values ?? new Dictionary<string, string>())
        {
            var value = raw?.Trim() ?? "";
            if (value.Length == 0) continue;
            if (!FieldName().IsMatch(name) || value.Length > 2000) throw Invalid($"Geçersiz sorgu parametresi: {(name.Length > 50 ? name[..50] : name)}.");
            result.Add(new(name, value));
        }
        if (result.Count > 40) throw Invalid("En fazla 40 sorgu parametresi gönderilebilir.");
        return result;
    }

    private static (HttpContent? Content, object? Preview) BuildContent(TrendyolOperation op, TrendyolOperationRequest request)
    {
        var hasJson = request.Body is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null };
        var hasForm = request.Form is { Count: > 0 } || request.Files is { Count: > 0 };
        if (op.Body != TrendyolBodyKind.Json && hasJson || op.Body != TrendyolBodyKind.Multipart && hasForm)
            throw Invalid("Bu servis bu türde istek gövdesi kabul etmiyor.");
        if (op.Body == TrendyolBodyKind.Json && hasJson)
        {
            var raw = request.Body!.Value.GetRawText();
            if (raw.Length > MaxJsonChars) throw Invalid("JSON gövdesi 5 MB sınırını aşıyor.");
            return (new StringContent(raw, Encoding.UTF8, "application/json"), request.Body.Value);
        }
        if (op.Body != TrendyolBodyKind.Multipart || !hasForm) return (null, null);

        var form = request.Form ?? new Dictionary<string, string>();
        var files = request.Files ?? new List<TrendyolFileInput>();
        if (form.Count > 20 || files.Count > 5) throw Invalid("En fazla 20 form alanı ve 5 dosya gönderilebilir.");
        var multipart = new MultipartFormDataContent();
        var preview = new List<object>();
        try
        {
            foreach (var (name, raw) in form)
            {
                var value = raw?.Trim() ?? "";
                if (value.Length == 0) continue;
                if (!FieldName().IsMatch(name) || value.Length > 2000) throw Invalid($"Geçersiz form alanı: {(name.Length > 50 ? name[..50] : name)}.");
                multipart.Add(new StringContent(value, Encoding.UTF8), name);
            }
            long total = 0;
            foreach (var file in files)
            {
                if (!FieldName().IsMatch(file.Field ?? "")) throw Invalid("Geçersiz dosya alanı adı.");
                if (!FileTypes.Contains(file.ContentType ?? "")) throw Invalid("Dosya türü PDF, JPEG, PNG veya WEBP olmalıdır.");
                byte[] bytes;
                try { bytes = Convert.FromBase64String(file.Base64 ?? ""); }
                catch (FormatException) { throw Invalid("Dosya içeriği okunamadı."); }
                total += bytes.Length;
                if (bytes.Length == 0 || bytes.Length > MaxFileBytes || total > MaxTotalFileBytes) throw Invalid("Her dosya 10 MB, toplam dosya boyutu 20 MB sınırını aşamaz.");
                var name = SafeFileName(file.FileName);
                var part = new ByteArrayContent(bytes);
                part.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType!);
                multipart.Add(part, file.Field!, name);
                preview.Add(new { field = file.Field, fileName = name, contentType = file.ContentType, size = bytes.Length });
            }
            return (multipart, new { form = form.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToDictionary(), files = preview });
        }
        catch
        {
            multipart.Dispose();
            throw;
        }
    }

    private static (JsonElement? Data, string? Text, string? Base64) Decode(TrendyolRawResponse response)
    {
        if (response.Body.Length == 0) return (null, null, null);
        var type = response.ContentType.ToLowerInvariant();
        // Skip whitespace and UTF-8 BOM, then sniff for '{', '[' or '"'.
        var first = response.Body.SkipWhile(b => b is 32 or 13 or 10 or 9 or 0xEF or 0xBB or 0xBF).FirstOrDefault();
        if (type.Contains("json") || first is 123 or 91 or 34)
        {
            try { using var doc = JsonDocument.Parse(response.Body); return (doc.RootElement.Clone(), null, null); }
            catch (JsonException) { }
        }
        if (type.StartsWith("text/") || type.Length == 0 && response.Body.All(b => b >= 9))
            return (null, Encoding.UTF8.GetString(response.Body), null);
        return (null, null, Convert.ToBase64String(response.Body));
    }

    private static string SafeFileName(string? value)
    {
        var name = System.IO.Path.GetFileName(value ?? "").Trim();
        name = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        return name.Length is > 0 and <= 120 ? name : "dosya";
    }

    private static IntegrationException Invalid(string message, string code = "invalid_request") => new(code, message, 400);

    [GeneratedRegex(@"^[A-Za-z0-9._:\-]{1,120}$")]
    private static partial Regex PathValue();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,49}$")]
    private static partial Regex FieldName();
}
