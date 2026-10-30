using MarketPilot.Web.Contracts;
using ContractOrder=MarketPilot.Web.Contracts.MarketplaceOrder;
namespace MarketPilot.Web.Enterprise.Nebim;

[Flags] public enum ErpReadCapability { None=0,Products=1,Variants=2,Identifiers=4,Prices=8,Warehouses=16,Stock=32,OrderStatus=64,Documents=128,Events=256,SyncStatus=512,All=1023 }
[Flags] public enum ErpWriteCapability { None=0,RegisterOrder=1,HandleEvents=2,All=3 }
public sealed record ErpConnectionProfile(string Id,string TenantId,string Mode="mock",bool Enabled=true,bool AllowMockWrites=false);
public sealed record ErpCapabilities(ErpReadCapability Read,ErpWriteCapability Write,bool IsMock);
public sealed record ErpError(string Code,string Message,bool Retryable=false);
public sealed class ErpProviderException(ErpError error):Exception(error.Message) {public ErpError Error{get;}=error;}
public sealed record ErpSyncResult<T>(T Data,string CorrelationId,DateTimeOffset CompletedAt,bool IsMock=true);
public sealed record ErpIdentifiers(IReadOnlyList<ProductVariant> Variants,IReadOnlyList<Barcode> Barcodes);
public sealed record ErpOrderStatus(string OrderId,string Status,long SourceVersion,DateTimeOffset UpdatedAt);
public sealed record ErpSyncStatus(string ProfileId,DateTimeOffset? LastSuccessfulReadAt,DateTimeOffset? LastSuccessfulWriteAt,DateTimeOffset? LastCanonicalSyncAt=null);
public interface INebimErpProvider
{
    // TODO(native-adapter): implement only after transport, identity, paging, write and error
    // contracts are verified with the actual Nebim installation. No native URLs/SQL assumed.
    ErpConnectionProfile Profile{get;}
    ErpCapabilities Capabilities{get;}
    Task<ErpSyncResult<IReadOnlyList<CanonicalProduct>>> ReadProductsAsync(CancellationToken ct=default);
    Task<ErpSyncResult<IReadOnlyList<ProductVariant>>> ReadVariantsAsync(CancellationToken ct=default);
    Task<ErpSyncResult<ErpIdentifiers>> ReadIdentifiersAsync(CancellationToken ct=default);
    Task<ErpSyncResult<IReadOnlyList<PriceList>>> ReadPriceListsAsync(CancellationToken ct=default);
    Task<ErpSyncResult<IReadOnlyList<Warehouse>>> ReadWarehousesAsync(CancellationToken ct=default);
    Task<ErpSyncResult<IReadOnlyList<StockPosition>>> ReadStockAsync(CancellationToken ct=default);
    Task<ErpSyncResult<ErpOrderStatus>> RegisterOrderAsync(ContractOrder order,CancellationToken ct=default);
    Task<ErpSyncResult<ErpOrderStatus>> GetOrderStatusAsync(string orderId,CancellationToken ct=default);
    Task<ErpSyncResult<IReadOnlyList<InvoiceReference>>> ReadDocumentReferencesAsync(CancellationToken ct=default);
    Task<ErpSyncResult<IReadOnlyList<IntegrationEvent>>> ReadEventsAsync(CancellationToken ct=default);
    Task<ErpSyncResult<string>> HandleEventAsync(IntegrationEvent change,CancellationToken ct=default);
    Task<ErpSyncResult<ErpSyncStatus>> GetSyncStatusAsync(CancellationToken ct=default);
}
public sealed record MockOrderEntry(string Fingerprint,ErpOrderStatus Status,ContractOrder Order);
public sealed class MockNebimState
{
    public Dictionary<string,MockOrderEntry> Orders{get;set;}=[];
    public Dictionary<string,string> Receipts{get;set;}=[];
    public Dictionary<string,IntegrationEvent> Events{get;set;}=[];
    public Dictionary<string,ErpSyncStatus> SyncStatuses{get;set;}=[];
    public Dictionary<string,long> SyncVersions{get;set;}=[];
}
