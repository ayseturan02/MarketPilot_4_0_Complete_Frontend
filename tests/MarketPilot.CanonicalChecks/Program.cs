using System.Text.Json;
using MarketPilot.Web.Contracts;
using MarketPilot.Web.Integrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using ContractOrder=MarketPilot.Web.Contracts.MarketplaceOrder;
using ContractShipment=MarketPilot.Web.Contracts.Shipment;

static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
static void Reject(Action action,int status=400){try{action();}catch(IntegrationException e){Check(e.Status==status,"Wrong rejection: "+e.Message);return;}throw new Exception("Expected rejection");}
var root=Path.Combine(Path.GetTempPath(),"marketpilot-canonical-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
var time=DateTimeOffset.UtcNow.AddMinutes(-1);
RecordContext C(string sourceId,long version=1,string source="nebim-v3",string? account=null,string? key=null)=>new("default",source,sourceId,account,"test-correlation",key??Guid.NewGuid().ToString("N"),version,time,time.AddSeconds(1));
var env=new TestEnvironment{ContentRootPath=root};var config=new ConfigurationBuilder().Build();var db=new DemoStore(env);var store=new CanonicalStore(db,config);
try
{
    var product=new CanonicalProduct("product-1",C("erp-product-1"),"Pamuklu Tişört","Giyim / Üst Giyim / Tişört","Tekstil");
    Check(store.Apply(product).State=="applied","Product insert");Check(store.Apply(product).State=="duplicate","Exact replay");
    Check(store.Apply(product with{Context=product.Context with{CorrelationId="another",ReceivedAt=time.AddSeconds(2)}}).State=="duplicate","Transport timestamps do not change business payload");
    Reject(()=>store.Apply(product with{Name="Different same key"}),409);
    var updated=product with{Name="Güncel tişört",Context=C("erp-product-1",2)};Check(store.Apply(updated,1).State=="applied","Versioned update");
    Check(store.Apply(product with{Context=C("erp-product-1",1)}).State=="stale","Out of order");
    Reject(()=>store.Apply(updated with{Name="Wrong equal version",Context=C("erp-product-1",2)}),409);
    Reject(()=>store.Apply(updated with{Context=C("erp-product-1",3)},1),409);
    var missing=product with{Id="missing-source",Context=C("")};Check(store.Apply(missing).State=="manual-mapping","Missing source identity goes to review");
    Check(store.Apply(missing).QueueId==store.Reviews().Single().Id,"Queue replay must not duplicate review");
    Check(store.Apply(product with{Id="product-2",Context=C("erp-product-2")}).State=="applied","Same name is not identity");
    var variant=new ProductVariant("v1",C("erp-v1"),"product-1","SAME-SKU","Ekru","M","EA",true,[new("BOX",12)]);
    Check(store.Apply(variant).State=="applied","Variant");
    Check(store.Apply(new Barcode("b1",C("barcode-1"),"v1","8691000000001","EAN13","EA")).State=="applied","First barcode");
    Check(store.Apply(new Barcode("b2",C("barcode-2"),"v1","8691000000099","EAN13","BOX")).State=="applied","Same SKU, different barcodes, explicit same variant");
    Check(store.Apply(variant with{Id="v-same-sku",Context=C("erp-v-conflict"),Size="L"}).State=="manual-mapping","Same SKU must not merge distinct variants");
    Check(store.Apply(variant with{Id="v2",Sku="OTHER-SKU",Context=C("erp-v2")}).State=="applied","Other variant");
    Check(store.Apply(variant with{Id="missing-product-variant",ProductId="not-an-identity",Sku="NO-MATCH",Context=C("missing-product-variant")}).State=="manual-mapping","Missing referenced identity is queued without name matching");
    Check(store.Apply(new Barcode("b-conflict",C("barcode-conflict"),"v2","8691000000001","EAN13","EA")).State=="manual-mapping","Same barcode different variant");
    Check(store.Apply(new Barcode("b1",C("barcode-1",2),"v2","8691000000001","EAN13","EA")).State=="manual-mapping","Updating the original barcode ID must not silently move its variant");
    Check(store.Apply(new Warehouse("w1",C("wms-main",source:"wms"),"MAIN","Ana depo")).State=="applied","Main depot");
    Check(store.Apply(new Warehouse("w2",C("wms-outlet",source:"wms"),"OUTLET","Outlet depo")).State=="applied","Second depot");
    Check(store.Available("v2","w1").Quantity==null,"Unknown physical stock is not reported as zero");
    Check(store.Apply(new StockPosition("stock1",C("stock-1",source:"wms"),"v1","w1",10,"EA",1)).State=="applied","Physical stock");
    Check(store.Apply(new StockPosition("stock2",C("stock-2",source:"wms"),"v1","w2",2,"BOX")).State=="applied","Box conversion");
    Check(store.Available("v1","w1").Quantity==9&&store.Available("v1","w2").Quantity==24,"Depots isolated and units normalized");
    Reject(()=>store.Apply(new StockPosition("stock3",C("stock-3",source:"wms"),"v1","w1",12,"EA")));
    Reject(()=>store.Apply(new StockPosition("stock-frac",C("stock-frac",source:"wms"),"v1","w1",1.5m,"EA")));
    Check(store.Apply(variant with{Id="fabric",Sku="FABRIC",BaseUnit="M",WholeUnitsOnly=false,Conversions=[new("CM",0.01m)],Context=C("erp-fabric")}).State=="applied","Decimal unit variant");
    Check(store.Apply(new StockPosition("fabric-stock",C("fabric-stock",source:"wms"),"fabric","w1",125.5m,"CM")).State=="applied","Decimal stock");Check(store.Available("fabric","w1").Quantity==1.255m,"Exact decimal conversion");
    var hold=new StockReservation("hold",C("hold",source:"marketpilot"),"v1","w1","order",4,"EA");store.Apply(hold);Check(store.Apply(hold).State=="duplicate"&&store.Available("v1","w1").Quantity==5,"Reservation replay");
    Reject(()=>store.Apply(variant with{Context=C("erp-v1",2),WholeUnitsOnly=false}),409);
    Reject(()=>store.Apply(new StockReservation("too-many",C("too-many",source:"marketpilot"),"v1","w1","order",6,"EA")));
    var success=0;Parallel.For(0,12,i=>{try{store.Apply(new StockReservation("parallel-"+i,C("parallel-"+i,source:"marketpilot"),"v1","w1","order",5,"EA"));Interlocked.Increment(ref success);}catch(IntegrationException){}});Check(success==1,"Concurrent capacity checked under lock");
    foreach(var (id,account,listing) in new[]{("m1","seller-a","listing-a"),("m2","seller-a","listing-b"),("m3","seller-b","listing-a")})Check(store.Apply(new ExternalProductMapping(id,C(listing,source:"trendyol",account:account),"v1",listing,"SAME-SKU","8691000000001")).State=="applied","Independent listings and accounts");
    Check(store.Apply(new ExternalProductMapping("m4",C("listing-conflict",source:"trendyol",account:"seller-a"),"v2","listing-a","OTHER-SKU",null)).State=="manual-mapping","Cannot steal listing");
    Check(store.Apply(new ExternalProductMapping("m1",C("listing-a",2,source:"trendyol",account:"seller-a"),"v2","listing-a","OTHER-SKU",null)).State=="manual-mapping","Cannot retarget existing mapping with a higher version");
    Reject(()=>store.Apply(product with{Context=C("foreign") with{TenantId="tenant-b"}}),403);
    var foreign=new CanonicalStore(db,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Workspace:BusinessId","tenant-b"}}).Build());Reject(()=>foreign.Available("v1","w1"),409);
    var order=new ContractOrder("o1",C("native-order",source:"trendyol",account:"seller-a"),"TY-1","new","TRY",[new("line1","v1","native-line","SAME-SKU","8691000000001",2,"EA",100,"TRY")],time);store.Apply(order);
    store.Apply(new PriceList("price1",C("price1"),"Perakende",[new("v1",100,"TRY","EA",true,10)],time));
    store.Apply(new FulfillmentRequest("f1",C("f1",source:"marketpilot"),"o1","w1","requested",[new("line1","v1",2,"EA")]));
    store.Apply(new ContractShipment("sh1",C("sh1",source:"cargo"),"f1","w1","Demo Kargo","TRACK",null,"prepared",[new("line1","v1",2,"EA")]));
    store.Apply(new ReturnRequest("r1",C("r1",source:"wms"),"o1","w1","requested",[new("line1","v1",1,"EA","Beden uymadı")]));
    store.Apply(new InvoiceReference("i1",C("i1",source:"invoice"),"o1","FTR-1","invoice","draft","TRY",200));
    var evt=new IntegrationEvent("e1",C("e1",source:"marketpilot"),"product.changed","product","product-1",1,JsonSerializer.SerializeToElement(new{version=2,price=100.00m}));store.Apply(evt);
    using(var reordered=JsonDocument.Parse("{\"price\":100,\"version\":2}"))Check(store.Apply(evt with{Payload=reordered.RootElement.Clone()}).State=="duplicate","JSON property order and decimal formatting must not change replay identity");
    var cp=new SyncCheckpoint("cp1",C("cp1",source:"trendyol",account:"seller-a"),"orders","opaque-token",10,time);store.Apply(cp);
    Reject(()=>store.Apply(cp with{Context=C("cp1",2,source:"trendyol",account:"seller-a"),LastSourceVersion=9}));
    var before=File.ReadAllText(Path.Combine(root,"Data","demo-store.json"));Reject(()=>store.Apply(new PriceList("bad-price",C("bad-price"),"Bad",[new("v1",1,"TRY","UNKNOWN",true)],time)));Check(File.ReadAllText(Path.Combine(root,"Data","demo-store.json"))==before,"Invalid writes roll back whole JSON state");
    var reopened=new CanonicalStore(new DemoStore(env),config);Check(reopened.Apply(updated).State=="duplicate","Receipts survive restart");Check(reopened.Available("v1","w2").Quantity==24,"Stock survives restart");
    var review=reopened.Reviews().First(r=>r.Reason=="barcode-shared-by-variants");Check(reopened.Resolve(review.Id,"v2").ResolvedVariantId=="v2","Manual review resolution");Check(reopened.Resolve(review.Id,"v2").State=="resolved","Manual resolution replay");Reject(()=>reopened.Resolve(review.Id,"v1"),409);
    Check(CanonicalStore.Types.Count==15,"All standalone model resources registered; order lines embedded and ATS derived");
    Console.WriteLine("PASS canonical contracts: all 17 models, SKU/barcode ambiguity, independent depots/listings/sellers/tenants, unit conversion, decimal/integer inventory, atomic reservations, missing identities/manual review, expected versions, replay, stale/equal versions, checkpoints, restart and rollback. JSON only; no external calls.");
}
finally{var resolved=Path.GetFullPath(root);if(!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(resolved).StartsWith("marketpilot-canonical-"))throw new Exception("Unsafe cleanup");Directory.Delete(resolved,true);}
sealed class TestEnvironment:IWebHostEnvironment
{
    public string EnvironmentName{get;set;}="Production";public string ApplicationName{get;set;}="CanonicalChecks";public string WebRootPath{get;set;}="";public string ContentRootPath{get;set;}="";public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();
}
