using MarketPilot.Web.Wms;

// Simulates a provider returning a successful reservation for the wrong warehouse.
sealed class MismatchedReservationProvider(IWmsProvider inner) : IWmsProvider
{
    public WmsConnectionProfile Profile => inner.Profile;
    public WmsCapabilities Capabilities => inner.Capabilities;
    public Task<WmsResult<IReadOnlyList<WmsWarehouse>>> ReadWarehousesAsync(CancellationToken ct = default) => inner.ReadWarehousesAsync(ct);
    public Task<WmsResult<IReadOnlyList<WmsStockSnapshot>>> ReadStockAsync(string id, CancellationToken ct = default) => inner.ReadStockAsync(id, ct);
    public Task<WmsResult<IReadOnlyList<WmsStockMovement>>> ReadStockMovementsAsync(string id, DateTimeOffset? since = null, CancellationToken ct = default) => inner.ReadStockMovementsAsync(id, since, ct);
    public Task<WmsResult<WmsFulfillmentAcceptance>> SubmitFulfillmentOrderAsync(WmsFulfillmentOrder order, CancellationToken ct = default) => inner.SubmitFulfillmentOrderAsync(order, ct);
    public Task<WmsResult<WmsFulfillmentAcceptance>> GetAcceptanceStatusAsync(string id, CancellationToken ct = default) => inner.GetAcceptanceStatusAsync(id, ct);
    public async Task<WmsResult<WmsReservation>> GetReservationStatusAsync(string id, CancellationToken ct = default)
    {
        var response = await inner.GetReservationStatusAsync(id, ct);
        return response with { Data = response.Data with { WarehouseId = "wrong-warehouse" } };
    }
    public Task<WmsResult<WmsFulfillmentProgress>> GetPickingStatusAsync(string id, CancellationToken ct = default) => inner.GetPickingStatusAsync(id, ct);
    public Task<WmsResult<WmsFulfillmentProgress>> GetPackingStatusAsync(string id, CancellationToken ct = default) => inner.GetPackingStatusAsync(id, ct);
    public Task<WmsResult<WmsShipmentStatus>> GetShipmentStatusAsync(string id, CancellationToken ct = default) => inner.GetShipmentStatusAsync(id, ct);
    public Task<WmsResult<WmsRequestEligibility>> CheckCancellationOrReturnEligibilityAsync(string id, WmsRequestType type, CancellationToken ct = default) => inner.CheckCancellationOrReturnEligibilityAsync(id, type, ct);
}
