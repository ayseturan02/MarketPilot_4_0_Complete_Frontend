using MarketPilot.Web.Local;
using MarketPilot.Web.Integrations.CentralStock;
namespace MarketPilot.Web.Synchronization;

public static class SynchronizationEndpoints
{
    public static void AddBackgroundSynchronization(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<StockOutbox>(); services.AddHostedService<StockOutboxWorker>();
        services.AddSingleton<OrderSynchronization>(); services.AddHostedService<OrderSynchronizationWorker>();
    }
    public static void MapBackgroundSynchronization(this WebApplication app)
    {
        app.MapGet("/api/sync/automation", (OrderSynchronization automation, StockOutbox outbox) => Results.Ok(new { automation = automation.Status(), outbox = outbox.Status() }));
        app.MapPost("/api/sync/automation", (AutomationSettings input, HttpContext context, LocalAccountStore accounts, OrderSynchronization automation, CentralStockService stock) => {
            if (accounts.Configured && !context.User.IsInRole("admin")) return Results.StatusCode(403);
            if(context.RequestServices.GetRequiredService<MarketPilot.Web.Enterprise.EnterpriseSync>().Managed)return Results.Conflict(new{error="Zaman planı Senkronizasyon Merkezi’nden yönetilir."});
            if(input.IntervalSeconds is <30 or >3600 || input.LookbackDays is <1 or >14) return Results.BadRequest(new { error = "Aralık 30–3600 saniye, sipariş taraması 1–14 gün olmalı." });
            try {
                if (input.Enabled && stock.Settings.AutoSync) stock.UpdateSettings(stock.Settings with { AutoSync = false });
                automation.Configure(input);
                return Results.Ok(new { message = input.Enabled ? "Zamanlanmış sipariş okuma ve seçilen stok otomasyonu etkin." : "Zamanlanmış sipariş ve stok taraması durduruldu. Daha önce kabul edilmiş gönderimler işlemeye devam eder.", automation = automation.Status() });
            } catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
            catch (IOException) { return Results.Json(new { error = "Otomasyon ayarları kaydedilemedi." }, statusCode: 503); }
        });
        app.MapPost("/api/sync/outbox/{id}/retry", (string id, HttpContext context, LocalAccountStore accounts, StockOutbox outbox) => {
            if (accounts.Configured && !context.User.IsInRole("admin")) return Results.StatusCode(403);
            try { outbox.Retry(id); return Results.Ok(new { ok = true }); }
            catch (MarketPilot.Web.Integrations.IntegrationException e) { return Results.Json(new { error = e.Message }, statusCode: e.Status); }
        });
    }
}
