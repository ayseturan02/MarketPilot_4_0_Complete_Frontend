namespace MarketPilot.Web.Wms;

public static class WmsEndpoints
{
    public static void AddWmsProviders(this IServiceCollection services, IConfiguration configuration)
    {
        var profile = new WmsConnectionProfile(
            configuration["Wms:ProfileId"] ?? "mock-wms",
            configuration["Workspace:BusinessId"] ?? "default",
            configuration["Wms:ProviderKey"] ?? "mock",
            configuration["Wms:Mode"] ?? "mock",
            configuration.GetValue("Wms:Enabled", true),
            configuration.GetValue("Wms:AllowMockWrites", false));
        var delay = configuration.GetValue("Wms:Mock:DelayMilliseconds", 0);
        var failureOperation = configuration["Wms:Mock:FailureOperation"];

        services.AddSingleton(profile);
        services.AddSingleton(new WmsMockBehavior(delay, failureOperation));
        services.AddSingleton<IWmsProvider, MockWmsProvider>();
        services.AddSingleton<WmsInventoryService>();
    }

    public static void MapWmsProviders(this WebApplication app)
    {
        var group = app.MapGroup("/api/wms");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (http.Request.Method != "GET" &&
                http.RequestServices.GetRequiredService<MarketPilot.Web.Local.LocalAccountStore>().Configured &&
                !http.User.IsInRole("admin"))
                return Results.StatusCode(403);

            try
            {
                return await next(context);
            }
            catch (WmsProviderException error)
            {
                var status = error.Error.Code switch
                {
                    "NotSupported" => 501,
                    "Disabled" or "WriteDisabled" or "IdempotencyConflict" => 409,
                    "NotFound" => 404,
                    "SimulatedFailure" => 503,
                    _ => 400
                };
                return Results.Json(new { error = error.Error }, statusCode: status);
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
            catch (IOException)
            {
                return Results.Json(new { error = "WMS stok görünümü kaydedilemedi; önceki kayıtlar korundu." }, statusCode: 503);
            }
        });

        group.MapGet("", (IWmsProvider provider) =>
            Results.Ok(new { provider.Profile, provider.Capabilities, realAdapter = "provider-contract-pending" }));
        group.MapGet("/inventory", async (WmsInventoryService inventory, CancellationToken ct) =>
            Results.Ok(await inventory.ReadAsync(ct)));
        group.MapGet("/inventory/movements", (string? warehouseId, string? sku, int? size, WmsInventoryService inventory) =>
            Results.Ok(inventory.ReadMovements(warehouseId, sku, size ?? 250)));
        group.MapGet("/inventory/history", (string? warehouseId, string? sku, int? size, WmsInventoryService inventory) =>
            Results.Ok(inventory.ReadHistory(warehouseId, sku, size ?? 250)));
        group.MapPut("/inventory/policy", async (
            WmsInventoryPolicyUpdate update, WmsInventoryService inventory, CancellationToken ct) =>
            Results.Ok(await inventory.UpdatePolicyAsync(update, ct)));
        group.MapGet("/warehouses", async (IWmsProvider provider, CancellationToken ct) =>
            Results.Ok(await provider.ReadWarehousesAsync(ct)));
        group.MapGet("/warehouses/{warehouseId}/stock", async (string warehouseId, IWmsProvider provider, CancellationToken ct) =>
            Results.Ok(await provider.ReadStockAsync(warehouseId, ct)));
        group.MapGet("/warehouses/{warehouseId}/stock-movements", async (
            string warehouseId, DateTimeOffset? since, IWmsProvider provider, CancellationToken ct) =>
            Results.Ok(await provider.ReadStockMovementsAsync(warehouseId, since, ct)));
        group.MapPost("/fulfillment-orders", async (
            WmsFulfillmentOrder order, IWmsProvider provider, CancellationToken ct) =>
            Results.Ok(await provider.SubmitFulfillmentOrderAsync(order, ct)));
        group.MapGet("/fulfillment-orders/{id}/acceptance", async (
            string id, IWmsProvider provider, CancellationToken ct) =>
            Results.Ok(await provider.GetAcceptanceStatusAsync(id, ct)));
        group.MapGet("/fulfillment-orders/{id}/reservation", async (
            string id, IWmsProvider provider, CancellationToken ct) =>
            Results.Ok(await provider.GetReservationStatusAsync(id, ct)));
        group.MapGet("/fulfillment-orders/{id}/picking", async (
            string id, IWmsProvider provider, CancellationToken ct) =>
            Results.Ok(await provider.GetPickingStatusAsync(id, ct)));
        group.MapGet("/fulfillment-orders/{id}/packing", async (
            string id, IWmsProvider provider, CancellationToken ct) =>
            Results.Ok(await provider.GetPackingStatusAsync(id, ct)));
        group.MapGet("/fulfillment-orders/{id}/shipment", async (
            string id, IWmsProvider provider, CancellationToken ct) =>
            Results.Ok(await provider.GetShipmentStatusAsync(id, ct)));
        group.MapGet("/fulfillment-orders/{id}/eligibility", async (
            string id, string type, IWmsProvider provider, CancellationToken ct) =>
        {
            if (!Enum.TryParse<WmsRequestType>(type, true, out var requestType) || !Enum.IsDefined(requestType))
                return Results.BadRequest(new { error = "type Cancellation veya Return olmalıdır." });
            return Results.Ok(await provider.CheckCancellationOrReturnEligibilityAsync(id, requestType, ct));
        });
    }
}
