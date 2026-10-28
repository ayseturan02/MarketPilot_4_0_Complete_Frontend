using System.Text.Json;

namespace MarketPilot.Web.Integrations.Trendyol;

// Trendyol'a ait tüm DI kayıtları ve /api/integrations/trendyol uçları burada; diğer pazaryerleri kendi klasörlerinde kayıt olur.
public static class TrendyolEndpoints
{
    public static void AddTrendyolIntegration(this IServiceCollection services)
    {
        services.AddHttpClient<TrendyolAdapter>(client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient<TrendyolApiClient>(client => client.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<TrendyolOperationRunner>();
        services.AddTransient<IMarketplaceAdapter>(sp => sp.GetRequiredService<TrendyolAdapter>());
    }

    public static void MapTrendyolApi(this RouteGroupBuilder integrations)
    {
        var group = integrations.MapGroup("/trendyol");

        group.MapGet("/operations", (IConfiguration configuration) => Results.Ok(new
        {
            environments = new[] { "stage", "production" }.ToDictionary(e => e, e => new
            {
                configured = TrendyolApiClient.IsConfigured(configuration, e),
                writes = TrendyolApiClient.WritesAllowed(configuration, e)
            }),
            panels = TrendyolCatalog.Panels
        }));

        group.MapPost("/connections/{id}/operations/{key}", async (string id, string key, TrendyolOperationRequest? request,
            IntegrationRegistry registry, MarketplaceClients clients, CancellationToken ct) =>
        {
            if (!registry.Connections.TryGetValue(id, out var state) || state.Settings.Marketplace != "trendyol")
                return Results.NotFound(new { error = "Trendyol mağazası bulunamadı." });
            if (!TrendyolCatalog.Operations.TryGetValue(key, out var operation))
                return Results.NotFound(new { error = "Trendyol servisi bulunamadı." });
            var gate = registry.Gates.GetOrAdd(id, _ => new(1, 1));
            if (!await gate.WaitAsync(0, ct)) return Results.Json(new { error = "Bu mağazada bir işlem zaten sürüyor." }, statusCode: 409);
            try
            {
                var result = await clients.Trendyol(state.Settings).RunAsync(operation, state.Settings, request ?? new(), ct);
                var updated = state with
                {
                    Status = state.Settings.Mode == "demo" ? "demo" : result.Sent ? "connected" : state.Status,
                    LastOperation = DateTimeOffset.UtcNow, LastAction = operation.Key, ErrorCode = null, LastCount = null,
                    Message = result.Sent ? $"{operation.Title}: Trendyol HTTP {result.Status}." : $"{operation.Title}: istek gönderilmedi (önizleme)."
                };
                registry.Connections[id] = updated; registry.Save();
                return Results.Ok(new { connection = updated, result });
            }
            catch (Exception ex) when (ex is IntegrationException or HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
            {
                var error = ex as IntegrationException ?? new IntegrationException(ex is OperationCanceledException ? "timeout" : "request_failed",
                    ex is OperationCanceledException ? "API isteği zaman aşımına uğradı veya iptal edildi." : "Trendyol isteği tamamlanamadı; bağlantıyı ve gönderilen alanları kontrol edin.");
                var updated = error.Status == 400 && error.Code is "invalid_request" or "stage_only" ? state
                    : state with { Status = "error", LastOperation = DateTimeOffset.UtcNow, LastAction = operation.Key, ErrorCode = error.Code, Message = error.Message, LastCount = null };
                registry.Connections[id] = updated; registry.Save();
                return Results.Json(new { error = error.Message, code = error.Code, connection = updated }, statusCode: error.Status);
            }
            finally { gate.Release(); }
        });
    }
}
