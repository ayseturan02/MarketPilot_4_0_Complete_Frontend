namespace MarketPilot.Web.Wms;

[Flags]
public enum WmsCapability
{
    None = 0,
    Warehouses = 1,
    Stock = 2,
    StockMovements = 4,
    FulfillmentSubmission = 8,
    AcceptanceStatus = 16,
    Reservations = 32,
    PickingStatus = 64,
    PackingStatus = 128,
    ShipmentStatus = 256,
    CancellationEligibility = 512,
    ReturnEligibility = 1024,
    All = 2047
}

public sealed record WmsConnectionProfile(
    string Id,
    string TenantId,
    string ProviderKey = "mock",
    string Mode = "mock",
    bool Enabled = true,
    bool AllowMockWrites = false);

public sealed record WmsCapabilities(WmsCapability Supported, bool IsMock)
{
    public bool Supports(WmsCapability capability) => (Supported & capability) == capability;
}

public sealed record WmsError(string Code, string Message, bool Retryable = false);

public sealed class WmsProviderException(WmsError error) : Exception(error.Message)
{
    public WmsError Error { get; } = error;
}

public sealed record WmsResult<T>(T Data, string CorrelationId, DateTimeOffset CompletedAt, bool IsMock = true);

public sealed record WmsWarehouse(string Id, string Code, string Name, bool IsActive);

public sealed record WmsStockSnapshot(
    string WarehouseId,
    string Sku,
    decimal? OnHandQuantity,
    decimal? ReservedQuantity,
    string Unit,
    DateTimeOffset CapturedAt,
    long? SourceVersion = null,
    decimal? DamagedQuantity = null,
    decimal? BlockedQuantity = null,
    decimal? ReturnInspectionQuantity = null,
    decimal? AllocatedQuantity = null,
    decimal? InTransferQuantity = null,
    bool ProviderManaged = false,
    string? ProviderName = null);

public sealed record WmsStockMovement(
    string Id,
    string WarehouseId,
    string Sku,
    decimal QuantityDelta,
    string Unit,
    string Reason,
    DateTimeOffset OccurredAt);

public sealed record WmsFulfillmentLine(string Sku, decimal Quantity, string Unit);

public sealed record WmsFulfillmentOrder(
    string Id,
    string ExternalOrderId,
    string WarehouseId,
    IReadOnlyList<WmsFulfillmentLine> Lines,
    DateTimeOffset RequestedAt);

public sealed record WmsFulfillmentAcceptance(string FulfillmentOrderId, string Status, string? Message, DateTimeOffset UpdatedAt);

public sealed record WmsReservationLine(string Sku, decimal Quantity, string Unit);

public sealed record WmsReservation(
    string Id,
    string FulfillmentOrderId,
    string WarehouseId,
    string Status,
    IReadOnlyList<WmsReservationLine> Lines,
    DateTimeOffset UpdatedAt);

public sealed record WmsFulfillmentProgress(string FulfillmentOrderId, string Status, DateTimeOffset UpdatedAt);

public sealed record WmsShipmentStatus(
    string FulfillmentOrderId,
    string Status,
    string? Carrier,
    string? TrackingNumber,
    string? TrackingUrl,
    DateTimeOffset UpdatedAt);

public enum WmsRequestType
{
    Cancellation,
    Return
}

public sealed record WmsRequestEligibility(
    string FulfillmentOrderId,
    WmsRequestType RequestType,
    bool IsEligible,
    string? Reason,
    DateTimeOffset CheckedAt);

public interface IWmsProvider
{
    WmsConnectionProfile Profile { get; }
    WmsCapabilities Capabilities { get; }

    Task<WmsResult<IReadOnlyList<WmsWarehouse>>> ReadWarehousesAsync(CancellationToken ct = default);
    Task<WmsResult<IReadOnlyList<WmsStockSnapshot>>> ReadStockAsync(string warehouseId, CancellationToken ct = default);
    Task<WmsResult<IReadOnlyList<WmsStockMovement>>> ReadStockMovementsAsync(string warehouseId, DateTimeOffset? since = null, CancellationToken ct = default);
    Task<WmsResult<WmsFulfillmentAcceptance>> SubmitFulfillmentOrderAsync(WmsFulfillmentOrder order, CancellationToken ct = default);
    Task<WmsResult<WmsFulfillmentAcceptance>> GetAcceptanceStatusAsync(string fulfillmentOrderId, CancellationToken ct = default);
    Task<WmsResult<WmsReservation>> GetReservationStatusAsync(string fulfillmentOrderId, CancellationToken ct = default);
    Task<WmsResult<WmsFulfillmentProgress>> GetPickingStatusAsync(string fulfillmentOrderId, CancellationToken ct = default);
    Task<WmsResult<WmsFulfillmentProgress>> GetPackingStatusAsync(string fulfillmentOrderId, CancellationToken ct = default);
    Task<WmsResult<WmsShipmentStatus>> GetShipmentStatusAsync(string fulfillmentOrderId, CancellationToken ct = default);
    Task<WmsResult<WmsRequestEligibility>> CheckCancellationOrReturnEligibilityAsync(string fulfillmentOrderId, WmsRequestType requestType, CancellationToken ct = default);
}

public sealed record WmsMockBehavior(int DelayMilliseconds = 0, string? FailureOperation = null);

public static class WmsMockOperations
{
    public const string Warehouses = "warehouses";
    public const string Stock = "stock";
    public const string StockMovements = "stock-movements";
    public const string FulfillmentSubmission = "fulfillment-submission";
    public const string AcceptanceStatus = "acceptance-status";
    public const string Reservations = "reservations";
    public const string PickingStatus = "picking-status";
    public const string PackingStatus = "packing-status";
    public const string ShipmentStatus = "shipment-status";
    public const string CancellationEligibility = "cancellation-eligibility";
    public const string ReturnEligibility = "return-eligibility";
}
