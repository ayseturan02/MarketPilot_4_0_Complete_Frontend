namespace MarketPilot.Web.Integrations.Trendyol;

// Sipariş statü webhook abonelikleri.
public static class TrendyolWebhooksPanel
{
    public const string Key = "webhooks";
    private const string Hooks = "webhook/sellers/{sellerId}/webhooks";
    private const string SampleWebhook = """{"url":"https://","authenticationType":"API_KEY","apiKey":"","subscribedStatuses":["CREATED","PICKING","INVOICED","SHIPPED","CANCELLED","DELIVERED"]}""";

    public static readonly TrendyolPanel Panel = new(Key, "Webhook", "Sipariş statüsü webhook aboneliklerini listeleme, yaratma, güncelleme, silme ve aktif/pasif alma.",
    [
        new("webhooks.list", Key, "Webhook listesi", "GET", Hooks) { Doc = "webhook-listeleme" },
        new("webhooks.create", Key, "Webhook yarat", "POST", Hooks)
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = SampleWebhook, Note = "authenticationType: API_KEY veya BASIC_AUTHENTICATION (username/password).", Doc = "webhook-yaratma" },
        new("webhooks.update", Key, "Webhook güncelle", "PUT", $"{Hooks}/{{id}}") { Write = true, Body = TrendyolBodyKind.Json, SampleBody = SampleWebhook, Doc = "webhook-güncelleme" },
        new("webhooks.delete", Key, "Webhook sil", "DELETE", $"{Hooks}/{{id}}") { Write = true, Doc = "webhook-silme" },
        new("webhooks.activate", Key, "Webhook aktif et", "PUT", $"{Hooks}/{{id}}/activate") { Write = true, Doc = "webhook-aktifpasif-alma" },
        new("webhooks.deactivate", Key, "Webhook pasif et", "PUT", $"{Hooks}/{{id}}/deactivate") { Write = true, Doc = "webhook-aktifpasif-alma" },
    ]);
}
