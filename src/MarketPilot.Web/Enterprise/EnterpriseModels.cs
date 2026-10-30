using MarketPilot.Web.Catalog;

namespace MarketPilot.Web.Enterprise;

public sealed record ErpItem(string Sku, string Barcode, string Name, decimal? Stock, decimal? Price, string Currency,
    string[]? Images = null, string? Category = null, string? Brand = null, string? Description = null, string? WarehouseCode = null, bool? VatIncluded = null,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] MarketPilot.Web.Workspace.StockAcknowledgement[]? StockAcknowledgements=null,
    string? SourceEntityId = null, long? SourceVersion = null, DateTimeOffset? SourceUpdatedAt = null, StockValue? Inventory = null,
    string? VariantId = null, Dictionary<string, string>? Attributes = null);
public sealed record ErpCatalogBatch(string RequestId, ErpItem[] Items,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] MarketPilot.Web.Workspace.StockAcknowledgement[]? StockAcknowledgements=null);
public sealed record WmsReservationSnapshot(string Barcode, string WarehouseCode, decimal? ReservedQuantity, long? SourceVersion, DateTimeOffset? SourceUpdatedAt);
public sealed class ErpReceipt
{
    public string Hash { get; set; } = "";
    public string[] ProductIds { get; set; } = [];
}
public sealed record SyncTarget(string StoreId, string Source, bool Enabled = false, bool Stock = true, bool Price = true, bool Images = false, bool Orders = true, bool Products = false);
public sealed record SyncSchedule(string Task, bool Enabled = true, int IntervalSeconds = 60);
public sealed class EnterprisePlan
{
    public bool Enabled { get; set; }
    public string WarehouseCode { get; set; } = "";
    public string PriceListCode { get; set; } = "";
    public string StockAuthority { get; set; } = "erp";
    public string FulfillmentMode { get; set; } = "disabled";
    public int SafetyStock { get; set; }
    public int LookbackDays { get; set; } = 2;
    public SyncTarget[] Targets { get; set; } = [];
    public SyncSchedule[] Schedules { get; set; } = Defaults();
    public static SyncSchedule[] Defaults() => [new("catalog",false,900),new("listings",false,900),new("stock",false,60),new("price",false,300),new("images",false,3600),new("orders",false,60),new("erp-orders",false,60),new("wms-orders",false,60),new("wms-stock",false,60),new("wms-reservations",false,60),new("wms-dispatches",false,60),new("cargo-create",false,60),new("shipments",false,60)];
}
public sealed class EnterpriseTask
{
    public string Task { get; set; } = "";
    public string State { get; set; } = "idle";
    public DateTimeOffset NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? Error { get; set; }
    public int Count { get; set; }
    public bool Manual { get; set; }
}
public sealed class EnterpriseJob
{
    public IntegrationEnvelope? IntegrationEvent { get; set; }
    public ErpTransferPlan? TransferPlan { get; set; }
    public string? Result { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Connector { get; set; } = "";
    public string ConnectorIdentity { get; set; } = "";
    public string StoreId { get; set; } = "";
    public string Source { get; set; } = "";
    public string AccountIdentity { get; set; } = "";
    public string Reference { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public string State { get; set; } = "queued";
    public string? Error { get; set; }
    public string? NativeJobId { get; set; }
    public string? ExternalReference { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}
public sealed class EnterpriseState
{
    public Dictionary<string,ErpTransferPolicy> ErpTransferPolicies { get; set; } = [];
    public Dictionary<string,MockErpTransferReceipt> MockErpTransferReceipts { get; set; } = [];
    public Dictionary<string,MarketPilot.Web.Wms.WmsMockProviderState> MockWmsProviderStates { get; set; } = [];
    public EnterprisePlan Plan { get; set; } = new();
    public bool Managed { get; set; }
    public Dictionary<string,EnterpriseTask> Tasks { get; set; } = new();
    public List<EnterpriseJob> Jobs { get; set; } = [];
    public DateTimeOffset? LastCatalogAt { get; set; }
    public DateTimeOffset? LastWmsStockAt { get; set; }
    public DateTimeOffset? LastWmsReservationAt { get; set; }
    public Dictionary<string,string> Delivered { get; set; } = new();
    public Dictionary<string,WmsDispatch> Dispatches { get; set; } = new();
}
public sealed record BridgeReceipt(string IdempotencyKey, string State, string? Reference = null, bool SafeToRetry = false);
public sealed record BridgeShipment(string ExternalOrderId, string StoreId, string AccountSource, string ShipmentId,
    string Carrier, string TrackingNumber, string Status, string? LabelUrl, DateTimeOffset UpdatedAt);
public sealed record WmsPackingLine(string Barcode,int Quantity);
public sealed record WmsDispatch(string ExternalOrderId,string StoreId,string AccountSource,string PackageId,string Status,WmsPackingLine[] Lines,DateTimeOffset UpdatedAt);
