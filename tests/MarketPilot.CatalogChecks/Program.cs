using System.Diagnostics;
using System.Text.Json;
using MarketPilot.Web.Catalog;
using MarketPilot.Web.Enterprise;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Demo;
using MarketPilot.Web.Local;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
static void Reject(Action action,string code){try{action();}catch(IntegrationException e){Check(e.Code==code,"Unexpected error "+e.Code);return;}throw new Exception("Expected "+code);}
var folder=Path.Combine(Path.GetTempPath(),"marketpilot-catalog-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
try{
    var env=new TestEnvironment{ContentRootPath=folder};var db=new DemoStore(env);var registry=new IntegrationRegistry(env);var demo=new DemoWorkspace(env);var hub=new CatalogHub(db,registry,demo);hub.Ensure();
var demoErp=new ErpCatalog(db,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Enterprise:Erp:DataMode","demo"}}).Build());
    Check(hub.Search(new()).TotalElements==17,"Legacy products must remain alongside eight mapped demo variants");var row=hub.Search(new(Search:"8691000000001")).Items.Single();Check(row.ListingCount==7,"Each store must have an independent link");var original=row.Product;
    var store=registry.Connections["trendyol"];var first=new CatalogCandidate(CatalogHub.Key(store.Id,"new-valid"),"new-valid","NEW-ROLLBACK","8698888888888","Rollback candidate","Test","",1,10,"TRY",true,"approved");var conflicting=first with{Key=CatalogHub.Key(store.Id,"new-conflict"),ExternalId="new-conflict",Sku=original.Sku,Barcode="8697777777777"};var batch=new CandidateBatch("test",store.Id,CatalogHub.Source(store),DateTimeOffset.UtcNow,[first,conflicting],0,null,false);
    Reject(()=>hub.Import(batch,[first.Key,conflicting.Key]),"master_missing");Check(hub.Search(new(Search:"NEW-ROLLBACK")).TotalElements==0,"Marketplace import created an ERP master product");Check(new DemoStore(env).SearchProducts(new(Search:"NEW-ROLLBACK")).TotalElements==0,"Marketplace product leaked to disk");
    var real=new ConnectionState("real-test",new("Live test","trendyol","real","production"),"untested",null,null,null,null,null);registry.Connections[real.Id]=real;
    var realErp=new ErpCatalog(db,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Enterprise:Erp:DataMode","real"}}).Build());
    realErp.Import(new("real-source",[new(original.Sku,original.Barcode,original.Name,43,100,"TRY",Category:original.Category,WarehouseCode:"MAIN")]));
    var native=first with{Key=CatalogHub.Key(real.Id,"real-1"),ExternalId="real-1",Sku=original.Sku,Barcode=original.Barcode,Stock=1.5m,Price=null,Currency="",VatIncluded=false};
    hub.Import(new("real",real.Id,CatalogHub.Source(real),DateTimeOffset.UtcNow,[native],0,null,false),[native.Key]);
    var separated=hub.Search(new(Search:original.Barcode));Check(separated.Items.Count==2,"Demo and real master must remain separate");var realProduct=separated.Items.Single(r=>r.Product.SourceMode=="real").Product;var realListing=db.CatalogRead(s=>s.Catalog.Listings[native.Key]);Check(realProduct.Stock==43&&!realProduct.PriceKnown&&realListing.Stock==1.5m&&realListing.Price==null&&realListing.Currency==""&&realListing.VatIncluded==false,"Marketplace observation changed or obscured ERP master ownership");Reject(()=>hub.Remap(new(native.Key,original.Id)),"mode_mismatch");Check(!hub.Preview([native.Key]).Single().Ready,"Real writes must be blocked");
    Check(DataOwnershipPolicy.All.Select(policy=>policy.Field).Distinct().Count()==14,"Every governed field must have exactly one policy");
    Check(DataOwnershipPolicy.For(OwnershipField.PhysicalStock,SourceSystem.Wms).Owner==SourceSystem.Wms&&
        !DataOwnershipPolicy.IsWriter(OwnershipField.PhysicalStock,SourceSystem.NebimV3,SourceSystem.Wms),"Stock authority must be configured, not selected by arrival time");
    Check(DataOwnershipPolicy.For(OwnershipField.Reservation,nebimIntegratedWms:true).Owner==SourceSystem.NebimV3&&
        DataOwnershipPolicy.IsWriter(OwnershipField.Reservation,SourceSystem.NebimV3,nebimIntegratedWms:true)&&
        !DataOwnershipPolicy.IsWriter(OwnershipField.Reservation,SourceSystem.Wms,nebimIntegratedWms:true),"Nebim-integrated WMS must have exactly one reservation writer");
    var earlier=DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    ExternalReference Ref(long? version,DateTimeOffset? at,string hash,SourceSystem source=SourceSystem.Wms)=>new(source,"physical-stock","WH-1",version,at,earlier,hash,"WH-1");
    Check(DataOwnershipPolicy.Resolve(OwnershipField.PhysicalStock,SourceSystem.Wms,SourceSystem.Wms,Ref(2,null,"same"),Ref(1,null,"older"))==OwnershipDecision.Stale,"Old source revision must be rejected");
    Check(DataOwnershipPolicy.Resolve(OwnershipField.PhysicalStock,SourceSystem.Wms,SourceSystem.Wms,Ref(null,null,"old"),Ref(null,null,"new"))==OwnershipDecision.Conflict,"Unordered differing source data must be blocked");
    Check(DataOwnershipPolicy.Resolve(OwnershipField.PhysicalStock,SourceSystem.Wms,SourceSystem.Wms,Ref(2,null,"same"),Ref(2,null,"same"))==OwnershipDecision.Duplicate,"Exact repeated source event must be idempotent");
    Check(DataOwnershipPolicy.Resolve(OwnershipField.PhysicalStock,SourceSystem.NebimV3,SourceSystem.Wms,Ref(null,null,"old"),Ref(null,null,"new",SourceSystem.NebimV3))==OwnershipDecision.NotOwner,"Non-authoritative stock source must not write");
    Check(StockValue.NotProvided.IsValid()&&StockValue.Unknown.IsValid()&&StockValue.Known(0).IsValid()&&
        !new StockValue(StockValueState.Known,null).IsValid()&&!new StockValue(StockValueState.Unknown,0).IsValid(),"Not-provided, unknown, and known-zero stock must remain distinct");
    var stockSku="SOURCE-STOCK-STATE";var stockBarcode="8695550000001";var baseTime=DateTimeOffset.Parse("2026-02-01T00:00:00Z");
    ErpItem StockItem(long revision,StockValue value,decimal? price=20,DateTimeOffset? at=null)=>new(stockSku,stockBarcode,"Stock state test",null,price,"TRY",WarehouseCode:"MAIN",VatIncluded:null,SourceVersion:revision,SourceUpdatedAt:at??baseTime.AddMinutes(revision),Inventory:value);
    realErp.Import(new("stock-unknown",[StockItem(1,StockValue.Unknown)]));
    var stockProduct=hub.Search(new(Search:stockSku)).Items.Single().Product;Check(!stockProduct.StockKnown&&stockProduct.StockState==StockValueState.Unknown&&stockProduct.VatIncluded is null,"Unknown stock/VAT must not be changed into zero or a guessed VAT value");
    realErp.Import(new("stock-zero",[StockItem(2,StockValue.Known(0))]));
    stockProduct=hub.Search(new(Search:stockSku)).Items.Single().Product;Check(stockProduct.StockKnown&&stockProduct.StockState==StockValueState.Known&&stockProduct.Stock==0,"Known zero stock must remain a known zero");
    Check(stockProduct.SellableStock is null&&stockProduct.SellableStockState==StockValueState.NotProvided,"Physical stock alone must not imply known sellable stock while WMS reservations are missing");
    db.Modify(s=>{var product=s.Products.Single(p=>p.Id==stockProduct.Id);product.ReservationState=StockValueState.Known;product.ReservedQuantity=0;return true;});
    stockProduct=hub.Search(new(Search:stockSku)).Items.Single().Product;Check(stockProduct.SellableStock==0&&stockProduct.SellableStockState==StockValueState.Known,"Known zero reservation must produce a known sellable balance");
    realErp.Import(new("stock-not-provided",[StockItem(3,StockValue.NotProvided)]));
    stockProduct=hub.Search(new(Search:stockSku)).Items.Single().Product;Check(stockProduct.StockKnown&&stockProduct.Stock==0,"Not-provided stock must preserve the previous authoritative value");
    realErp.Import(new("stock-unknown-again",[StockItem(4,StockValue.Unknown)]));
    stockProduct=hub.Search(new(Search:stockSku)).Items.Single().Product;Check(!stockProduct.StockKnown&&stockProduct.StockState==StockValueState.Unknown&&stockProduct.SellableStock is null&&stockProduct.SellableStockState==StockValueState.Unknown,"Explicitly unknown stock must clear knownness without becoming zero or exposing sellable quantity");
    Reject(()=>realErp.Import(new("stale-stock-source",[StockItem(3,StockValue.Known(5),price:99)])),"source_stale");
    Reject(()=>realErp.Import(new("unordered-price-source",[StockItem(5,StockValue.Unknown,price:99,at:null) with{SourceVersion=null,SourceUpdatedAt=null}])),"source_unordered");
    Reject(()=>realErp.Import(new("wrong-stock-warehouse",[StockItem(5,StockValue.Known(5))]),SourceSystem.NebimV3,"SECONDARY"),"warehouse_mismatch");
    Reject(()=>realErp.Import(new("missing-stock-warehouse",[new ErpItem("NO-WAREHOUSE","8695550000004","No warehouse",1,10,"TRY")])), "warehouse_required");
    try{realErp.Import(new("missing-currency",[new ErpItem("NO-CURRENCY","8695550000002","No currency",0,10,"",WarehouseCode:"MAIN") ]));throw new Exception("Missing currency was accepted");}catch(ArgumentException){}
    try{realErp.Import(new("invalid-stock-state",[new ErpItem("BAD-STOCK","8695550000003","Bad stock",null,10,"TRY",WarehouseCode:"MAIN",Inventory:StockValue.Known(0)) with{Stock=2}]));throw new Exception("Conflicting stock representations were accepted");}catch(ArgumentException){}
    CatalogEdit Change(Product p,decimal stock)=>new(p.Name,p.Sku,p.Barcode,p.Category,p.Brand,p.Description,stock,p.Price,p.Currency,p.VatIncluded,p.Status,p.Version);
    var demoLink=row.Listings.Single(l=>l.StoreId=="trendyol");
    Reject(()=>hub.Publish(new("blocked-legacy-price",[demoLink.Key],new(){{original.Id,original.Version}})),"pricing_workflow_required");
    Reject(()=>hub.Edit(original.Id,Change(original,41)),"master_read_only");
    var restartedDb=new DemoStore(env);var restartedDemo=new DemoWorkspace(env);var restarted=new CatalogHub(restartedDb,registry,restartedDemo);restarted.Ensure();
    Check(!restarted.Preview([demoLink.Key]).Single().Ready,"Restart must preserve the ERP-only price publishing requirement");
    var operations=new OperationsHub(restartedDb,registry,restartedDemo);var nativeOrder=restartedDemo.Snapshot(store.Settings).Orders[0] with{PackageId="real-package",OrderNumber="REAL-TEST-1",Cargo=new("Test carrier","REAL-TRACK","","","",null,null),LastModifiedDate=1};operations.Observe(real,new MarketplacePage<MarketplaceOrder>([nativeOrder],0,25,1,1,null));operations.Observe(real,new MarketplacePage<MarketplaceOrder>([nativeOrder with{Status="Shipped",LastModifiedDate=2}],0,25,1,1,null));var captured=operations.Orders(new(Search:"REAL-TEST-1"));Check(captured.Items.Count==1&&captured.Items[0].Status=="Kargolandı"&&captured.Items[0].Lines[0].ProductId==realProduct.Id&&captured.Items[0].ExternalReferences.Any(reference=>reference.System==SourceSystem.Marketplace&&reference.SourceVersion==2),"Observed marketplace order must be versioned and linked to ERP master");Check(operations.Shipments(new(Search:"REAL-TRACK")).Items.Single().ExternalReferences.Any(reference=>reference.System==SourceSystem.Marketplace),"Marketplace shipment observations must retain their source identity");Check(new DemoStore(env).CatalogRead(s=>s.Catalog.Orders.Count)==1,"Read-page order snapshot did not persist");
    Reject(()=>operations.Observe(real,new MarketplacePage<MarketplaceOrder>([nativeOrder with{Status="Cancelled",LastModifiedDate=2}],0,25,1,1,null)),"order_conflict");
    Reject(()=>operations.Observe(real,new MarketplacePage<MarketplaceOrder>([nativeOrder with{Status="Cancelled",LastModifiedDate=null}],0,25,1,1,null)),"order_conflict");
    var sw=Stopwatch.StartNew();restartedDb.Modify(s=>{for(var i=0;i<100000;i++)s.Products.Add(new Product{Id="scale-"+i,Name="Scale row "+i,Sku="SCALE-"+i,Stock=i,Price=10,SourceMode="real",SourceSystem="nebim-v3"});return true;});var page=restarted.Search(new(Search:"Scale row",Page:3999));Check(page.Items.Count==25&&page.TotalElements==100000&&page.TotalPages==4000,"Filtering/paging broke with 100k products");Check(JsonSerializer.Serialize(page).Length<100000,"Returned full catalogue instead of one page");var elapsed=sw.ElapsedMilliseconds;
    var backups=new LocalBackups(env);var backup=backups.Create();backups.Verify(backup.Name);Check(new DemoStore(env).CatalogRead(s=>s.Catalog.Listings.ContainsKey(native.Key)),"Shared catalogue metadata did not persist");
    // Production rule: polyamide-based blends cannot be produced, under any name composition labels use.
    foreach(var text in new[]{"%80 Polyamid %20 Elastan","POLİAMİD karışım","Polyamide blend","Naylon çorap","PA66 iplik"})Check(ProductionRules.CompositionHasPolyamide(text),"Polyamide not detected: "+text);
    foreach(var text in new[]{"%100 Pamuk","Keten karışımı","Paket","Spa 6 kişilik"})Check(!ProductionRules.CompositionHasPolyamide(text),"False polyamide match: "+text);
    Check(!ProductionRules.TextHasPolyamide("Naylon poşette gönderilir."),"Packaging nylon in a description must not block a product");
    Reject(()=>hub.Edit(original.Id,Change(original,original.Stock) with{Description="%78 Poliamid %22 Elastan"}),"production_rule");
    Reject(()=>hub.Edit(original.Id,Change(original,original.Stock) with{Name="Naylon tayt"}),"production_rule");
    Console.WriteLine($"PASS shared catalogue domain: legacy preservation, exact store links, atomic import rollback, demo/live separation, decimal/unknown values, live-write guard, stale queued versions, restart recovery, 100k products with 25-row response ({elapsed} ms including fixture persistence), encrypted backup coverage.");
}finally{var absolute=Path.GetFullPath(folder);var root=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(!absolute.StartsWith(root,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(absolute).StartsWith("marketpilot-catalog-check-"))throw new Exception("Unsafe cleanup path");Directory.Delete(absolute,true);}
sealed class TestEnvironment:IWebHostEnvironment{public string ApplicationName{get;set;}="MarketPilot.Web";public string EnvironmentName{get;set;}="Development";public string ContentRootPath{get;set;}="";public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();public string WebRootPath{get;set;}="";public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();}
