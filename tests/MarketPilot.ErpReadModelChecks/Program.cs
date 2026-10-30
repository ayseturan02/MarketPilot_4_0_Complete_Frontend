using MarketPilot.Web.Catalog;
using MarketPilot.Web.Enterprise;
using Microsoft.Extensions.Configuration;
using MarketPilot.Web.Contracts;
using MarketPilot.Web.Enterprise.Nebim;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Demo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using ContractOrder=MarketPilot.Web.Contracts.MarketplaceOrder;
static void Check(bool value,string message){if(!value)throw new Exception(message);}
static void Reject(Action action){try{action();}catch(IntegrationException){return;}throw new Exception("Expected rejection");}
var folder=Path.Combine(Path.GetTempPath(),"marketpilot-erp-read-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
try
{
 var env=new TestEnvironment{ContentRootPath=folder};var db=new DemoStore(env);var registry=new IntegrationRegistry(env);var hub=new CatalogHub(db,registry,new DemoWorkspace(env));hub.Ensure();
 var mock=new MockNebimProvider(db,new("mock-nebim","default"));var provider=new TestProvider{Products=(await mock.ReadProductsAsync()).Data.ToArray(),Variants=(await mock.ReadVariantsAsync()).Data.ToArray(),Barcodes=(await mock.ReadIdentifiersAsync()).Data.Barcodes.ToArray()};var service=new ErpCatalogReadModel(db,provider);
 var original=db.CatalogRead(s=>s.Products.Single(p=>p.Barcode==provider.Barcodes[0].Value));var id=original.Id;var links=db.CatalogRead(s=>s.Catalog.Listings.Where(l=>l.Value.ProductId==id).ToDictionary(p=>p.Key,p=>p.Value));Check(links.Count==7,"Fixture links");
 var first=await service.Preview(default);Check(first.Changes.Single().ProductId==id&&first.Changes.Single().Action=="updated","Adopt only exact SKU and barcode");Check(db.CatalogRead(s=>s.Products.Single(p=>p.Id==id).ErpSyncedAt)==null,"Preview cannot mutate master");
 service.Apply(first.Token);service.Apply(first.Token);var loaded=db.CatalogRead(s=>s.Products.Single(p=>p.Id==id));Check(loaded.ErpProductId==provider.Products[0].Context.SourceRecordId&&loaded.Attributes["Beden"]=="S"&&loaded.Images.Single()=="/assets/mock-erp/ekru.svg","Provider owns all master fields");
 Check(db.CatalogRead(s=>links.All(l=>s.Catalog.Listings[l.Key]==l.Value)),"Adoption preserves listing identities");
 var legacy=new ErpCatalog(db,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Enterprise:Erp:DataMode","demo"}}).Build());
 Reject(()=>legacy.Import(new("legacy-overwrite",[new(loaded.Sku,loaded.Barcode,"ALTERED MASTER",null,null,"TRY")])));

 var v=provider.Variants[0];var p=provider.Products[0];var b=provider.Barcodes[0];
 provider.Products=[p with{Name="ERP updated title",Description="ERP updated description",Context=p.Context with{SourceVersion=3}}];provider.Variants=[v with{Sku="ERP-NEW-SKU",Color="Siyah",Size="M",Context=v.Context with{SourceVersion=3}}];provider.Barcodes=[b with{Value="8691000099999",Context=b.Context with{SourceVersion=3}}];
 var update=await service.Preview(default);Check(update.Changes.Single().Action=="updated","Updated source preview");service.Apply(update.Token);loaded=db.CatalogRead(s=>s.Products.Single(p=>p.Id==id));Check(loaded.Sku=="ERP-NEW-SKU"&&loaded.Barcode=="8691000099999"&&loaded.Id==id,"Stable ERP identity despite SKU/barcode changes");Check(db.CatalogRead(s=>links.All(l=>s.Catalog.Listings[l.Key]==l.Value)),"ERP updates preserve all marketplace mappings");
 var good=provider.Products[0];provider.Products=[good with{Name="Different same version"}];var conflict=await service.Preview(default);Check(conflict.Changes.Single().Action=="review","Equal version mismatch must not overwrite");service.Apply(conflict.Token);Check(db.CatalogRead(s=>s.Products.Single(p=>p.Id==id).Name)=="ERP updated title","Preserve last valid name");
 provider.Products=[good];var goodVariant=provider.Variants[0];provider.Variants=[goodVariant with{Color=""}];var invalid=await service.Preview(default);Check(invalid.Changes.Single().Action=="review","Missing color is reviewed");service.Apply(invalid.Token);Check(db.CatalogRead(s=>s.Products.Single(p=>p.Id==id).Attributes["Renk"])=="Siyah","Do not invent missing color");
 provider.Variants=[goodVariant];var restore=await service.Preview(default);service.Apply(restore.Token);Check(db.CatalogRead(s=>s.Products.Single(p=>p.Id==id).Status)=="Aktif","Reappearance clears review");
 var products=provider.Products;var variants=provider.Variants;var codes=provider.Barcodes;provider.Products=[];provider.Variants=[];provider.Barcodes=[];
 var missing=await service.Preview(default);Check(missing.Changes.Single().Action=="missing","Full snapshot absence");service.Apply(missing.Token);Check(db.CatalogRead(s=>s.Products.Single(p=>p.Id==id).Status)=="İnceleme","Missing record retained for review");Check(db.CatalogRead(s=>links.All(l=>s.Catalog.Listings[l.Key]==l.Value)),"Missing records retain mappings");
 provider.Products=products;provider.Variants=variants;provider.Barcodes=codes;service.Apply((await service.Preview(default)).Token);
 var stale=await service.Preview(default);db.Modify(s=>{s.Products.Single(p=>p.Id==id).Version++;return true;});Reject(()=>service.Apply(stale.Token));
 Reject(()=>hub.Edit(id,new(loaded.Name,loaded.Sku,loaded.Barcode,loaded.Category,loaded.Brand,loaded.Description,1,1,"TRY",true,"Aktif",loaded.Version)));
 var reopened=new DemoStore(env);Check(reopened.CatalogRead(s=>s.Products.Single(p=>p.Id==id).ErpProductId)==good.Context.SourceRecordId,"Source metadata survives restart");Check(reopened.CatalogRead(s=>links.All(l=>s.Catalog.Listings[l.Key]==l.Value)),"Mappings survive restart");
 provider.Products=[good with{Context=good.Context with{TenantId="another"}}];try{await service.Preview(default);throw new Exception("Foreign snapshot accepted");}catch(IntegrationException){}
 provider.Products=[good];provider.Variants=[goodVariant,goodVariant with{Id="new-variant",Sku="NEW-ERP-SKU",Size="L",Context=goodVariant.Context with{SourceRecordId="new-variant",SourceVersion=1}}];provider.Barcodes=[codes[0],codes[0] with{Id="new-barcode",VariantId="new-variant",Value="8691000088888",Context=codes[0].Context with{SourceRecordId="new-barcode",SourceVersion=1}}];
 var added=await service.Preview(default);Check(added.Changes.Any(c=>c.Action=="added"),"New ERP variant is previewed");service.Apply(added.Token);var newRow=db.CatalogRead(s=>s.Products.Single(p=>p.ErpVariantId=="new-variant"));Check(!newRow.StockKnown&&!newRow.PriceKnown&&newRow.Currency=="","Missing stock and price stay unknown, not invented");
 Console.WriteLine("PASS ERP catalog read model: provider fields and approved images, readonly master, exact identity adoption, stable product/listing identity on SKU/barcode changes, preview, equal-version conflicts, missing/invalid data review, recovery, stale preview rejection and JSON restart. Mock only.");
}
finally{var absolute=Path.GetFullPath(folder);if(!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(absolute).StartsWith("marketpilot-erp-read-"))throw new Exception("Unsafe cleanup");Directory.Delete(absolute,true);}
sealed class TestProvider:INebimErpProvider
{
 public CanonicalProduct[] Products=[];public ProductVariant[] Variants=[];public Barcode[] Barcodes=[];
 public ErpConnectionProfile Profile=>new("mock-nebim","default");public ErpCapabilities Capabilities=>new(ErpReadCapability.Products|ErpReadCapability.Variants|ErpReadCapability.Identifiers,ErpWriteCapability.None,true);
 private static Task<ErpSyncResult<T>> Result<T>(T data)=>Task.FromResult(new ErpSyncResult<T>(data,"test",DateTimeOffset.UtcNow));
 public Task<ErpSyncResult<IReadOnlyList<CanonicalProduct>>> ReadProductsAsync(CancellationToken ct=default)=>Result<IReadOnlyList<CanonicalProduct>>(Products);
 public Task<ErpSyncResult<IReadOnlyList<ProductVariant>>> ReadVariantsAsync(CancellationToken ct=default)=>Result<IReadOnlyList<ProductVariant>>(Variants);
 public Task<ErpSyncResult<ErpIdentifiers>> ReadIdentifiersAsync(CancellationToken ct=default)=>Result(new ErpIdentifiers(Variants,Barcodes));
 public Task<ErpSyncResult<IReadOnlyList<PriceList>>> ReadPriceListsAsync(CancellationToken ct=default)=>throw new NotSupportedException();
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
