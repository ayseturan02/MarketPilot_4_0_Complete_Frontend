using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Local;

var builder = WebApplication.CreateBuilder(args);
// DNS rebinding: a foreign site resolving to 127.0.0.1 sends its own Host header, so only local host names are accepted unless AllowedHosts is configured.
if (string.IsNullOrWhiteSpace(builder.Configuration["AllowedHosts"]) || builder.Configuration["AllowedHosts"] == "*") builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase; o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()); });
builder.Services.AddSingleton<DemoStore>();
builder.Services.AddMarketplaceIntegrations(builder.Configuration);
builder.AddLocalAccess();
builder.Services.AddLocalReliability();
var app = builder.Build();
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
app.MapLocalReliability();
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapMarketplaceIntegrations();
app.MapListingSearch();

app.MapGet("/api/health", () => Results.Ok(new { status="ok", mode="demo", apiConnected=false, timestamp=DateTimeOffset.UtcNow }));
app.MapGet("/api/overview", (DemoStore db) => Results.Ok(db.Overview()));
app.MapGet("/api/products", (DemoStore db) => Results.Ok(db.SearchProducts(new ListingQuery(Size:50)).Items));
app.MapPost("/api/products", (DemoStore db, ProductDraft draft) => {
    if(string.IsNullOrWhiteSpace(draft.Name) || string.IsNullOrWhiteSpace(draft.Sku) || draft.Price < 0 || draft.Stock < 0) return Results.BadRequest(new {error="Ürün adı, SKU, pozitif fiyat ve stok gerekli."});
    var p = db.Modify(s => { if(s.Products.Any(x=>x.Sku.Equals(draft.Sku,StringComparison.OrdinalIgnoreCase))) return null;
        var item=new Product {Id=Guid.NewGuid().ToString("N")[..8],Name=draft.Name.Trim(),Sku=draft.Sku.Trim(),Category=draft.Category??"Diğer",Price=draft.Price,Stock=draft.Stock,Image=draft.Image??"📦",Channels=draft.Channels??new(),Status="Aktif"}; s.Products.Insert(0,item); db.Event(s,"Ürün eklendi",item.Name); return item; });
    return p is null ? Results.Conflict(new {error="Bu SKU zaten kayıtlı."}) : Results.Ok(p);
});
app.MapPut("/api/products/{id}", (DemoStore db,string id, ProductPatch patch) => {
    var p=db.Modify(s=>{var v=s.Products.FirstOrDefault(x=>x.Id==id);if(v==null)return null; if(patch.Stock.HasValue){if(patch.Stock<0)return null;v.Stock=patch.Stock.Value;}if(patch.Price.HasValue){if(patch.Price<0)return null;v.Price=patch.Price.Value;}if(patch.Status is not null)v.Status=patch.Status; db.Event(s,"Ürün güncellendi",v.Name);return v;});
    return p is null?Results.NotFound(new{error="Ürün bulunamadı veya değer geçersiz."}):Results.Ok(p);
});
app.MapGet("/api/orders", (DemoStore db)=>Results.Ok(db.SearchOrders(new ListingQuery(Size:50)).Items));
app.MapPut("/api/orders/{id}/status", (DemoStore db,string id,OrderPatch patch)=>{
    var allowed=new[]{"Yeni","Hazırlanıyor","Kargolandı","Tamamlandı","İptal"};if(!allowed.Contains(patch.Status))return Results.BadRequest(new{error="Geçersiz sipariş durumu."});
    var o=db.Modify(s=>{var v=s.Orders.FirstOrDefault(x=>x.Id==id);if(v==null)return null;v.Status=patch.Status;db.Event(s,"Sipariş durumu",$"{v.Id} → {patch.Status}");return v;});return o is null?Results.NotFound():Results.Ok(o);
});
app.MapGet("/api/connections", (DemoStore db)=>Results.Ok(db.Read().Connections));
app.MapPut("/api/connections/{id}",(DemoStore db,string id,ConnectionPatch patch)=>{
    var c=db.Modify(s=>{var v=s.Connections.FirstOrDefault(x=>x.Id==id);if(v==null)return null;v.Enabled=patch.Enabled;db.Event(s,"Bağlantı değiştirildi",$"{v.Name}: {(v.Enabled?"Etkin":"Devre dışı")}");return v;});return c is null?Results.NotFound():Results.Ok(c);
});
app.MapPost("/api/sync/demo",(DemoStore db)=>{db.Modify(s=>{s.LastSync=DateTimeOffset.Now;db.Event(s,"Demo senkronizasyon","Örnek veriler kontrol edildi. Dış pazaryeri API çağrısı yapılmadı.");return true;});return Results.Ok(new{message="Demo senkronizasyon tamamlandı; gerçek pazaryerlerine bağlanılmadı."});});
app.MapGet("/api/activity",(DemoStore db)=>Results.Ok(db.Read().Activity));
app.MapGet("/api/settings",(DemoStore db)=>Results.Ok(db.Read().Settings));
app.MapPut("/api/settings",(DemoStore db,Settings settings)=>{db.Modify(s=>{s.Settings=settings;db.Event(s,"Ayarlar güncellendi","Tercihler kaydedildi");return true;});return Results.Ok(settings);});
app.MapGet("/api/export/products.csv",(DemoStore db)=>{var products=db.Read().Products;static string Esc(string s)=>"\""+(s.Length>0&&"=+-@\t\r".Contains(s[0])?"'"+s:s).Replace("\"","\"\"")+"\"";var lines=new List<string>{"Id,Ürün,SKU,Kategori,Fiyat,Stok,Durum"}; lines.AddRange(products.Select(p=>string.Join(",",Esc(p.Id),Esc(p.Name),Esc(p.Sku),Esc(p.Category),p.Price.ToString(CultureInfo.InvariantCulture),p.Stock,Esc(p.Status))));return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n",lines))).ToArray(),"text/csv","urunler.csv");});

// MarketPilot 2.0: internal stock ledger, barcodes and demo shipping records.
app.MapGet("/api/stock/movements", (DemoStore db) => Results.Ok(db.Read().StockMovements));
app.MapPost("/api/stock/movements", (DemoStore db, StockMovementDraft draft) => {
    if (draft.Delta == 0 || Math.Abs((long)draft.Delta)>100000 || string.IsNullOrWhiteSpace(draft.Reason))
        return Results.BadRequest(new {error="Geçerli miktar değişimi ve neden gerekli."});
    var result = db.Modify(s => {
        var product=s.Products.FirstOrDefault(p=>p.Id==draft.ProductId);
        if(product is null || (long)product.Stock+draft.Delta<0) return null;
        product.Stock+=draft.Delta;
        var item=new StockMovement {Id=Guid.NewGuid().ToString("N")[..10],ProductId=product.Id,ProductName=product.Name,Delta=draft.Delta,Balance=product.Stock,Reason=draft.Reason.Trim(),At=DateTimeOffset.UtcNow};
        s.StockMovements.Insert(0,item);
        db.Event(s,"Stok hareketi",$"{product.Sku}: {draft.Delta:+#;-#;0} / {draft.Reason}");
        return item;
    });
    return result is null?Results.BadRequest(new {error="Ürün bulunamadı veya stok negatife düşer."}):Results.Ok(result);
});
app.MapGet("/api/shipments",(DemoStore db)=>Results.Ok(db.Read().Shipments));
app.MapPost("/api/shipments",(DemoStore db,ShipmentDraft draft)=>{
    if(string.IsNullOrWhiteSpace(draft.OrderId)||string.IsNullOrWhiteSpace(draft.Carrier)||string.IsNullOrWhiteSpace(draft.TrackingNumber))
        return Results.BadRequest(new {error="Sipariş, kargo firması ve takip numarası gerekli."});
    var item=db.Modify(s=>{
        if(!s.Orders.Any(o=>o.Id==draft.OrderId)||s.Shipments.Any(x=>x.OrderId==draft.OrderId))return null;
        var sh=new Shipment{Id=Guid.NewGuid().ToString("N")[..10],OrderId=draft.OrderId,Carrier=draft.Carrier.Trim(),TrackingNumber=draft.TrackingNumber.Trim(),Status="Hazırlanıyor",CreatedAt=DateTimeOffset.UtcNow};
        s.Shipments.Insert(0,sh);db.Event(s,"Gönderi oluşturuldu",$"{sh.OrderId} · {sh.Carrier}");return sh;
    });
    return item is null?Results.Conflict(new{error="Sipariş bulunamadı veya gönderisi zaten var."}):Results.Ok(item);
});
app.MapPut("/api/shipments/{id}/status",(DemoStore db,string id,ShipmentStatusPatch patch)=>{
    if(!new[]{"Hazırlanıyor","Kargoya verildi","Teslim edildi","İptal"}.Contains(patch.Status))return Results.BadRequest(new{error="Geçersiz durum."});
    var item=db.Modify(s=>{var sh=s.Shipments.FirstOrDefault(x=>x.Id==id);if(sh is null)return null;sh.Status=patch.Status;db.Event(s,"Gönderi güncellendi",$"{sh.OrderId}: {patch.Status}");return sh;});
    return item is null?Results.NotFound():Results.Ok(item);
});
app.MapGet("/api/barcodes",(DemoStore db)=>Results.Ok(db.Read().Products.Select(p=>new {p.Id,p.Name,p.Sku,p.Barcode})));
app.MapPut("/api/products/{id}/barcode",(DemoStore db,string id,BarcodePatch patch)=>{
    var barcode=(patch.Barcode??"").Trim();
    if(barcode.Length>40 || (barcode.Length>0 && !System.Text.RegularExpressions.Regex.IsMatch(barcode,@"^[a-zA-Z0-9\-_.]+$")))return Results.BadRequest(new{error="Barkod 40 karakteri geçemez ve harf/rakam/-/_. içermelidir."});
    var item=db.Modify(s=>{var p=s.Products.FirstOrDefault(x=>x.Id==id);if(p is null || (barcode.Length>0 && s.Products.Any(x=>x.Id!=id && x.Barcode==barcode)))return null;p.Barcode=barcode;db.Event(s,"Barkod güncellendi",$"{p.Sku} → {barcode}");return p;});
    return item is null?Results.Conflict(new{error="Ürün bulunamadı veya barkod başka bir üründe kullanılıyor."}):Results.Ok(item);
});

app.MapFallbackToFile("index.html");
app.Run();

public record BarcodePatch(string? Barcode);
public record StockMovementDraft(string ProductId,int Delta,string Reason);
public record ShipmentDraft(string OrderId,string Carrier,string TrackingNumber);
public record ShipmentStatusPatch(string Status);
public class StockMovement {public string Id{get;set;}="";public string ProductId{get;set;}="";public string ProductName{get;set;}="";public int Delta{get;set;}public int Balance{get;set;}public string Reason{get;set;}="";public DateTimeOffset At{get;set;}=DateTimeOffset.UtcNow;}
public class Shipment {public string Id{get;set;}="";public string OrderId{get;set;}="";public string Carrier{get;set;}="";public string TrackingNumber{get;set;}="";public string Status{get;set;}="Hazırlanıyor";public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.UtcNow;}
public class Product {public string Barcode{get;set;}="";public string Id{get;set;}="";public string Name{get;set;}="";public string Sku{get;set;}="";public string Category{get;set;}="";public decimal Price{get;set;}public int Stock{get;set;}public string Image{get;set;}="📦";public List<string> Channels{get;set;}=new();public string Status{get;set;}="Aktif";}
public record ProductDraft(string Name,string Sku,string? Category,decimal Price,int Stock,string? Image,List<string>? Channels);
public record ProductPatch(decimal? Price,int? Stock,string? Status);
public class Order {public string Id{get;set;}="";public string Customer{get;set;}="";public string Channel{get;set;}="";public string Product{get;set;}="";public decimal Total{get;set;}public int Quantity{get;set;}public string Status{get;set;}="Yeni";public DateTimeOffset Date{get;set;}=DateTimeOffset.Now;}
public record OrderPatch(string Status);
public class Connection {public string Id{get;set;}="";public string Name{get;set;}="";public string Initial{get;set;}="";public string Color{get;set;}="#888";public bool Enabled{get;set;}public bool Available{get;set;}public string Note{get;set;}="";}
public record ConnectionPatch(bool Enabled);
public class Activity {public string Title{get;set;}="";public string Detail{get;set;}="";public DateTimeOffset At{get;set;}=DateTimeOffset.Now;}
public class Settings {public string BusinessName{get;set;}="İnternet Tekstil";public string Currency{get;set;}="TRY";public int LowStockThreshold{get;set;}=10;public bool EmailNotifications{get;set;}=true;public bool SyncNotifications{get;set;}=true;}
public class Database {public List<StockMovement> StockMovements{get;set;}=new();public List<Shipment> Shipments{get;set;}=new();public List<Product> Products{get;set;}=new();public List<Order> Orders{get;set;}=new();public List<Connection> Connections{get;set;}=new();public List<Activity> Activity{get;set;}=new();public Settings Settings{get;set;}=new();public DateTimeOffset LastSync{get;set;}=DateTimeOffset.Now.AddMinutes(-18);}
public partial class DemoStore {
 private readonly object _gate=new();private readonly string _file;private readonly JsonSerializerOptions _json=new(JsonSerializerDefaults.Web){WriteIndented=true}; private Database _data;
 public DemoStore(IWebHostEnvironment env){var folder=Path.Combine(env.ContentRootPath,"Data");Directory.CreateDirectory(folder);_file=Path.Combine(folder,"demo-store.json");try{_data=File.Exists(_file)?JsonSerializer.Deserialize<Database>(File.ReadAllText(_file),_json)??Seed():Seed();}catch{_data=Seed();}Save();}
 public Database Read(){lock(_gate){return JsonSerializer.Deserialize<Database>(JsonSerializer.Serialize(_data,_json),_json)!;}}
 public T Modify<T>(Func<Database,T> action){lock(_gate){var r=action(_data);Save();return r;}}
 public void Event(Database s,string title,string detail){s.Activity.Insert(0,new Activity{Title=title,Detail=detail});s.Activity=s.Activity.Take(40).ToList();}
 private void Save(){File.WriteAllText(_file,JsonSerializer.Serialize(_data,_json));}
 public object Overview(){var d=Read();var orders=d.Orders.Where(o=>o.Status!="İptal").ToList();return new{revenue=orders.Sum(o=>o.Total),orders=d.Orders.Count,products=d.Products.Count,lowStock=d.Products.Count(p=>p.Stock<=d.Settings.LowStockThreshold),activeConnections=d.Connections.Count(c=>c.Enabled && c.Available),lastSync=d.LastSync,latestOrders=d.Orders.Take(5),activity=d.Activity.Take(5),channels=d.Orders.GroupBy(o=>o.Channel).Select(g=>new{name=g.Key,total=g.Sum(o=>o.Total),count=g.Count()}).OrderByDescending(x=>x.total).ToList()};}
 private static Database Seed(){var d=new Database();
 var items=new (string name,string sku,string category,decimal price,int stock,string icon,string[] channels)[]{
 ("Kablosuz Bluetooth Kulaklık","ELK-1001","Elektronik",1899,124,"🎧",new[]{"Trendyol","Amazon"}),
 ("Minimalist Seramik Vazo","EV-2040","Ev & Yaşam",749,8,"🏺",new[]{"Hepsiburada","n11"}),
 ("Organik Pamuk Oversize Tişört","MOD-3002","Moda",599,86,"👕",new[]{"Trendyol","WooCommerce"}),
 ("Paslanmaz Çelik Termos 500ml","EV-1018","Ev & Yaşam",1099,42,"🥤",new[]{"Amazon","Trendyol","n11"}),
 ("Ergonomik Kablosuz Mouse","ELK-1033","Elektronik",1299,5,"🖱️",new[]{"Hepsiburada","Amazon"}),
 ("Deri Kapaklı Not Defteri","KRT-4512","Kırtasiye",329,235,"📓",new[]{"n11","WooCommerce"}),
 ("Aromaterapi Mum Seti","EV-5551","Ev & Yaşam",429,64,"🕯️",new[]{"Trendyol","Hepsiburada"}),
 ("Akıllı Masa Lambası","ELK-6090","Elektronik",1799,19,"💡",new[]{"Amazon","WooCommerce"}),
 ("Spor Su Matarası","SP-3339","Spor",489,102,"🏃",new[]{"Trendyol","n11"})};
 int i=1;foreach(var t in items)d.Products.Add(new Product{Id=$"p{i++:000}",Name=t.name,Sku=t.sku,Category=t.category,Price=t.price,Stock=t.stock,Image=t.icon,Channels=t.channels.ToList()});
 var customers=new[]{"Zeynep Kaya","Mehmet Yılmaz","Elif Demir","Burak Aydın","Sude Şahin","Ahmet Arslan","Deniz Öztürk","Ceren Koç","Can Yıldız","Merve Öz"};var channels=new[]{"Trendyol","Hepsiburada","Amazon","n11","WooCommerce"};var statuses=new[]{"Yeni","Hazırlanıyor","Kargolandı","Tamamlandı","Tamamlandı","Tamamlandı","İptal"};var rng=new Random(47);
 for(int j=0;j<32;j++){var p=d.Products[rng.Next(d.Products.Count)];var qty=rng.Next(1,4);d.Orders.Add(new Order{Id=$"MP-{260000+j}",Customer=customers[j%customers.Length],Channel=channels[j%channels.Length],Product=p.Name,Total=p.Price*qty,Quantity=qty,Status=statuses[j%statuses.Length],Date=DateTimeOffset.Now.AddHours(-j*3-rng.Next(1,3))});}
 var cons=new (string,string,string,bool,string)[]{("Trendyol","T","#ff7c31",true,"Demo bağlantı · Gerçek API bağlı değil"),("Hepsiburada","H","#f49b22",true,"Demo bağlantı · Gerçek API bağlı değil"),("Amazon","a","#252b37",true,"Demo bağlantı · Gerçek API bağlı değil"),("n11","n","#d34a7b",false,"Bağlantı henüz yapılandırılmadı"),("WooCommerce","W","#8558c4",true,"Demo bağlantı · Gerçek API bağlı değil"),("Shopify","S","#76b64c",false,"Adaptör geliştirme aşamasında"),("ÇiçekSepeti","Ç","#ee5a69",false,"Adaptör geliştirme aşamasında"),("PrestaShop","P","#3482c6",false,"Adaptör geliştirme aşamasında"),("eBay","e","#527cda",false,"Adaptör geliştirme aşamasında")};
 int k=1;foreach(var c in cons)d.Connections.Add(new Connection{Id=$"c{k++}",Name=c.Item1,Initial=c.Item2,Color=c.Item3,Enabled=c.Item4,Available=c.Item4,Note=c.Item5});
 d.Activity=new(){new(){Title="Sipariş alındı",Detail="MP-260000 · Trendyol",At=DateTimeOffset.Now.AddMinutes(-6)},new(){Title="Stok kontrol edildi",Detail="9 ürün · Örnek veri",At=DateTimeOffset.Now.AddMinutes(-18)},new(){Title="Yeni ürün listelendi",Detail="Kablosuz Bluetooth Kulaklık",At=DateTimeOffset.Now.AddHours(-1)},new(){Title="Kargo durumu güncellendi",Detail="MP-260002 · Örnek işlem",At=DateTimeOffset.Now.AddHours(-2)}};return d;
 }
}
