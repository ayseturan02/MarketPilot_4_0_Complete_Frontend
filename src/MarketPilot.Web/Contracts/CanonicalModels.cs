using System.Text.Json;
namespace MarketPilot.Web.Contracts;

// Versions are monotonic within (tenant, source, account, kind, source record).
public sealed record RecordContext(string TenantId,string SourceSystem,string SourceRecordId,string? ExternalAccountId,
    string CorrelationId,string IdempotencyKey,long SourceVersion,DateTimeOffset OccurredAt,DateTimeOffset ReceivedAt);
public abstract record CanonicalRecord(string Id,RecordContext Context);
public sealed record CanonicalProduct(string Id,RecordContext Context,string Name,string Category,string Brand,string Status="active",[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Description=null,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ApprovedImages=null) : CanonicalRecord(Id,Context);
public sealed record ProductVariant(string Id,RecordContext Context,string ProductId,string Sku,string Color,string Size,string BaseUnit,
    bool WholeUnitsOnly,IReadOnlyList<UnitConversion> Conversions,string Status="active") : CanonicalRecord(Id,Context);
public sealed record UnitConversion(string UnitCode,decimal BaseUnits);
public sealed record Barcode(string Id,RecordContext Context,string VariantId,string Value,string Scheme,string UnitCode) : CanonicalRecord(Id,Context);
public sealed record ExternalProductMapping(string Id,RecordContext Context,string VariantId,string ExternalListingId,string ExternalSku,string? ExternalBarcode) : CanonicalRecord(Id,Context);
public sealed record Warehouse(string Id,RecordContext Context,string Code,string Name,bool Active=true) : CanonicalRecord(Id,Context);
public sealed record StockPosition(string Id,RecordContext Context,string VariantId,string WarehouseId,decimal Quantity,string UnitCode,decimal SafetyStock=0) : CanonicalRecord(Id,Context);
public sealed record StockReservation(string Id,RecordContext Context,string VariantId,string WarehouseId,string OrderId,decimal Quantity,string UnitCode,
    string Status="held",DateTimeOffset? ExpiresAt=null) : CanonicalRecord(Id,Context);
public sealed record AvailableToSell(string TenantId,string VariantId,string WarehouseId,string UnitCode,decimal? OnHand,decimal Reserved,decimal? SafetyStock,decimal? Quantity,DateTimeOffset CalculatedAt);
public sealed record PriceEntry(string VariantId,decimal? Amount,string Currency,string UnitCode,bool VatIncluded,decimal? VatRate=null);
public sealed record PriceList(string Id,RecordContext Context,string Name,IReadOnlyList<PriceEntry> Entries,DateTimeOffset ValidFrom,DateTimeOffset? ValidTo=null) : CanonicalRecord(Id,Context);
public sealed record OrderLine(string Id,string VariantId,string ExternalLineId,string Sku,string? Barcode,decimal Quantity,string UnitCode,decimal UnitPrice,string Currency);
public sealed record MarketplaceOrder(string Id,RecordContext Context,string OrderNumber,string Status,string Currency,IReadOnlyList<OrderLine> Lines,DateTimeOffset OrderedAt) : CanonicalRecord(Id,Context);
public sealed record FulfillmentLine(string OrderLineId,string VariantId,decimal Quantity,string UnitCode);
public sealed record FulfillmentRequest(string Id,RecordContext Context,string OrderId,string WarehouseId,string Status,IReadOnlyList<FulfillmentLine> Lines) : CanonicalRecord(Id,Context);
public sealed record Shipment(string Id,RecordContext Context,string FulfillmentRequestId,string WarehouseId,string Carrier,string? TrackingNumber,string? LabelUrl,string Status,IReadOnlyList<FulfillmentLine> Lines,DateTimeOffset? ShippedAt=null) : CanonicalRecord(Id,Context);
public sealed record ReturnLine(string OrderLineId,string VariantId,decimal Quantity,string UnitCode,string Reason);
public sealed record ReturnRequest(string Id,RecordContext Context,string OrderId,string WarehouseId,string Status,IReadOnlyList<ReturnLine> Lines,DateTimeOffset? ReceivedAt=null) : CanonicalRecord(Id,Context);
public sealed record InvoiceReference(string Id,RecordContext Context,string OrderId,string Number,string DocumentType,string Status,string Currency,decimal Amount,string? DocumentUrl=null) : CanonicalRecord(Id,Context);
public sealed record IntegrationEvent(string Id,RecordContext Context,string EventType,string EntityType,string EntityId,int SchemaVersion,JsonElement Payload) : CanonicalRecord(Id,Context);
public sealed record SyncCheckpoint(string Id,RecordContext Context,string Stream,string? Cursor,long LastSourceVersion,DateTimeOffset? LastSuccessAt) : CanonicalRecord(Id,Context);

public sealed record ContractReceipt(string Fingerprint,string State,string RecordId,string? QueueId);
public sealed record MappingReview(string Id,string Kind,string RecordId,RecordContext Context,string Reason,JsonElement Payload,DateTimeOffset CreatedAt,string State="pending",string? ResolvedVariantId=null);
public sealed record ContractResult(string State,string RecordId,string? QueueId=null);
public sealed class ContractState
{
    public int SchemaVersion {get;set;}=1;
    public Dictionary<string,JsonElement> Records {get;set;}=[];
    public Dictionary<string,ContractReceipt> Receipts {get;set;}=[];
    public List<MappingReview> ManualMappings {get;set;}=[];
}
