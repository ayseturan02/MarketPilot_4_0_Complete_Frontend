using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Trendyol;

namespace MarketPilot.Web.Webhooks;
public sealed record OrderHookBinding(string Id, ConnectionState Store, string SellerId, IConfigurationSection Settings);
public sealed partial class OrderWebhookReceiver(IConfiguration configuration, IntegrationRegistry registry, OrderWebhookInbox inbox)
{
    public const string Root = "Webhooks:Trendyol";
    public const int MaxBytes = 2 * 1024 * 1024;
    private string CallbackPath(string id)=>configuration["Workspace:BusinessId"] is { } business&&business!="default"&&MarketPilot.Web.Local.BusinessWorkspaces.ValidId(business)?"/hooks/businesses/"+business+"/orders/"+id:"/hooks/orders/"+id;
    private readonly object rateGate = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> windows = new();
    public OrderHookBinding? Binding(string id)
    {
        var readiness = Inspect(id);
        if (!readiness.Ready)
            return null;
        var settings = configuration.GetSection(Root + ":Endpoints:" + id);
        var store = registry.Connections[readiness.ConnectionId!];
        var seller = CredentialProfiles.For(configuration, store.Settings)[$"Marketplaces:Trendyol:{store.Settings.Environment}:SellerId"]!;
        return new(id, store, long.Parse(seller, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture), settings);
    }

    private static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
    private static bool Authorized(HttpRequest request, OrderHookBinding binding)
    {
        var s = binding.Settings;
        if ((s["AuthenticationType"] ?? "API_KEY") == "API_KEY")
        {
            var header = request.Headers["x-api-key"];
            if (header.Count != 1 || header[0] is not { } key || key.Length > 1024)
                return false;
            var primary = Equal(key, s["ApiKey"]!);
            var previous = s["PreviousApiKey"];
            var secondary = previous is { Length: >= 32 } && Equal(key, previous);
            return primary | secondary;
        }

        var auth = request.Headers.Authorization;
        if (auth.Count != 1 || auth[0]?.Length > 2048 || !AuthenticationHeaderValue.TryParse(auth[0], out var parsed) || !parsed.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
            return false;
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes(s["Username"] + ":" + s["Password"]));
        return Equal(parsed.Parameter ?? "", expected);
    }

    private bool Transport(HttpContext c) => c.Request.IsHttps || IPAddress.IsLoopback(c.Connection.RemoteIpAddress ?? IPAddress.None) && (configuration.GetValue<bool>(Root + ":AllowInsecureLoopback") || configuration.GetValue<bool>(Root + ":TrustLoopbackTlsProxy") && c.Request.Headers["X-Forwarded-Proto"].Count == 1 && c.Request.Headers["X-Forwarded-Proto"].ToString().Equals("https", StringComparison.OrdinalIgnoreCase));
    private bool Rate(string id)
    {
        var limit = Math.Clamp(configuration.GetValue<int?>(Root + ":RequestsPerMinute") ?? 6000, 1, 100000);
        lock (rateGate)
        {
            var now = DateTimeOffset.UtcNow;
            var v = windows.GetValueOrDefault(id);
            if (now - v.Start >= TimeSpan.FromMinutes(1))
                v = (now, 0);
            v.Count++;
            windows[id] = v;
            return v.Count <= limit;
        }
    }

    public async Task<IResult> Receive(string id, HttpContext context, CancellationToken ct)
    {
        var binding = Binding(id);
        if (binding == null)
            return Results.NotFound();
        if (!Transport(context))
            return Results.StatusCode(403);
        if (!Authorized(context.Request, binding))
            return Results.Unauthorized();
        context.Response.Headers.CacheControl = "no-store";
        if (!Rate(id.ToLowerInvariant()))
        {
            context.Response.Headers.RetryAfter = "60";
            return Results.StatusCode(429);
        }

        if (!context.Request.HasJsonContentType())
            return Results.StatusCode(415);
        if (context.Request.Headers.ContentEncoding.Count > 0)
            return Results.StatusCode(415);
        if (context.Request.ContentLength > MaxBytes)
            return Results.StatusCode(413);
        try
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (bytes.Length + read > MaxBytes)
                    return Results.StatusCode(413);
                bytes.Write(buffer, 0, read);
            }

            var items = OrderWebhookPayload.Parse(bytes.ToArray(), binding);
            var result = inbox.Accept(binding, items);
            return Results.Ok(result);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Geçersiz sipariş bildirimi." });
        }
        catch (IntegrationException e)
        {
            return Results.Json(new { error = e.Message, code = e.Code }, statusCode: e.Status);
        }
        catch (Exception e)when (e is IOException or UnauthorizedAccessException)
        {
            return Results.StatusCode(503);
        }
    }

    public object Status()
    {
        var endpoints = configuration.GetSection(Root + ":Endpoints").GetChildren().Select(s => Inspect(s.Key)).ToArray();
        return new
        {
            receiverImplemented = true,
            exampleCallbackPath = CallbackPath("receiver01"),
            configuredCount = endpoints.Length,
            readyCount = endpoints.Count(e => e.Ready),
            endpoints,
            inbox = inbox.Status()
        };
    }
}

public static class OrderWebhookPayload
{
    private static readonly Dictionary<string, string> Statuses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CREATED"] = "Created",
        ["PICKING"] = "Picking",
        ["INVOICED"] = "Invoiced",
        ["SHIPPED"] = "Shipped",
        ["CANCELLED"] = "Cancelled",
        ["DELIVERED"] = "Delivered",
        ["UNDELIVERED"] = "UnDelivered",
        ["RETURNED"] = "Returned",
        ["UNSUPPLIED"] = "UnSupplied",
        ["AWAITING"] = "Awaiting",
        ["UNPACKED"] = "UnPacked",
        ["ATCOLLECTIONPOINT"] = "AtCollectionPoint",
        ["VERIFIED"] = "Verified"
    };
    public sealed record Item(MarketplaceOrder Order, long Version, string Fingerprint);
    public static Item[] Parse(byte[] bytes, OrderHookBinding binding)
    {
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 48 });
        var root = doc.RootElement;
        RejectDuplicateFields(root);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array || content.GetArrayLength()is < 1 or > 100)
            throw Invalid("Sipariş bildirimi 1–100 paket içeren content listesi olmalı.");
        var items = new List<Item>();
        foreach (var e in content.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object)
                throw Invalid("Paket biçimi geçersiz.");
            var id = Text(e, "shipmentPackageId");
            if (id.Length == 0)
                id = Text(e, "id");
            var number = Text(e, "orderNumber");
            var status = Text(e, "shipmentPackageStatus");
            if (status.Length == 0)
                status = Text(e, "status");
            if (id.Length is < 1 or > 100 || number.Length is < 1 or > 100 || !Statuses.TryGetValue(status.Replace("_", ""), out var normalized))
                throw Invalid("Paket kimliği, sipariş numarası veya durum geçersiz.");
            foreach (var field in new[]
            {
                "supplierId",
                "sellerId"
            }

            )
                if (Text(e, field)is { Length: > 0 } seller && seller != binding.SellerId)
                    throw Invalid("Paket bu satıcıya ait değil.");
            if (!e.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array || lines.GetArrayLength() > 1000)
                throw Invalid("Sipariş satırları geçersiz.");
            foreach (var line in lines.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.Object || !line.TryGetProperty("quantity", out var q) || q.ValueKind != JsonValueKind.Number || !q.TryGetInt32(out var quantity) || quantity < 0)
                    throw Invalid("Sipariş satır adedi geçersiz.");
                if (Text(line, "sellerId")is { Length: > 0 } seller && seller != binding.SellerId)
                    throw Invalid("Sipariş satırı bu satıcıya ait değil.");
            }

            var version = Timestamp(e, "lastModifiedDate");
            if (version == null && e.TryGetProperty("packageHistories", out var h) && h.ValueKind == JsonValueKind.Array)
                version = h.EnumerateArray().Select(x => Timestamp(x, "createdDate")).Max();
            if (version is null or <= 0 or > 253402300799999 || version > DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeMilliseconds())
                throw Invalid("Paketin son değişiklik zamanı bildirilmedi.");
            var order = TrendyolAdapter.MapOrder(e)with
            {
                PackageId = id,
                Status = normalized,
                LastModifiedDate = version
            };
            using var canonical = new MemoryStream();
            using (var writer = new Utf8JsonWriter(canonical))
                Canonical(writer, e);
            items.Add(new(order, version.Value, Convert.ToHexString(SHA256.HashData(canonical.ToArray())).ToLowerInvariant()));
        }

        return items.ToArray();
    }

    private static string Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";
    private static long? Timestamp(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    private static IntegrationException Invalid(string message) => new("invalid_webhook", message, 400);
    private static void RejectDuplicateFields(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject())
            {
                if (!keys.Add(p.Name))
                    throw Invalid("Tekrarlanan JSON alanı var.");
                RejectDuplicateFields(p.Value);
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var v in e.EnumerateArray())
                RejectDuplicateFields(v);
    }

    private static void Canonical(Utf8JsonWriter writer, JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(p.Name);
                Canonical(writer, p.Value);
            }

            writer.WriteEndObject();
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var v in e.EnumerateArray())
                Canonical(writer, v);
            writer.WriteEndArray();
        }
        else
            e.WriteTo(writer);
    }
}
