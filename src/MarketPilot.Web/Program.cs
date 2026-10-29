using System.Text.Json;
using MarketPilot.Web.Enterprise;
using MarketPilot.Web.Workspace;
using MarketPilot.Web.Analytics;
using System.Text.Json.Serialization;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Local;
using MarketPilot.Web.Catalog;
using MarketPilot.Web.Webhooks;
using MarketPilot.Web.Integrations.CentralStock;
using MarketPilot.Web.Integrations.Demo;
using MarketPilot.Web.Synchronization;

var builder = WebApplication.CreateBuilder(args);
var configuredDeploymentMode = builder.Configuration["Deployment:Mode"];
if (configuredDeploymentMode is not null &&
    configuredDeploymentMode is not ("local" or "public"))
    throw new InvalidOperationException("Deployment:Mode yalnızca 'local' veya 'public' olabilir.");
var publicDeployment = LocalAccess.IsPublicDeployment(builder.Configuration);
if (publicDeployment)
{
    var allowedHosts = builder.Configuration["AllowedHosts"];
    if (builder.Environment.IsDevelopment() || string.IsNullOrWhiteSpace(allowedHosts) ||
        allowedHosts.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(host => host == "*"))
        throw new InvalidOperationException("İnternet dağıtımında Development ortamı kapalı olmalı ve AllowedHosts açıkça tanımlanmalıdır.");

    if (!IPAddress.TryParse(builder.Configuration["Deployment:TrustedProxyIp"], out var trustedProxy))
        throw new InvalidOperationException("İnternet dağıtımında Deployment:TrustedProxyIp geçerli bir proxy IP adresi olmalıdır.");

    var bootstrapGateway = builder.Configuration["Deployment:BootstrapGatewayIp"];
    if (bootstrapGateway is not null && !IPAddress.TryParse(bootstrapGateway, out _))
        throw new InvalidOperationException("Deployment:BootstrapGatewayIp geçerli bir IP adresi olmalıdır.");

    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(trustedProxy);
        options.ForwardLimit = 1;
        options.RequireHeaderSymmetry = true;
    });
}
// Held for the process lifetime; fails fast before any store reads or writes the Data folder.
builder.Services.AddSingleton(DataDirectoryLock.Acquire(builder.Environment.ContentRootPath));
// DNS rebinding: a foreign site resolving to 127.0.0.1 sends its own Host header, so only local host names are accepted unless AllowedHosts is configured.
if (!publicDeployment && (string.IsNullOrWhiteSpace(builder.Configuration["AllowedHosts"]) || builder.Configuration["AllowedHosts"] == "*")) builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase; o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()); });
builder.Services.AddSingleton<DemoStore>();
builder.Services.AddCatalog();
builder.Services.AddOrderWebhooks();
builder.Services.AddMarketplaceIntegrations(builder.Configuration);
builder.AddLocalAccess();
builder.Services.AddLocalReliability();
builder.Services.AddBackgroundSynchronization();
builder.Services.AddEnterprise();
builder.Services.AddSingleton<WorkspaceHub>();
builder.Services.AddOperationalPreparation();
builder.Services.AddSingleton<BusinessWorkspaces>();builder.Services.AddHostedService<BusinessWorkspaceWorker>();
var app = builder.Build();
if (publicDeployment)
{
    app.UseForwardedHeaders();
    app.UseHsts();
}
app.Services.GetRequiredService<CatalogHub>().Ensure();
app.Use(async(context,next)=>
{
    var h=context.Response.Headers;
    h.XContentTypeOptions="nosniff";h.XFrameOptions="DENY";h["Referrer-Policy"]="no-referrer";h["Cross-Origin-Opener-Policy"]="same-origin";
    h.ContentSecurityPolicy="default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; img-src 'self' https: data:; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    await next();
});
app.Use(async(context,next)=>
{
    try{await next();}
    catch(OperationCanceledException)when(context.RequestAborted.IsCancellationRequested){}
    catch(Exception)
    {
        try{context.RequestServices.GetRequiredService<LocalAudit>().Add("error","request","Bir sunucu isteği tamamlanamadı.");}catch(Exception){}
        if(!context.Response.HasStarted){context.Response.Clear();context.Response.StatusCode=500;await context.Response.WriteAsJsonAsync(new{error="İşlem tamamlanamadı. Sistem olaylarını kontrol et."});}else context.Abort();
    }
});
app.UseLocalAccess();
app.UseBusinessWorkspaces();
app.MapLocalReliability();
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapMarketplaceIntegrations();
app.MapListingSearch();
app.MapCatalog();
app.MapOrderWebhooks();
app.MapBackgroundSynchronization();
app.MapAnalyticsWorkspace();
app.MapSharedWorkspace();
app.MapOperationalPreparation();
app.MapEnterprise();

app.MapGet("/api/health", (IWebHostEnvironment env, ReliabilityState reliability, IConfiguration configuration) => {
    var storage = File.Exists(Path.Combine(env.ContentRootPath, "Data", "demo-store.json"));
    var queue = reliability.LastQueueError is null && (reliability.LastQueueTick is null || DateTimeOffset.TryParse(reliability.LastQueueTick, out var tick) && DateTimeOffset.UtcNow - tick < TimeSpan.FromSeconds(30));
    var backup = reliability.LastBackupError is null;
    return Results.Json(new { status = storage && queue && backup ? "ok" : "degraded", mode = LocalAccess.IsPublicDeployment(configuration) ? "public" : "local", checks = new { storage, queue, backup }, timestamp = DateTimeOffset.UtcNow }, statusCode: storage && queue && backup ? 200 : 503);
});
app.MapGet("/api/sync/status", (DemoWorkspace demo, CentralStockService stock, DemoStore db, OrderSynchronization automation, StockOutbox outbox) => Results.Ok(new {
    demo = demo.QueueStatus(), stock = stock.Status(), automation = automation.Status(), outbox = outbox.Status(),
    catalog = db.CatalogRead(s => new { pending = s.Catalog.Jobs.Count(j => j.State == "pending"), failed = s.Catalog.Jobs.Count(j => j.State == "failed"), completed = s.Catalog.Jobs.Count(j => j.State == "completed") })
}));
app.MapGet("/api/overview", (OperationsHub hub) => Results.Ok(hub.Overview()));
app.MapGet("/api/products", (DemoStore db) => Results.Ok(db.SearchProducts(new ListingQuery(Size:50)).Items));
app.MapPost("/api/products", () => Results.Json(new { error = "Yeni ürün oluşturma kapalı. Ürünler Nebim V3 kaynağından alınır." }, statusCode: 405));
app.MapPut("/api/products/{id}", (DemoStore db,string id, ProductPatch patch) => {
    if (patch.Price is < 0 or > 10000000 || patch.Stock is < 0 or > 10000000 || patch.Status is not null && patch.Status is not ("Aktif" or "Pasif" or "Arşiv"))
        return Results.BadRequest(new { error = "Fiyat, stok veya ürün durumu geçersiz. Hiçbir alan değiştirilmedi." });
    return db.CatalogRead(s=>s.Products.Any(x=>x.Id==id))
        ? Results.Conflict(new {error="Ürün ana verisi salt okunurdur; değişiklik Nebim V3 kaynağından alınmalıdır."})
        : Results.NotFound(new {error="Ürün bulunamadı."});
});
app.MapGet("/api/orders", (OperationsHub hub)=>Results.Ok(hub.Orders(new ListingQuery(Size:50)).Items));
app.MapPut("/api/orders/{id}/status", (DemoStore db,OperationsHub hub,string id,OrderPatch patch)=>{
    var allowed=new[]{"Yeni","Hazırlanıyor","Kargolandı","Tamamlandı","İptal","İade"};
    if(!allowed.Contains(patch.Status))return Results.BadRequest(new{error="Geçersiz sipariş durumu."});
    var existingOrder=hub.AllOrders().FirstOrDefault(x=>x.Id==id);
    if(existingOrder is null)return Results.NotFound();
    if(existingOrder.SourceMode!="local")return Results.Conflict(new{error="Pazaryeri sipariş durumu WMS/kargo kaynağından alınmalıdır."});
    try {
        var order=db.Modify(s=>{var value=s.Orders.FirstOrDefault(x=>x.Id==id);if(value==null)return null;LocalOrderStock.ChangeStatus(s,value,patch.Status);db.Event(s,"Sipariş durumu",$"{value.Id} → {patch.Status}");return value;});
        return order is null?Results.NotFound():Results.Ok(order);
    } catch(ArgumentException error){return Results.BadRequest(new{error=error.Message});}
});
app.MapGet("/api/connections", (DemoStore db)=>Results.Ok(db.Read().Connections));
app.MapPut("/api/connections/{id}",(DemoStore db,string id,ConnectionPatch patch)=>{
    var c=db.Modify(s=>{var v=s.Connections.FirstOrDefault(x=>x.Id==id);if(v==null)return null;v.Enabled=patch.Enabled;db.Event(s,"Bağlantı değiştirildi",$"{v.Name}: {(v.Enabled?"Etkin":"Devre dışı")}");return v;});return c is null?Results.NotFound():Results.Ok(c);
});
app.MapPost("/api/sync/demo", async (DemoStore db, CentralStockService stock, DemoWorkspace demo, CancellationToken ct) => {
    if (stock.RealSource) return Results.Conflict(new { error = "Bu düğme yalnızca demo stok kaynağıyla çalışır." });
    if (!await stock.RunAsync(ct)) return Results.Conflict(new { error = "Senkronizasyon zaten çalışıyor." });
    if (!stock.LastRunSucceeded) return Results.Conflict(new { error = "Senkronizasyon durduruldu. Merkezi stok ekranındaki uyarıları kontrol et." });
    db.Modify(s => { s.LastSync = DateTimeOffset.UtcNow; db.Event(s, "Demo senkronizasyon", "Kaynak stok okundu ve demo kanalların güncellemeleri arka plan kuyruğuna alındı."); return true; });
    return Results.Ok(new { message = "Kaynak stok okundu. Kanal güncellemeleri arka planda işleniyor.", queue = demo.QueueStatus() });
});
app.MapGet("/api/activity",(OperationsHub hub)=>Results.Ok(hub.Activities()));
app.MapGet("/api/settings",(DemoStore db)=>Results.Ok(db.Read().Settings));
app.MapPut("/api/settings",(DemoStore db,Settings settings)=>{if(string.IsNullOrWhiteSpace(settings.BusinessName)||settings.BusinessName.Length>200||settings.Currency is null||!System.Text.RegularExpressions.Regex.IsMatch(settings.Currency,"^[A-Z]{3}$")||settings.LowStockThreshold is <0 or >10000000)return Results.BadRequest(new{error="İşletme adı, para birimi veya stok eşiği geçersiz."});db.Modify(s=>{s.Settings=settings;db.Event(s,"Ayarlar güncellendi","Tercihler kaydedildi");return true;});return Results.Ok(settings);});
app.MapGet("/api/export/products.csv", (DemoStore db) =>
    Results.File(ProductCsv.Create(db.Read().Products.Where(p=>p.SourceSystem==ProductOwnershipPolicy.ErpSource)), "text/csv", "urunler.csv"));

// MarketPilot 2.0: internal stock ledger, barcodes and demo shipping records.
app.MapGet("/api/stock/movements", (DemoStore db) => Results.Ok(db.Read().StockMovements));
app.MapPost("/api/stock/movements", () => Results.Conflict(new {error="Stok hareketleri WMS/depo kaynağından alınmalıdır; yerel stok yazımı kapalıdır."}));
app.MapGet("/api/shipments",(OperationsHub hub)=>Results.Ok(hub.Shipments(new ListingQuery(Size:50)).Items));
app.MapPost("/api/shipments",(DemoStore db,ShipmentDraft draft)=>{
    if(string.IsNullOrWhiteSpace(draft.OrderId)||string.IsNullOrWhiteSpace(draft.Carrier)||string.IsNullOrWhiteSpace(draft.TrackingNumber))
        return Results.BadRequest(new {error="Sipariş, kargo firması ve takip numarası gerekli."});
    var item=db.Modify(s=>{
        if(!s.Orders.Any(o=>o.Id==draft.OrderId&&o.SourceMode=="local")||s.Shipments.Any(x=>x.OrderId==draft.OrderId))return null;
        var sh=new Shipment{Id=Guid.NewGuid().ToString("N")[..10],OrderId=draft.OrderId,Carrier=draft.Carrier.Trim(),TrackingNumber=draft.TrackingNumber.Trim(),Status="Hazırlanıyor",CreatedAt=DateTimeOffset.UtcNow};
        s.Shipments.Insert(0,sh);db.Event(s,"Gönderi oluşturuldu",$"{sh.OrderId} · {sh.Carrier}");return sh;
    });
    return item is null?Results.Conflict(new{error="Sipariş bulunamadı veya gönderisi zaten var."}):Results.Ok(item);
});
app.MapPut("/api/shipments/{id}/status",(DemoStore db,string id,ShipmentStatusPatch patch)=>{
    if(!new[]{"Hazırlanıyor","Kargoya verildi","Teslim edildi","İptal"}.Contains(patch.Status))return Results.BadRequest(new{error="Geçersiz durum."});
    var item=db.Modify(s=>{var sh=s.Shipments.FirstOrDefault(x=>x.Id==id);if(sh is null)return null;var linked=s.Orders.FirstOrDefault(o=>o.Id==sh.OrderId);if(linked is null||linked.SourceMode!="local")return null;sh.Status=patch.Status;if(patch.Status is "Kargoya verildi" or "Teslim edildi")LocalOrderStock.ChangeStatus(s,linked,patch.Status=="Teslim edildi"?"Tamamlandı":"Kargolandı");db.Event(s,"Gönderi güncellendi",$"{sh.OrderId}: {patch.Status}");return sh;});
    return item is null?Results.NotFound():Results.Ok(item);
});
app.MapGet("/api/barcodes",(DemoStore db)=>Results.Ok(db.Read().Products.Where(p=>p.SourceSystem==ProductOwnershipPolicy.ErpSource).Select(p=>new {p.Id,p.Name,p.Sku,p.Barcode})));
app.MapPut("/api/products/{id}/barcode",(DemoStore db,string id,BarcodePatch patch)=>db.CatalogRead(s=>s.Products.Any(p=>p.Id==id))
    ?Results.Conflict(new{error="Barkod ana verisi salt okunurdur; değişiklik Nebim V3 kaynağından alınmalıdır."})
    :Results.NotFound(new{error="Ürün bulunamadı."}));

app.MapFallback("/api/{**path}",()=>Results.NotFound(new{error="API işlemi bulunamadı."}));
app.MapFallbackToFile("index.html");
app.Run();

public record BarcodePatch(string? Barcode);
public record StockMovementDraft(string ProductId,int Delta,string Reason);
public record ShipmentDraft(string OrderId,string Carrier,string TrackingNumber);
public record ShipmentStatusPatch(string Status);
public class StockMovement {public string Id{get;set;}="";public string ProductId{get;set;}="";public string ProductName{get;set;}="";public decimal Delta{get;set;}public decimal Balance{get;set;}public string Reason{get;set;}="";public DateTimeOffset At{get;set;}=DateTimeOffset.UtcNow;}
public class Shipment {public List<ExternalReference> ExternalReferences{get;set;}=[];public string Source{get;set;}="";public string? LabelUrl{get;set;}public DateTimeOffset UpdatedAt{get;set;}=DateTimeOffset.UtcNow;public string StoreId{get;set;}="";public string Id{get;set;}="";public string OrderId{get;set;}="";public string Carrier{get;set;}="";public string TrackingNumber{get;set;}="";public string Status{get;set;}="Hazırlanıyor";public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.UtcNow;}
public class Product {public List<ExternalReference> ExternalReferences{get;set;}=[];public StockValueState? StockState{get;set;}public StockValueState ReservationState{get;set;}=StockValueState.NotProvided;public decimal ReservedQuantity{get;set;}public decimal? SellableStock{get;set;}public StockValueState SellableStockState{get;set;}=StockValueState.NotProvided;public string VariantId{get;set;}="";public List<string> Images{get;set;}=new();public Dictionary<string,string> Attributes{get;set;}=new();public string Brand{get;set;}="";public string Description{get;set;}="";public string Currency{get;set;}="TRY";public bool StockKnown{get;set;}=true;public bool PriceKnown{get;set;}=true;public bool? VatIncluded{get;set;}=true;public string SourceSystem{get;set;}="";public string SourceMode{get;set;}="local";public int Version{get;set;}=1;public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.UtcNow;public string Barcode{get;set;}="";public string Id{get;set;}="";public string Name{get;set;}="";public string Sku{get;set;}="";public string Category{get;set;}="";public decimal Price{get;set;}public decimal Stock{get;set;}public string Image{get;set;}="📦";public List<string> Channels{get;set;}=new();public string Status{get;set;}="Aktif";}
public record ProductPatch(decimal? Price,int? Stock,string? Status);
public class Order {public List<ExternalReference> ExternalReferences{get;set;}=[];public MarketplaceAddress? ShipmentAddress{get;set;}public string CustomerEmail{get;set;}="";public string SourceIdentity{get;set;}="local";public MarketplaceInvoice? Invoice{get;set;}public MarketplaceCargo? Cargo{get;set;}public int Version{get;set;}=1;public bool StockManaged{get;set;}public bool StockApplied{get;set;}public string RequestId{get;set;}="";public string RequestFingerprint{get;set;}="";public bool TotalKnown{get;set;}=true;public string StoreId{get;set;}="";public string PackageId{get;set;}="";public string ChannelOrderNumber{get;set;}="";public string SourceMode{get;set;}="local";public string Currency{get;set;}="TRY";public OperationLine[] Lines{get;set;}=[];public string Id{get;set;}="";public string Customer{get;set;}="";public string Channel{get;set;}="";public string Product{get;set;}="";public decimal Total{get;set;}public int Quantity{get;set;}public string Status{get;set;}="Yeni";public DateTimeOffset Date{get;set;}=DateTimeOffset.Now;}
public record OrderPatch(string Status);
public class Connection {public string Id{get;set;}="";public string Name{get;set;}="";public string Initial{get;set;}="";public string Color{get;set;}="#888";public bool Enabled{get;set;}public bool Available{get;set;}public string Note{get;set;}="";}
public record ConnectionPatch(bool Enabled);
public class Activity {public string Title{get;set;}="";public string Detail{get;set;}="";public DateTimeOffset At{get;set;}=DateTimeOffset.Now;}
public class Settings {public string BusinessName{get;set;}="İnternet Tekstil";public string Currency{get;set;}="TRY";public int LowStockThreshold{get;set;}=10;public bool EmailNotifications{get;set;}=true;public bool SyncNotifications{get;set;}=true;}
public class Database {public OperationalState Operations{get;set;}=new();public Dictionary<string,ErpReceipt> ErpReceipts{get;set;}=new();public List<WorkspaceRecord> WorkspaceRecords{get;set;}=new();public Dictionary<string,string> WorkspaceActions{get;set;}=new();public Dictionary<string,ProductCost> ProductCosts{get;set;}=new();public CatalogState Catalog{get;set;}=new();public List<StockMovement> StockMovements{get;set;}=new();public List<Shipment> Shipments{get;set;}=new();public List<Product> Products{get;set;}=new();public List<Order> Orders{get;set;}=new();public List<Connection> Connections{get;set;}=new();public List<Activity> Activity{get;set;}=new();public Settings Settings{get;set;}=new();public DateTimeOffset LastSync{get;set;}=DateTimeOffset.Now.AddMinutes(-18);}
public partial class DemoStore {
 private readonly object _gate=new();private readonly string _file;private readonly JsonSerializerOptions _json=new(JsonSerializerDefaults.Web){WriteIndented=true}; private Database _data; private long _revision; private readonly string _instance=Guid.NewGuid().ToString("N"); public string Revision => _instance + ":" + Interlocked.Read(ref _revision);
 public DemoStore(IWebHostEnvironment env){var folder=Path.Combine(env.ContentRootPath,"Data");Directory.CreateDirectory(folder);_file=Path.Combine(folder,"demo-store.json");_data=File.Exists(_file)?JsonSerializer.Deserialize<Database>(File.ReadAllText(_file),_json)??throw new InvalidOperationException("Local catalogue file cannot be read."):Seed();foreach(var product in _data.Products){product.StockState??=product.StockKnown?StockValueState.Known:StockValueState.Unknown;product.ReservedQuantity=Math.Max(0,product.ReservedQuantity);product.ExternalReferences??=[];}foreach(var order in _data.Orders)order.ExternalReferences??=[];foreach(var shipment in _data.Shipments)shipment.ExternalReferences??=[];foreach(var record in _data.WorkspaceRecords)record.ExternalReferences??=[];DemoErpCatalogFixture.Apply(_data);foreach(var order in _data.Orders.Where(o=>o.SourceMode=="local")){order.StockManaged=false;order.StockApplied=false;}RetiredChannelCleanup.Apply(_data);Save();}
 public Database Read(){lock(_gate){return JsonSerializer.Deserialize<Database>(JsonSerializer.Serialize(_data,_json),_json)!;}}
 public T Modify<T>(Func<Database,T> action){lock(_gate){var before=JsonSerializer.Serialize(_data,_json);try{var r=action(_data);Save();return r;}catch{_data=JsonSerializer.Deserialize<Database>(before,_json)!;throw;}}}
 public void Event(Database s,string title,string detail){s.Activity.Insert(0,new Activity{Title=title,Detail=detail});s.Activity=s.Activity.Take(40).ToList();}
 private void Save(){var temp=_file+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(_data,_json));File.Move(temp,_file,true);Interlocked.Increment(ref _revision);}
 public object Overview(){var d=Read();var orders=d.Orders.Where(o=>o.Status!="İptal").ToList();var products=d.Products.Where(p=>p.SourceSystem==ProductOwnershipPolicy.ErpSource).ToArray();return new{revenue=orders.Sum(o=>o.Total),orders=d.Orders.Count,products=products.Length,lowStock=products.Count(p=>p.StockKnown&&p.Stock<=d.Settings.LowStockThreshold),activeConnections=d.Connections.Count(c=>c.Enabled && c.Available),lastSync=d.LastSync,latestOrders=d.Orders.Take(5),activity=d.Activity.Take(5),channels=d.Orders.GroupBy(o=>o.Channel).Select(g=>new{name=g.Key,total=g.Sum(o=>o.Total),count=g.Count()}).OrderByDescending(x=>x.total).ToList()};}
 private static Database Seed(){var d=new Database();
 var items=new (string name,string sku,string category,decimal price,int stock,string icon,string[] channels)[]{
 ("Organik Pamuk Oversize Tişört","TSH-3002","Giyim / Tişört",599,86,"👕",new[]{"Trendyol"}),
 ("Keten Gömlek","GML-1018","Giyim / Gömlek",1099,42,"👔",new[]{"Amazon","Trendyol","n11"}),
 ("Dokuma Pantolon","PNT-1033","Giyim / Pantolon",1299,5,"👖",new[]{"Hepsiburada","Amazon"}),
 ("Fermuarlı Sweatshirt","SWT-4512","Giyim / Sweatshirt",799,235,"🧥",new[]{"n11"}),
 ("Triko Hırka","HRK-5551","Giyim / Hırka",1199,64,"🧶",new[]{"Trendyol","Hepsiburada"}),
 ("Pamuklu Basic Tişört","TSH-6090","Giyim / Tişört",499,19,"👕",new[]{"Amazon"}),
 ("Kadın Günlük Elbise","ELB-3339","Giyim / Elbise",899,102,"👗",new[]{"Trendyol","n11"}),
 ("Kapüşonlu Mont","MNT-2040","Giyim / Dış Giyim",1749,8,"🧥",new[]{"Hepsiburada","n11"}),
 ("Erkek Polo Yaka Tişört","POL-1001","Giyim / Tişört",749,124,"👕",new[]{"Trendyol","Amazon"})};
 int i=1;foreach(var t in items)d.Products.Add(new Product{Id=$"p{i++:000}",Name=t.name,Sku=t.sku,Category=t.category,Price=t.price,Stock=t.stock,Image=t.icon,Channels=t.channels.ToList(),SourceSystem=ProductOwnershipPolicy.ErpSource,SourceMode="demo"});
 var customers=new[]{"Zeynep Kaya","Mehmet Yılmaz","Elif Demir","Burak Aydın","Sude Şahin","Ahmet Arslan","Deniz Öztürk","Ceren Koç","Can Yıldız","Merve Öz"};var channels=new[]{"Trendyol","Hepsiburada","Amazon","n11"};var statuses=new[]{"Yeni","Hazırlanıyor","Kargolandı","Tamamlandı","Tamamlandı","Tamamlandı","İptal"};var rng=new Random(47);
 for(int j=0;j<32;j++){var p=d.Products[rng.Next(d.Products.Count)];var qty=rng.Next(1,4);d.Orders.Add(new Order{Id=$"MP-{260000+j}",Customer=customers[j%customers.Length],Channel=channels[j%channels.Length],Product=p.Name,Total=p.Price*qty,Quantity=qty,Status=statuses[j%statuses.Length],Date=DateTimeOffset.Now.AddHours(-j*3-rng.Next(1,3))});}
 var cons=new (string,string,string,bool,string)[]{("Trendyol","T","#ff7c31",true,"Demo bağlantı · Gerçek API bağlı değil"),("Hepsiburada","H","#f49b22",true,"Demo bağlantı · Gerçek API bağlı değil"),("Amazon","a","#252b37",true,"Demo bağlantı · Gerçek API bağlı değil"),("n11","n","#d34a7b",false,"Bağlantı henüz yapılandırılmadı"),("Shopify","S","#76b64c",false,"Adaptör geliştirme aşamasında"),("ÇiçekSepeti","Ç","#ee5a69",false,"Adaptör geliştirme aşamasında"),("PrestaShop","P","#3482c6",false,"Adaptör geliştirme aşamasında"),("eBay","e","#527cda",false,"Adaptör geliştirme aşamasında")};
 int k=1;foreach(var c in cons)d.Connections.Add(new Connection{Id=$"c{k++}",Name=c.Item1,Initial=c.Item2,Color=c.Item3,Enabled=c.Item4,Available=c.Item4,Note=c.Item5});
 d.Activity=new(){new(){Title="Sipariş alındı",Detail="MP-260000 · Trendyol",At=DateTimeOffset.Now.AddMinutes(-6)},new(){Title="Stok kontrol edildi",Detail="9 giyim ürünü · Örnek veri",At=DateTimeOffset.Now.AddMinutes(-18)},new(){Title="Ürün kaynağından alındı",Detail="Organik Pamuk Oversize Tişört",At=DateTimeOffset.Now.AddHours(-1)},new(){Title="Kargo durumu güncellendi",Detail="MP-260002 · Örnek işlem",At=DateTimeOffset.Now.AddHours(-2)}};return d;
 }
}
