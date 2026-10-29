using System.Text.Json;
using MarketPilot.Web.Contracts;
using MarketPilot.Web.Enterprise.Nebim;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using ContractOrder=MarketPilot.Web.Contracts.MarketplaceOrder;
static void Check(bool value,string reason){if(!value)throw new Exception(reason);}
static async Task Reject(Func<Task> action,string code){try{await action();}catch(ErpProviderException e){Check(e.Error.Code==code,"Unexpected error "+e.Error.Code);return;}throw new Exception("Expected "+code);}
var folder=Path.Combine(Path.GetTempPath(),"marketpilot-nebim-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
try
{
    var env=new TestEnvironment{ContentRootPath=folder};var db=new DemoStore(env);var profile=new ErpConnectionProfile("mock-nebim","default",AllowMockWrites:true);var mock=new MockNebimProvider(db,profile);
    var products=await mock.ReadProductsAsync();Check(products.IsMock&&products.Data.Count==1,"Products");
    var variants=await mock.ReadVariantsAsync();Check(variants.Data.Single().ProductId==products.Data.Single().Id,"Variants linked");
    var identifiers=await mock.ReadIdentifiersAsync();Check(identifiers.Data.Barcodes.Single().VariantId==variants.Data.Single().Id&&identifiers.Data.Variants.Single().Sku==variants.Data.Single().Sku,"Barcode/SKU");
    Check((await mock.ReadPriceListsAsync()).Data.Single().Entries.Single().Amount==449,"Prices");
    Check((await mock.ReadWarehousesAsync()).Data.Count==2&&(await mock.ReadStockAsync()).Data.Count==2,"Depots/stock");
    Check((await mock.ReadDocumentReferencesAsync()).Data.Count==0&&(await mock.ReadEventsAsync()).Data.Count==0,"Supported empty results");
    var canonical=new CanonicalStore(db,new ConfigurationBuilder().Build());var sync=new NebimCanonicalSync(mock,canonical,db);await sync.Run(default);await sync.Run(default);
    Check(canonical.Available(variants.Data.Single().Id,"mock-nebim-main").Quantity==36&&canonical.Available(variants.Data.Single().Id,"mock-nebim-outlet").Quantity==12,"Canonical integration and conversion");
    Check((await mock.GetSyncStatusAsync()).Data.LastCanonicalSyncAt!=null,"Sync completion timestamp");
    var blockedProfile=profile with{Id="blocked"};var blockedProvider=new MockNebimProvider(db,blockedProfile,new(ErpReadCapability.All&~ErpReadCapability.Stock,ErpWriteCapability.None,true));
    await Reject(async()=>{await new NebimCanonicalSync(blockedProvider,canonical,db).Run(default);},"NotSupported");
    Check(!db.CatalogRead(s=>s.Contracts.Records.Values.Any(r=>r.GetRawText().Contains("blocked-product"))),"Unsupported read cannot partly import canonical master data");
    var time=DateTimeOffset.UtcNow;RecordContext Context(string id,long version=1)=>new("default","trendyol",id,"seller-a","workflow",id+"-"+version,version,time,time);
    var variant=variants.Data.Single();var order=new ContractOrder("order1",Context("native-order"),"ORDER-1","new","TRY",[new("line1",variant.Id,"native-line1",variant.Sku,identifiers.Data.Barcodes.Single().Value,2,"EA",449,"TRY")],time);
    var registered=await mock.RegisterOrderAsync(order);Check(registered.Data.Status=="registered","Register");Check((await mock.RegisterOrderAsync(order)).Data==registered.Data,"Idempotent registration");
    Check((await mock.RegisterOrderAsync(order with{Context=order.Context with{IdempotencyKey="new-key",CorrelationId="new-correlation"}})).Data==registered.Data,"Stable order identity across repeated imports");
    await Reject(async()=>{await mock.RegisterOrderAsync(order with{OrderNumber="different"});},"IdempotencyConflict");
    await Reject(async()=>{await mock.RegisterOrderAsync(order with{Id="foreign",Context=order.Context with{TenantId="other"}});},"TenantMismatch");
    Check((await mock.GetOrderStatusAsync("order1")).Data.Status=="registered","Status lookup");
    var document=(await mock.ReadDocumentReferencesAsync()).Data.Single();Check(document.OrderId==order.Id&&document.Amount==898&&document.Status=="draft","Mock document reference only");
    IntegrationEvent Event(string id,string type,long version)=>new(id,Context(id,version),type,"order",order.Id,1,JsonSerializer.SerializeToElement(new{reason="mock fixture"}));
    var correction=Event("correction","order.corrected",2);Check((await mock.HandleEventAsync(correction)).Data=="applied","Correction");Check((await mock.HandleEventAsync(correction)).Data=="duplicate","Event replay");
    await mock.HandleEventAsync(Event("cancel","order.cancelled",3));Check((await mock.GetOrderStatusAsync(order.Id)).Data.Status=="cancelled","Cancel event");
    Check((await mock.HandleEventAsync(Event("stale","order.corrected",1))).Data=="stale","Out of order event");
    await Reject(async()=>{await mock.HandleEventAsync(Event("same-version","order.corrected",3));},"VersionConflict");
    await mock.HandleEventAsync(Event("return","order.returned",4));Check((await mock.GetOrderStatusAsync(order.Id)).Data.Status=="returned","Return event");
    Check((await mock.ReadEventsAsync()).Data.Count==3,"Only applied events recorded");
    await Reject(async()=>{await mock.HandleEventAsync(Event("unsupported","product.magic",5));},"NotSupported");
    await Reject(async()=>{await mock.GetOrderStatusAsync("missing");},"NotFound");
    var readOnly=new MockNebimProvider(db,profile with{AllowMockWrites=false});await Reject(async()=>{await readOnly.RegisterOrderAsync(order);},"NotSupported");
    var unsupported=new MockNebimProvider(db,profile,new(ErpReadCapability.None,ErpWriteCapability.None,true));
    foreach(var action in new Func<Task>[] {async()=>{await unsupported.ReadProductsAsync();},async()=>{await unsupported.ReadVariantsAsync();},async()=>{await unsupported.ReadIdentifiersAsync();},async()=>{await unsupported.ReadPriceListsAsync();},async()=>{await unsupported.ReadWarehousesAsync();},async()=>{await unsupported.ReadStockAsync();},async()=>{await unsupported.GetOrderStatusAsync(order.Id);},async()=>{await unsupported.ReadDocumentReferencesAsync();},async()=>{await unsupported.ReadEventsAsync();},async()=>{await unsupported.GetSyncStatusAsync();},async()=>{await unsupported.HandleEventAsync(correction);}})await Reject(action,"NotSupported");
    var real=new MockNebimProvider(db,profile with{Mode="real"});Check(real.Capabilities.Read==ErpReadCapability.None&&!real.Capabilities.IsMock,"Real adapter cannot advertise unsupported capabilities");await Reject(async()=>{await real.ReadProductsAsync();},"NotSupported");
    await Reject(async()=>{await new MockNebimProvider(db,profile with{Enabled=false}).ReadProductsAsync();},"Disabled");
    var another=new MockNebimProvider(db,profile with{Id="other-profile"});Check((await another.GetSyncStatusAsync()).Data.LastSuccessfulReadAt==null,"Profile sync metadata isolated");await Reject(async()=>{await another.GetOrderStatusAsync(order.Id);},"NotFound");
    using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();try{await mock.ReadProductsAsync(cancelled.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}}
    var reopened=new MockNebimProvider(new DemoStore(env),profile);Check((await reopened.GetOrderStatusAsync(order.Id)).Data.Status=="returned","Restart resumes persistent mock state");Check((await reopened.HandleEventAsync(correction)).Data=="duplicate","Restart replay safety");
    Console.WriteLine("PASS mock Nebim provider: all read/write flows, canonical sync/units, order registration/status/documents, cancellation/return/correction events, idempotency/versioning, capability refusals, disabled/real modes, profile/tenant boundaries, cancellation and JSON restart. No network transport or native Nebim contract assumed.");
}
finally{var absolute=Path.GetFullPath(folder);if(!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(absolute).StartsWith("marketpilot-nebim-"))throw new Exception("Unsafe cleanup");Directory.Delete(absolute,true);}
sealed class TestEnvironment:IWebHostEnvironment
{
    public string EnvironmentName{get;set;}="Production";public string ApplicationName{get;set;}="NebimChecks";public string WebRootPath{get;set;}="";public string ContentRootPath{get;set;}="";public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();
}
