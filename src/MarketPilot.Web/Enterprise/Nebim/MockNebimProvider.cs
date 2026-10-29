using System.Text.Json;
using MarketPilot.Web.Contracts;
using ContractOrder=MarketPilot.Web.Contracts.MarketplaceOrder;
namespace MarketPilot.Web.Enterprise.Nebim;

// All fields below are MarketPilot fixtures, NOT a Nebim transport contract.
public sealed class MockNebimProvider(DemoStore db,ErpConnectionProfile profile,ErpCapabilities? capabilities=null):INebimErpProvider
{
    public ErpConnectionProfile Profile{get;}=profile;
    public ErpCapabilities Capabilities{get;}=capabilities??new(profile.Mode=="mock"&&profile.Enabled?ErpReadCapability.All:ErpReadCapability.None,profile.Mode=="mock"&&profile.Enabled&&profile.AllowMockWrites?ErpWriteCapability.All:ErpWriteCapability.None,profile.Mode=="mock");
    private static readonly DateTimeOffset FixtureTime=new(2026,1,1,0,0,0,TimeSpan.Zero);
    private static ErpProviderException Error(string code,string message)=>new(new(code,message));
    private string Id(string suffix)=>Profile.Id+"-"+suffix;
    private string Scope(string value)=>JsonSerializer.Serialize(new[]{Profile.TenantId,Profile.Id,value});
    private static string Hash(CanonicalRecord record)=>CanonicalStore.Fingerprint(record);
    private void Touch(MockNebimState state,bool read){var key=Scope("sync");var old=state.SyncStatuses.GetValueOrDefault(key)??new(Profile.Id,null,null);state.SyncStatuses[key]=read?old with{LastSuccessfulReadAt=DateTimeOffset.UtcNow}:old with{LastSuccessfulWriteAt=DateTimeOffset.UtcNow};}
    private RecordContext Context(string id)=>new(Profile.TenantId,"mock-nebim-v3",Id(id),null,"mock-fixture",Id(id)+"-v2",2,FixtureTime,FixtureTime);
    private void Guard(CancellationToken ct,ErpReadCapability read=ErpReadCapability.None,ErpWriteCapability write=ErpWriteCapability.None)
    {
        ct.ThrowIfCancellationRequested();
        if(!Profile.Enabled)throw Error("Disabled","ERP profili kapalı.");
        if(Profile.Mode!="mock"||!Capabilities.IsMock)throw Error("NotSupported","Gerçek Nebim adaptörü doğrulanmış teknik sözleşme bekliyor.");
        if(string.IsNullOrWhiteSpace(Profile.Id)||string.IsNullOrWhiteSpace(Profile.TenantId))throw Error("InvalidProfile","Profil ve işletme kimliği gerekli.");
        if((Capabilities.Read&read)!=read||(Capabilities.Write&write)!=write)throw Error("NotSupported","İstenen ERP işlemi bu sağlayıcı tarafından desteklenmiyor.");
        if(write!=ErpWriteCapability.None&&!Profile.AllowMockWrites)throw Error("WriteDisabled","Mock ERP yazma izni kapalı.");
    }
    private void Identity(RecordContext c){if(c==null||c.TenantId!=Profile.TenantId)throw Error("TenantMismatch","ERP kaydı başka işletmeye ait.");if(string.IsNullOrWhiteSpace(c.SourceSystem)||string.IsNullOrWhiteSpace(c.SourceRecordId)||string.IsNullOrWhiteSpace(c.IdempotencyKey)||string.IsNullOrWhiteSpace(c.CorrelationId)||c.SourceVersion<1||c.OccurredAt==default||c.ReceivedAt<c.OccurredAt)throw Error("InvalidIdentity","Güvenilir kaynak kimliği, sürüm ve zaman damgası gerekli.");}
    private Task<ErpSyncResult<T>> Read<T>(T value,CancellationToken ct,ErpReadCapability capability){Guard(ct,capability);db.Modify(s=>{Touch(s.NebimMock,true);return true;});return Task.FromResult(Result(value));}
    private static ErpSyncResult<T> Result<T>(T value)=>new(value,Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow);
    private ProductVariant Variant()=>new(Id("variant"),Context("variant"),Id("product"),"TSH-EKR-S","Ekru","S","EA",true,[new("BOX",12)]);
    public Task<ErpSyncResult<IReadOnlyList<CanonicalProduct>>> ReadProductsAsync(CancellationToken ct=default)=>Read<IReadOnlyList<CanonicalProduct>>([new(Id("product"),Context("product"),"Mock pamuklu tişört","Giyim / Tişört","Mock Tekstil",Description:"Mock ERP giyim kaydı; pamuklu ekru tişört.",ApprovedImages:["/assets/mock-erp/ekru.svg"])],ct,ErpReadCapability.Products);
    public Task<ErpSyncResult<IReadOnlyList<ProductVariant>>> ReadVariantsAsync(CancellationToken ct=default)=>Read<IReadOnlyList<ProductVariant>>([Variant()],ct,ErpReadCapability.Variants);
    public Task<ErpSyncResult<ErpIdentifiers>> ReadIdentifiersAsync(CancellationToken ct=default)=>Read(new ErpIdentifiers([Variant()],[new(Id("barcode"),Context("barcode"),Id("variant"),"8691000000001","EAN13","EA")]),ct,ErpReadCapability.Identifiers);
    public Task<ErpSyncResult<IReadOnlyList<PriceList>>> ReadPriceListsAsync(CancellationToken ct=default)=>Read<IReadOnlyList<PriceList>>([new(Id("retail"),Context("retail"),"Mock perakende",[new(Id("variant"),449,"TRY","EA",true,10)],FixtureTime)],ct,ErpReadCapability.Prices);
    public Task<ErpSyncResult<IReadOnlyList<Warehouse>>> ReadWarehousesAsync(CancellationToken ct=default)=>Read<IReadOnlyList<Warehouse>>([new(Id("main"),Context("main"),"MAIN","Mock ana depo"),new(Id("outlet"),Context("outlet"),"OUTLET","Mock outlet depo")],ct,ErpReadCapability.Warehouses);
    public Task<ErpSyncResult<IReadOnlyList<StockPosition>>> ReadStockAsync(CancellationToken ct=default)=>Read<IReadOnlyList<StockPosition>>([new(Id("main-stock"),Context("main-stock"),Id("variant"),Id("main"),36,"EA"),new(Id("outlet-stock"),Context("outlet-stock"),Id("variant"),Id("outlet"),1,"BOX")],ct,ErpReadCapability.Stock);
    public Task<ErpSyncResult<ErpOrderStatus>> RegisterOrderAsync(ContractOrder order,CancellationToken ct=default)
    {
        Guard(ct,write:ErpWriteCapability.RegisterOrder);Identity(order.Context);
        if(string.IsNullOrWhiteSpace(order.Id)||string.IsNullOrWhiteSpace(order.OrderNumber)||string.IsNullOrWhiteSpace(order.Context.ExternalAccountId)||order.Status!="new"||order.Currency!="TRY"||order.Lines==null||order.Lines.Count is <1 or >1000||order.Lines.Any(l=>l==null||string.IsNullOrWhiteSpace(l.Id)||string.IsNullOrWhiteSpace(l.ExternalLineId)||l.VariantId!=Id("variant")||l.Sku!=Variant().Sku||!string.IsNullOrEmpty(l.Barcode)&&l.Barcode!="8691000000001"||l.UnitCode!="EA"||l.Quantity<=0||l.Quantity>1000000||decimal.Truncate(l.Quantity)!=l.Quantity||l.UnitPrice<0||l.UnitPrice>1000000||l.Currency!=order.Currency)||order.Lines.Select(l=>l.Id).Distinct().Count()!=order.Lines.Count)throw Error("InvalidOrder","Mock sipariş kimliği, varyantı, para birimi veya satırları geçersiz.");
        var fingerprint=Hash(order);var key=Scope(order.Id);var receipt=Scope(JsonSerializer.Serialize(new[]{order.Context.SourceSystem,order.Context.ExternalAccountId,order.Context.IdempotencyKey}));
        return Task.FromResult(db.Modify(s=>{
            if(s.NebimMock.Receipts.TryGetValue(receipt,out var prior)&&prior!=fingerprint)throw Error("IdempotencyConflict","ERP işlem anahtarı farklı içerikle kullanıldı.");
            if(s.NebimMock.Orders.TryGetValue(key,out var old)){if(old.Fingerprint!=fingerprint)throw Error("OrderConflict","Sipariş daha önce farklı içerikle kaydedildi.");s.NebimMock.Receipts[receipt]=fingerprint;return Result(old.Status);}
            if(s.NebimMock.Orders.Count>=5000)throw Error("CapacityExceeded","Mock order capacity exceeded.");
            var status=new ErpOrderStatus(order.Id,"registered",1,DateTimeOffset.UtcNow);s.NebimMock.Orders[key]=new(fingerprint,status,order);s.NebimMock.Receipts[receipt]=fingerprint;Touch(s.NebimMock,false);return Result(status);
        }));
    }
    public Task<ErpSyncResult<ErpOrderStatus>> GetOrderStatusAsync(string orderId,CancellationToken ct=default){Guard(ct,ErpReadCapability.OrderStatus);var row=db.CatalogRead(s=>s.NebimMock.Orders.GetValueOrDefault(Scope(orderId)))??throw Error("NotFound","Mock ERP siparişi bulunamadı.");return Read(row.Status,ct,ErpReadCapability.OrderStatus);}
    public Task<ErpSyncResult<IReadOnlyList<InvoiceReference>>> ReadDocumentReferencesAsync(CancellationToken ct=default)
    {
        Guard(ct,ErpReadCapability.Documents);
        var rows=db.CatalogRead(s=>s.NebimMock.Orders.Where(p=>p.Key.StartsWith("[",StringComparison.Ordinal)&&JsonSerializer.Deserialize<string[]>(p.Key) is {} key&&key[0]==Profile.TenantId&&key[1]==Profile.Id).Select(p=>new InvoiceReference(Id("document-"+p.Value.Order.Id),Context("document-"+p.Value.Order.Id),p.Value.Order.Id,"MOCK-"+p.Value.Order.OrderNumber,"invoice","draft",p.Value.Order.Currency,p.Value.Order.Lines.Sum(l=>l.Quantity*l.UnitPrice))).ToArray());return Read<IReadOnlyList<InvoiceReference>>(rows,ct,ErpReadCapability.Documents);
    }
    public Task<ErpSyncResult<IReadOnlyList<IntegrationEvent>>> ReadEventsAsync(CancellationToken ct=default){Guard(ct,ErpReadCapability.Events);return Read<IReadOnlyList<IntegrationEvent>>(db.CatalogRead(s=>s.NebimMock.Events.Where(p=>JsonSerializer.Deserialize<string[]>(p.Key) is {} key&&key[0]==Profile.TenantId&&key[1]==Profile.Id).Select(p=>p.Value).ToArray()),ct,ErpReadCapability.Events);}
    public Task<ErpSyncResult<string>> HandleEventAsync(IntegrationEvent change,CancellationToken ct=default)
    {
        Guard(ct,write:ErpWriteCapability.HandleEvents);Identity(change.Context);
        if(change.EventType is not ("order.cancelled" or "order.returned" or "order.corrected"))throw Error("NotSupported","Mock ERP bu olay türünü desteklemiyor.");
        if(change.EntityType!="order"||change.SchemaVersion!=1||string.IsNullOrWhiteSpace(change.Id)||change.Payload.ValueKind!=JsonValueKind.Object||change.Payload.GetRawText().Length>100000)throw Error("InvalidEvent","Mock ERP olay sözleşmesi geçersiz.");
        var receipt=Scope("event:"+JsonSerializer.Serialize(new[]{change.Context.SourceSystem,change.Context.ExternalAccountId,change.Context.IdempotencyKey}));var fingerprint=Hash(change);
        return Task.FromResult(db.Modify(s=>{
            if(s.NebimMock.Receipts.TryGetValue(receipt,out var prior)){if(prior!=fingerprint)throw Error("IdempotencyConflict","Olay işlem anahtarı farklı içerikle kullanıldı.");return Result("duplicate");}
            var eventKey=Scope("event-record:"+change.Id);if(s.NebimMock.Events.TryGetValue(eventKey,out var priorEvent)){if(Hash(priorEvent)!=fingerprint)throw Error("IdempotencyConflict","Event identity has different content.");s.NebimMock.Receipts[receipt]=fingerprint;return Result("duplicate");}
            var key=Scope(change.EntityId);var order=s.NebimMock.Orders.GetValueOrDefault(key)??throw Error("NotFound","Mock ERP siparişi bulunamadı.");
            if(change.Context.SourceVersion<=order.Status.SourceVersion){if(change.Context.SourceVersion==order.Status.SourceVersion)throw Error("VersionConflict","Aynı ERP sürümünde farklı olay.");s.NebimMock.Receipts[receipt]=fingerprint;return Result("stale");}
            var status=order.Status with{Status=change.EventType.Split('.')[1],SourceVersion=change.Context.SourceVersion,UpdatedAt=DateTimeOffset.UtcNow};s.NebimMock.Orders[key]=order with{Status=status};s.NebimMock.Receipts[receipt]=fingerprint;
            s.NebimMock.Events[Scope("event-record:"+change.Id)]=change;Touch(s.NebimMock,false);return Result("applied");
        }));
    }
    public Task<ErpSyncResult<ErpSyncStatus>> GetSyncStatusAsync(CancellationToken ct=default){Guard(ct,ErpReadCapability.SyncStatus);return Task.FromResult(Result(db.CatalogRead(s=>new ErpSyncStatus(Profile.Id,s.NebimMock.SyncStatuses.GetValueOrDefault(Scope("sync"))?.LastSuccessfulReadAt,s.NebimMock.SyncStatuses.GetValueOrDefault(Scope("sync"))?.LastSuccessfulWriteAt,s.NebimMock.SyncStatuses.GetValueOrDefault(Scope("sync"))?.LastCanonicalSyncAt))));}
}
