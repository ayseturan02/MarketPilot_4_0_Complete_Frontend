using System.Text.RegularExpressions;

namespace MarketPilot.Web.Webhooks;

public sealed record OrderHookReadiness(string Id, bool Enabled, bool Ready, string? ConnectionId,
    string? ConnectionName, string? Environment, string AuthenticationType, string CallbackPath,
    string? CallbackUrl, bool PublicUrlReady, string[] Issues, string[] DeliveryIssues);

public sealed partial class OrderWebhookReceiver
{
    public OrderHookReadiness Inspect(string id)
    {
        var settings = configuration.GetSection(Root + ":Endpoints:" + id);
        var issues = new List<string>();
        var deliveryIssues = new List<string>();
        var enabled = bool.TryParse(settings["Enabled"], out var selected) && selected;
        var connectionId = settings["ConnectionId"];
        registry.Connections.TryGetValue(connectionId ?? "", out var store);
        var type = settings["AuthenticationType"] ?? "API_KEY";
        var validId = Regex.IsMatch(id, "^[a-zA-Z0-9_-]{8,80}$") &&
            !new[] { "trendyol", "dolap", "localhost" }.Any(word => id.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (!validId) issues.Add("Alıcı kimliği 8–80 harf/rakam/tire/alt çizgi olmalı; sağlayıcı adı veya localhost içeremez.");
        if (!enabled) issues.Add("Alıcı etkinleştirilmemiş.");
        if (store is null) issues.Add("Alıcının mağaza bağlantısı bulunamadı.");
        else
        {
            if (store.Settings.Mode != "real" || store.Settings.Marketplace != "trendyol")
                issues.Add("Alıcı gerçek Trendyol bağlantısına bağlanmalı; demo mağazalar dış bildirim kabul etmez.");
            if (store.Settings.Environment is not ("stage" or "production")) issues.Add("Mağazanın ortamı geçersiz.");
            try
            {
                if (!long.TryParse(MarketPilot.Web.Integrations.CredentialProfiles.For(configuration,store.Settings)[$"Marketplaces:Trendyol:{store.Settings.Environment}:SellerId"], out var seller) || seller < 1)
                    issues.Add("Bu mağazanın satıcı numarası tanımlanmamış veya geçersiz.");
            } catch (MarketPilot.Web.Integrations.IntegrationException e) { issues.Add(e.Message); }
        }
        static bool ValidSecret(string? value) => value is { Length: >= 32 and <= 1024 } && !value.Any(char.IsControl);
        if (type == "API_KEY" && !ValidSecret(settings["ApiKey"])) issues.Add("Alıcıya ait bağımsız API anahtarı eksik veya geçersiz.");
        if (type == "BASIC_AUTHENTICATION")
        {
            var username = settings["Username"];
            if (string.IsNullOrWhiteSpace(username) || username.Length > 200 || username.Contains(':') || username.Any(char.IsControl))
                issues.Add("Basic kimlik doğrulaması kullanıcı adı eksik veya geçersiz.");
            if (!ValidSecret(settings["Password"])) issues.Add("Basic kimlik doğrulaması parolası eksik veya geçersiz.");
        }
        if (type is not ("API_KEY" or "BASIC_AUTHENTICATION")) issues.Add("Kimlik doğrulama yöntemi geçersiz.");
        var path = CallbackPath(id);
        string? publicUrl = null;
        var baseUrl = configuration[Root + ":PublicBaseUrl"];
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin) && origin.Scheme == "https" &&
            !origin.IsLoopback && origin.UserInfo.Length == 0 && origin.Query.Length == 0 && origin.Fragment.Length == 0 &&
            origin.AbsolutePath == "/" && !new[] { "trendyol", "dolap", "localhost" }.Any(word => origin.Host.Contains(word, StringComparison.OrdinalIgnoreCase)) && validId)
            publicUrl = origin.GetLeftPart(UriPartial.Authority) + path;
        else deliveryIssues.Add("Dışarıdan erişilebilen HTTPS bildirim adresi tanımlanmamış. Yerel adres canlı abonelik için kullanılamaz.");
        return new(id, enabled, issues.Count == 0, connectionId, store?.Settings.Name, store?.Settings.Environment,
            type is "API_KEY" or "BASIC_AUTHENTICATION" ? type : "INVALID", path, publicUrl,
            publicUrl is not null, issues.ToArray(), deliveryIssues.ToArray());
    }
}
