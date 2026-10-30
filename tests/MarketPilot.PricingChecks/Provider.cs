using MarketPilot.Web.Contracts;
using MarketPilot.Web.Enterprise.Nebim;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using ContractOrder=MarketPilot.Web.Contracts.MarketplaceOrder;
sealed class TestProvider:INebimErpProvider
{
 public PriceList[] Lists=[];public CanonicalProduct[] Products=[];public ProductVariant[] Variants=[];public Barcode[] Barcodes=[];
 public ErpConnectionProfile Profile=>new("mock-nebim","default");public ErpCapabilities Capabilities=>new(ErpReadCapability.Products|ErpReadCapability.Variants|ErpReadCapability.Identifiers|ErpReadCapability.Prices,ErpWriteCapability.None,true);
 private static Task<ErpSyncResult<T>> Result<T>(T data)=>Task.FromResult(new ErpSyncResult<T>(data,"test",DateTimeOffset.UtcNow));
 public Task<ErpSyncResult<IReadOnlyList<CanonicalProduct>>> ReadProductsAsync(CancellationToken ct=default)=>Result<IReadOnlyList<CanonicalProduct>>(Products);
 public Task<ErpSyncResult<IReadOnlyList<ProductVariant>>> ReadVariantsAsync(CancellationToken ct=default)=>Result<IReadOnlyList<ProductVariant>>(Variants);
 public Task<ErpSyncResult<ErpIdentifiers>> ReadIdentifiersAsync(CancellationToken ct=default)=>Result(new ErpIdentifiers(Variants,Barcodes));
 public Task<ErpSyncResult<IReadOnlyList<PriceList>>> ReadPriceListsAsync(CancellationToken ct=default)=>Result<IReadOnlyList<PriceList>>(Lists);
 public Task<ErpSyncResult<IReadOnlyList<Warehouse>>> ReadWarehousesAsync(CancellationToken ct=default)=>throw new NotSupportedException();
 public Task<ErpSyncResult<IReadOnlyList<StockPosition>>> ReadStockAsync(CancellationToken ct=default)=>throw new NotSupportedException();
 public Task<ErpSyncResult<ErpOrderStatus>> RegisterOrderAsync(ContractOrder o,CancellationToken ct=default)=>throw new NotSupportedException();
 public Task<ErpSyncResult<ErpOrderStatus>> GetOrderStatusAsync(string id,CancellationToken ct=default)=>throw new NotSupportedException();
 public Task<ErpSyncResult<IReadOnlyList<InvoiceReference>>> ReadDocumentReferencesAsync(CancellationToken ct=default)=>throw new NotSupportedException();
 public Task<ErpSyncResult<IReadOnlyList<IntegrationEvent>>> ReadEventsAsync(CancellationToken ct=default)=>throw new NotSupportedException();
 public Task<ErpSyncResult<string>> HandleEventAsync(IntegrationEvent e,CancellationToken ct=default)=>throw new NotSupportedException();
 public Task<ErpSyncResult<ErpSyncStatus>> GetSyncStatusAsync(CancellationToken ct=default)=>throw new NotSupportedException();
}
sealed class TestEnvironment:IWebHostEnvironment {public string EnvironmentName{get;set;}="Production";public string ApplicationName{get;set;}="ErpReadChecks";public string ContentRootPath{get;set;}="";public string WebRootPath{get;set;}="";public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();}
