using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MarketPilot.Web.Integrations.Amazon;

// Tokens are held in memory only. Only the fixed EU and LWA hosts can receive requests.
public sealed partial class AmazonApiClient(IHttpClientFactory factory, IConfiguration configuration)
{
    public const string MarketplaceId = "A33AVAJ2PDY3EV";
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset Until, string Fingerprint)> tokens = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> tokenGates = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> cooldowns = new();
    public static bool IsConfigured(IConfiguration c, string environment) => environment is "stage" or "production" &&
        new[] { "SellerId", "ClientId", "ClientSecret", "RefreshToken" }.All(k => c[$"Marketplaces:Amazon:{environment}:{k}"] is { Length: > 0 } v && !string.IsNullOrWhiteSpace(v) && !v.Any(char.IsControl));
    public string Seller(string environment)
    {
        if (!IsConfigured(configuration, environment)) throw new IntegrationException("configuration_missing", "Amazon mağaza bağlantısı henüz tanımlanmadı.", 409);
        return configuration[$"Marketplaces:Amazon:{environment}:SellerId"]!;
    }
    public static string Host(string environment) => environment switch { "production" => "sellingpartnerapi-eu.amazon.com", "stage" => "sandbox.sellingpartnerapi-eu.amazon.com", _ => throw new IntegrationException("invalid_environment", "Geçersiz Amazon ortamı.", 400) };
    private async Task<string> Token(string environment, CancellationToken ct)
    {
        Seller(environment);
        var section = configuration.GetSection($"Marketplaces:Amazon:{environment}");
        var credentials = string.Join('|', new[] { section["ClientId"], section["ClientSecret"], section["RefreshToken"] });
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(credentials)));
        var gate = tokenGates.GetOrAdd(environment, _ => new(1, 1)); await gate.WaitAsync(ct);
        try
        {
            if (tokens.TryGetValue(environment, out var saved) && saved.Until > DateTimeOffset.UtcNow && saved.Fingerprint == fingerprint) return saved.Value;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.amazon.com/auth/o2/token") { Content = new FormUrlEncodedContent(new Dictionary<string,string> { ["grant_type"] = "refresh_token", ["refresh_token"] = section["RefreshToken"]!, ["client_id"] = section["ClientId"]!, ["client_secret"] = section["ClientSecret"]! }) };
            using var response = await factory.CreateClient("AmazonSpApi").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new IntegrationException("authorization_failed", "Amazon yetkilendirmesi doğrulanamadı. Mağaza yetkisini kontrol edin.", 409);
            using var json = JsonDocument.Parse(await Read(response, ct));
            var value = Aj.S(json.RootElement, "access_token"); var ttl = Aj.N(json.RootElement, "expires_in");
            if (value.Length is 0 or > 4096 || value.Any(char.IsControl) || ttl is null or <= 60 || Aj.S(json.RootElement,"token_type").ToLowerInvariant() != "bearer") throw new IntegrationException("invalid_token", "Amazon yetkilendirme yanıtı okunamadı.");
            tokens[environment] = (value, DateTimeOffset.UtcNow.AddSeconds(Math.Min((double)ttl.Value - 60, 3540)), fingerprint); return value;
        }
        finally { gate.Release(); }
    }
    public Task<JsonDocument> Get(string environment, string path, Dictionary<string,string> query, CancellationToken ct) => Send(environment, path, query, null, ct);
    private async Task<JsonDocument> Send(string environment, string path, Dictionary<string,string> query, HttpContent? patch, CancellationToken ct)
    {
        Seller(environment);
        if (!path.StartsWith('/') || path.Contains('?') || path.Contains("..")) throw new IntegrationException("invalid_path", "Geçersiz Amazon işlemi.", 400);
        var key = environment+":"+path.Split('/')[1];
        if (cooldowns.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow) throw new IntegrationException("rate_limited", "Amazon istek sınırı bekleniyor. Biraz sonra yeniden deneyin.", 429);
        var token = await Token(environment, ct);
        var qs = string.Join('&', query.Select(p=>Uri.EscapeDataString(p.Key)+"="+Uri.EscapeDataString(p.Value)));
        using var request = new HttpRequestMessage(patch is null ? HttpMethod.Get : HttpMethod.Patch, $"https://{Host(environment)}{path}{(qs.Length>0?"?"+qs:"")}") { Content = patch };
        request.Headers.TryAddWithoutValidation("x-amz-access-token",token);
        request.Headers.TryAddWithoutValidation("x-amz-date",DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ",CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("User-Agent","InternetTekstil/5.10 (Language=CSharp)");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await factory.CreateClient("AmazonSpApi").SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if (!response.IsSuccessStatusCode)
        {
            var code=(int)response.StatusCode;
            if(code==401)tokens.TryRemove(environment,out _);
            if(code==429){var delay=response.Headers.RetryAfter?.Delta??TimeSpan.FromSeconds(30);cooldowns[key]=DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(delay.TotalSeconds,1,300));}
            throw new IntegrationException(code==429?"rate_limited":"amazon_error",code switch { 401 or 403=>"Amazon bu işleme erişim vermedi. Mağaza yetkilerini kontrol edin.",404=>"Amazon kaydı bulunamadı.",429=>"Amazon istek sınırına ulaşıldı. Biraz sonra yeniden deneyin.",_=>"Amazon verileri alınamadı. Daha sonra yeniden deneyin."},code is 401 or 403?409:code is 400 or 404 or 409 or 422 or 429?(code==422?400:code):502);
        }
        return JsonDocument.Parse(await Read(response,ct));
    }
    private static async Task<byte[]> Read(HttpResponseMessage response,CancellationToken ct)
    {
        const int limit=10*1024*1024;
        if(response.Content.Headers.ContentLength>limit)throw new IntegrationException("response_large","Amazon yanıtı boyut sınırını aştı.");
        await using var stream=await response.Content.ReadAsStreamAsync(ct);using var output=new MemoryStream();var bytes=new byte[8192];int n;
        while((n=await stream.ReadAsync(bytes,ct))>0){if(output.Length+n>limit)throw new IntegrationException("response_large","Amazon yanıtı boyut sınırını aştı.");output.Write(bytes,0,n);}return output.ToArray();
    }
}
internal static class Aj
{
    public static JsonElement F(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var v)?v:default;
    public static string S(JsonElement e,string key)=>F(e,key).ValueKind is JsonValueKind.String or JsonValueKind.Number?F(e,key).ToString():"";
    public static decimal? N(JsonElement e,string key)=>decimal.TryParse(F(e,key).ToString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var n)?n:null;
    public static void RequireArray(JsonElement e,string key){if(F(e,key).ValueKind!=JsonValueKind.Array)throw new IntegrationException("invalid_response","Amazon liste yanıtı beklenen biçimde değil.");}
    public static JsonElement[] A(JsonElement e,string key)=>F(e,key).ValueKind==JsonValueKind.Array?F(e,key).EnumerateArray().ToArray():[];
    public static DateTimeOffset? D(JsonElement e,string key)=>DateTimeOffset.TryParse(S(e,key),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var d)?d:null;
    public static decimal? Money(JsonElement e)=>N(e,"amount")??N(e,"currencyAmount");
}
