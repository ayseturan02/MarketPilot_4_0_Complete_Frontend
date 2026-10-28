using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Demo;
namespace MarketPilot.Web.Catalog;

public sealed partial class CatalogHub(DemoStore db,IntegrationRegistry registry,DemoWorkspace demo)
{
    private int initialized;private readonly object bootstrapGate=new();
    public static string Source(ConnectionState c)=>c.Settings.Mode=="demo"?$"demo|{c.Settings.Marketplace}|{c.Settings.Environment}|{c.Settings.Name}":$"real|{c.Settings.Marketplace}|{c.Settings.Environment}";
    public static string Key(string storeId,string identity)=>storeId+":"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    public void Ensure()
    {
        if(Volatile.Read(ref initialized)==1)return;lock(bootstrapGate){if(initialized==1)return;
        if(!db.CatalogRead(s=>s.Catalog.Version>0)){
            var seeds=registry.Connections.Values.Where(c=>c.Settings.Mode=="demo").OrderBy(c=>c.Id=="trendyol"?0:1).ThenBy(c=>c.Id).Select(c=>{var snapshot=demo.Snapshot(c.Settings);return(Store:c,Rows:snapshot.Products.Select(p=>Candidate(c,p) with{Description=snapshot.ProductInfo.GetValueOrDefault(p.Barcode)?.Description,Attributes=snapshot.ProductInfo.GetValueOrDefault(p.Barcode)?.Attributes}).ToArray());}).ToArray();
            db.Modify(s=>{if(s.Catalog.Version>0)return false;foreach(var seed in seeds)ImportRows(s,seed.Store,seed.Rows,DateTimeOffset.UtcNow);s.Catalog.Version=1;db.Event(s,"Katalog bağlandı","Mevcut demo mağazalar ortak ürün kimlikleriyle kataloğa bağlandı; yerel ürünler korundu.");return true;});
        }Volatile.Write(ref initialized,1);}
    }
    public static CatalogCandidate Candidate(ConnectionState store,MarketplaceProduct p)=>new(Key(store.Id,Identity(p.Id,p.Sku,p.Barcode)),p.Id,p.Sku,p.Barcode,p.Name,p.CategoryName,p.BrandName,p.Stock,p.SalePrice,p.Currency,store.Settings.Mode=="demo"?true:null,p.ApprovalStatus,p.Images);
    public static string Identity(string id,string sku,string barcode)=>!string.IsNullOrWhiteSpace(id)?"id:"+id:!string.IsNullOrWhiteSpace(sku)?"sku:"+sku:!string.IsNullOrWhiteSpace(barcode)?"barcode:"+barcode:throw new IntegrationException("missing_identity","Mağaza ürününün kimliği bildirilmedi.",409);
    private Func<CatalogListing,CatalogListing> Fresh(){var cache=new Dictionary<string,Dictionary<string,MarketplaceProduct>>();return link=>Current(link,cache);}
    private CatalogListing Current(CatalogListing link,Dictionary<string,Dictionary<string,MarketplaceProduct>> cache)
    {
        if(!registry.Connections.TryGetValue(link.StoreId,out var c)||Source(c)!=link.Source)return link with{Status="source_changed"};
        if(c.Settings.Mode!="demo")return link;
        if(!cache.TryGetValue(c.Id,out var products)){products=demo.Snapshot(c.Settings).Products.GroupBy(p=>p.Id+"\0"+p.Barcode).ToDictionary(g=>g.Key,g=>g.First());cache[c.Id]=products;}var p=products.GetValueOrDefault(link.ExternalId+"\0"+link.Barcode);
        if(p==null)return link with{Status="missing"};
        // FBA quantities are not merchant-fulfilled stock and must not become a publish target.
        var stock=c.Settings.Marketplace=="amazon"&&MarketPilot.Web.Integrations.Amazon.AmazonDemo.IsAmazonFulfilled(p.Barcode)||c.Settings.Marketplace=="n11"&&MarketPilot.Web.Integrations.N11.N11Demo.Depot(p.Barcode)?null:p.Stock;
        return link with{Stock=stock,Price=p.SalePrice,Currency=p.Currency,Status=p.ApprovalStatus,ObservedAt=DateTimeOffset.UtcNow};
    }
    public ListingPage<CatalogProductRow> Search(CatalogQuery q)
    {
        Ensure();var fresh=Fresh();return db.CatalogRead<ListingPage<CatalogProductRow>>(s=>{
            var byProduct=s.Catalog.Listings.Values.GroupBy(l=>l.ProductId).ToDictionary(g=>g.Key,g=>g.ToArray());
            var rows=s.Products.Where(p=>string.IsNullOrWhiteSpace(q.Search)||ListingFilters.Text(p.Name,q.Search)||ListingFilters.Text(p.Sku,q.Search)||ListingFilters.Text(p.Barcode,q.Search))
                .Where(p=>ListingFilters.Exact(p.Status,q.Status)&&ListingFilters.Text(p.Category,q.Category)&&ListingFilters.Exact(p.SourceMode,q.Mode))
                .Where(p=>string.IsNullOrWhiteSpace(q.StoreId)||byProduct.GetValueOrDefault(p.Id,[]).Any(l=>l.StoreId==q.StoreId))
                .Where(p=>q.Mapped!="yes"||byProduct.ContainsKey(p.Id)).Where(p=>q.Mapped!="no"||!byProduct.ContainsKey(p.Id)).OrderByDescending(p=>p.CreatedAt).ThenBy(p=>p.Id);
            var total=rows.Count();var items=rows.Skip(q.Page*q.Size).Take(q.Size).Select(p=>new CatalogProductRow(Clone(p),byProduct.GetValueOrDefault(p.Id,[]).Take(8).Select(fresh).ToArray(),byProduct.GetValueOrDefault(p.Id,[]).Length)).ToArray();return new(items,q.Page,q.Size,total,(int)Math.Ceiling(total/(double)q.Size));
        });
    }
    public object Detail(string id,CatalogQuery q){Ensure();var fresh=Fresh();return db.CatalogRead(s=>{var product=s.Products.FirstOrDefault(p=>p.Id==id)??throw new IntegrationException("not_found","Ürün bulunamadı.",404);var links=s.Catalog.Listings.Values.Where(l=>l.ProductId==id&&(string.IsNullOrWhiteSpace(q.StoreId)||l.StoreId==q.StoreId)).OrderBy(l=>l.StoreId).ToArray();return (object)new{product=Clone(product),listings=new ListingPage<CatalogListing>(links.Skip(q.Page*q.Size).Take(q.Size).Select(fresh).ToArray(),q.Page,q.Size,links.Length,(int)Math.Ceiling(links.Length/(double)q.Size))};});}
    private static Product Clone(Product p)=>JsonSerializer.Deserialize<Product>(JsonSerializer.Serialize(p))!;
    public object Edit(string id,CatalogEdit edit)
    {
        Ensure();ValidateEdit(edit);
        return db.Modify(s=>{var p=s.Products.FirstOrDefault(p=>p.Id==id)??throw new IntegrationException("not_found","Ürün bulunamadı.",404);if(p.Version!=edit.Version)throw new IntegrationException("version_conflict","Ürün başka bir işlemde değişti. Güncel kaydı açıp tekrar dene.",409);
            if(s.Products.Any(x=>x.Id!=id&&x.SourceMode==p.SourceMode&&(x.Sku.Equals(edit.Sku.Trim(),StringComparison.OrdinalIgnoreCase)||edit.Barcode.Trim().Length>0&&x.Barcode==edit.Barcode.Trim())))throw new IntegrationException("duplicate_identity","Aynı SKU veya barkod başka bir üründe var.",409);
            var priorStock=p.Stock;var priorKnown=p.StockKnown;p.Name=edit.Name.Trim();p.Sku=edit.Sku.Trim();p.Barcode=edit.Barcode.Trim();p.Category=edit.Category.Trim();p.Brand=edit.Brand.Trim();p.Description=edit.Description;p.Stock=edit.Stock??0;p.StockKnown=edit.Stock.HasValue;p.Price=edit.Price??0;p.PriceKnown=edit.Price.HasValue;p.Currency=edit.Currency;p.VatIncluded=edit.VatIncluded;p.Status=edit.Status;p.Version++;if(priorKnown&&p.StockKnown&&priorStock!=p.Stock)s.StockMovements.Insert(0,new StockMovement{Id=Guid.NewGuid().ToString("N"),ProductId=p.Id,ProductName=p.Name,Delta=p.Stock-priorStock,Balance=p.Stock,Reason="Merkezi katalog düzenlemesi"});db.Event(s,"Merkezi ürün güncellendi",p.Sku+" · Mağaza değerleri değiştirilmedi.");return Clone(p);});
    }
    public object Import(CandidateBatch batch,string[] keys)
    {
        Ensure();var fresh=Fresh();if(keys==null||keys.Length is <1 or >100||keys.Distinct().Count()!=keys.Length)throw new IntegrationException("invalid_selection","1–100 farklı ürün seç.",400);if(!registry.Connections.TryGetValue(batch.StoreId,out var store)||Source(store)!=batch.Source)throw new IntegrationException("source_changed","Mağaza bağlantısı değişti. Listeyi yenile.",409);var rows=keys.Select(k=>batch.Rows.FirstOrDefault(r=>r.Key==k)??throw new IntegrationException("invalid_selection","Seçilen kayıt bu sayfada bulunamadı.",400)).ToArray();
        return db.Modify(s=>{var result=ImportRows(s,store,rows,batch.ObservedAt);db.Event(s,"Kataloğa aktarıldı",$"{store.Settings.Name} · {result.Count} eşleştirme · merkezi stok ve fiyat korunur.");return result;});
    }
    private static List<object> ImportRows(Database s,ConnectionState store,CatalogCandidate[] rows,DateTimeOffset at)
    {
        var result=new List<object>();foreach(var r in rows){if(r.Name.Length>300||r.Sku.Length>100||r.Barcode.Length>100||r.Description?.Length>20000||r.Images?.Count>100||r.Attributes?.Count>100)throw new IntegrationException("invalid_product","Mağaza ürün alanları izin verilen sınırı aşıyor.",409);var old=s.Catalog.Listings.GetValueOrDefault(r.Key);Product? p=old==null?null:s.Products.FirstOrDefault(p=>p.Id==old.ProductId);
            if(old!=null&&old.Source!=Source(store))throw new IntegrationException("source_changed","Kayıt farklı bir mağaza bağlantısına ait.",409);
            if(p==null){var matches=s.Products.Where(p=>p.SourceMode==store.Settings.Mode&&(r.Barcode.Length>0?p.Barcode==r.Barcode:r.Sku.Length>0&&p.Sku.Equals(r.Sku,StringComparison.OrdinalIgnoreCase))).ToArray();if(matches.Length>1)throw new IntegrationException("ambiguous_identity","Birden fazla ürün eşleşiyor; kimlikleri düzelt.",409);p=matches.SingleOrDefault();
                if(p==null&&r.Sku.Length>0){var skuMatch=s.Products.Where(p=>p.SourceMode==store.Settings.Mode&&p.Sku.Equals(r.Sku,StringComparison.OrdinalIgnoreCase)).ToArray();if(skuMatch.Length>0){if(skuMatch.Length!=1||r.Barcode.Length>0&&skuMatch[0].Barcode.Length>0&&skuMatch[0].Barcode!=r.Barcode)throw new IntegrationException("identity_conflict","SKU aynı ancak barkod farklı. Otomatik birleştirme yapılmadı.",409);p=skuMatch[0];}}
                if(p==null){p=new Product{Id=Guid.NewGuid().ToString("N"),Name=r.Name,Sku=r.Sku.Length>0?r.Sku:"KANAL-"+Guid.NewGuid().ToString("N")[..10],Barcode=r.Barcode,Category=r.Category,Brand=r.Brand,Description=r.Description??"",Images=(r.Images??[]).Where(v=>Uri.TryCreate(v,UriKind.Absolute,out var uri)&&uri.Scheme=="https").ToList(),Attributes=r.Attributes??new(),Stock=r.Stock??0,StockKnown=r.Stock.HasValue,Price=r.Price??0,PriceKnown=r.Price.HasValue,Currency=r.Currency,VatIncluded=r.VatIncluded,SourceMode=store.Settings.Mode,Image="👕"};s.Products.Insert(0,p);}
            }
            if(r.Name.Length==0)throw new IntegrationException("invalid_product","Ürün adı bildirilmedi.",409);
            s.Catalog.Listings[r.Key]=new(r.Key,p.Id,store.Id,Source(store),r.ExternalId,r.Sku,r.Barcode,r.Name,r.Category,r.Brand,r.Stock,r.Price,r.Currency,r.VatIncluded,r.Status,at);RefreshChannels(s,p.Id);result.Add(new{key=r.Key,productId=p.Id});
        }return result;
    }
    private static void RefreshChannels(Database s,string id){var p=s.Products.First(x=>x.Id==id);p.Channels=s.Catalog.Listings.Values.Where(l=>l.ProductId==id).Select(l=>l.Source.Split('|')[1]).Distinct().Select(x=>x switch{"trendyol"=>"Trendyol","hepsiburada"=>"Hepsiburada","amazon"=>"Amazon","boyner"=>"Boyner","ticimax"=>"Ticimax",_=>x}).ToList();}
    public object Remap(CatalogRemap input){Ensure();return db.Modify(s=>{var link=s.Catalog.Listings.GetValueOrDefault(input.ListingKey)??throw new IntegrationException("not_found","Mağaza eşleştirmesi bulunamadı.",404);var p=s.Products.FirstOrDefault(p=>p.Id==input.ProductId)??throw new IntegrationException("not_found","Merkezi ürün bulunamadı.",404);if(p.SourceMode=="local"&&!s.Catalog.Listings.Values.Any(l=>l.ProductId==p.Id))p.SourceMode=link.Source.Split('|')[0];if(p.SourceMode!=link.Source.Split('|')[0])throw new IntegrationException("mode_mismatch","Demo ve gerçek ürünler aynı kayıtta birleştirilemez.",409);s.Catalog.Listings[link.Key]=link with{ProductId=p.Id};RefreshChannels(s,p.Id);RefreshChannels(s,link.ProductId);db.Event(s,"Ürün eşleştirmesi değişti",link.Sku+" → "+p.Sku);return new{ok=true};});}
    public object Categories(CatalogQuery q){Ensure();return db.CatalogRead(s=>{var rows=s.Products.GroupBy(p=>p.Category).Where(g=>ListingFilters.Text(g.Key,q.Search)).OrderBy(g=>g.Key).Select(g=>new{category=g.Key,products=g.Count(),brands=g.Select(p=>p.Brand).Where(b=>b.Length>0).Distinct().Take(10),bindings=s.Catalog.Categories.Values.Where(b=>b.Category==g.Key).ToArray()}).ToArray();return ListingFilters.Page(rows,new ListingQuery(Page:q.Page,Size:q.Size));});}
    public object Bind(CategoryBinding input){Ensure();if(string.IsNullOrWhiteSpace(input.Category)||input.Category.Length>200||string.IsNullOrWhiteSpace(input.ExternalCategoryId)||input.ExternalCategoryId.Length>100||input.ExternalCategoryName.Length>300||input.BrandId.Length>100||input.Attributes==null||input.Attributes.Count>100||input.Attributes.Any(a=>a.Key.Length>100||a.Value.Length>500)||!registry.Connections.ContainsKey(input.StoreId))throw new IntegrationException("invalid_mapping","Kategori, mağaza ve kanal kategori numarasını kontrol et.",400);return db.Modify(s=>{s.Catalog.Categories[Key(input.StoreId,input.Category)]=input;db.Event(s,"Kategori eşleştirildi",input.Category);return input;});}
    public PublishLine[] Preview(string[] keys)
    {
        Ensure();var fresh=Fresh();if(keys==null||keys.Length is <1 or >100||keys.Distinct().Count()!=keys.Length)throw new IntegrationException("invalid_selection","1–100 farklı mağaza kaydı seç.",400);return db.CatalogRead(s=>keys.Select(k=>{
            var l=s.Catalog.Listings.GetValueOrDefault(k)??throw new IntegrationException("not_found","Eşleştirme bulunamadı.",404);var p=s.Products.First(x=>x.Id==l.ProductId);var c=registry.Connections.GetValueOrDefault(l.StoreId);var current=fresh(l);string? reason=c==null||Source(c)!=l.Source?"Mağaza bağlantısı değişmiş.":c.Settings.Mode!="demo"?"Gerçek mağazaya gönderim henüz açık değil.":p.Status!="Aktif"?"Ürün aktif değil.":current.Status is "missing" or "source_changed"?"Mağaza kaydı bulunamadı.":!p.StockKnown||!p.PriceKnown?"Merkezi stok veya fiyat bilinmiyor.":p.Stock!=decimal.Truncate(p.Stock)||p.Stock>1000000||p.Price is <=0 or >10000000?"Demo gönderimi için tam sayı stok ve pozitif fiyat gerekli.":p.Currency!=current.Currency||p.VatIncluded!=true?"Para birimi ve KDV dahil fiyatı kontrol et.":l.Barcode.Length==0?"Mağaza barkodu gerekli.":c.Settings.Marketplace=="amazon"&&MarketPilot.Web.Integrations.Amazon.AmazonDemo.IsAmazonFulfilled(l.Barcode)?"Amazon deposu stoğu bu işlemle değiştirilemez.":c.Settings.Marketplace=="n11"&&MarketPilot.Web.Integrations.N11.N11Demo.Depot(l.Barcode)?"n11depom stoğu bu işlemle değiştirilemez.":null;
            return new PublishLine(k,p.Id,p.Version,l.StoreId,p.Name,current.Stock,current.Price,p.StockKnown?p.Stock:null,p.PriceKnown?p.Price:null,reason==null,reason);
        }).ToArray());
    }
    public object Publish(CatalogPublish input)
    {
        if(string.IsNullOrWhiteSpace(input.RequestId)||input.RequestId.Length>100||input.Keys==null||input.Versions==null)throw new IntegrationException("invalid_request","İşlem kimliği gerekli.",400);
        var fp=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{keys=input.Keys.OrderBy(k=>k).ToArray(),versions=input.Versions.OrderBy(v=>v.Key)}))));
        // Repeated requests return the original job even if a product was edited afterwards.
        var previous=db.CatalogRead(s=>s.Catalog.Jobs.FirstOrDefault(j=>j.RequestId==input.RequestId));if(previous!=null){if(previous.Fingerprint!=fp)throw new IntegrationException("idempotency_conflict","Bu işlem kimliği farklı bir istekte kullanılmış.",409);return previous;}
        var lines=Preview(input.Keys);if(lines.Any(l=>!l.Ready))throw new IntegrationException("publish_blocked",lines.First(l=>!l.Ready).Reason!,409);
        return db.Modify(s=>{var duplicate=s.Catalog.Jobs.FirstOrDefault(j=>j.RequestId==input.RequestId);if(duplicate!=null){if(duplicate.Fingerprint!=fp)throw new IntegrationException("idempotency_conflict","İşlem kimliği farklı bir istekte kullanılmış.",409);return duplicate;}
            var job=new CatalogJob{RequestId=input.RequestId,Fingerprint=fp};foreach(var line in lines){var l=s.Catalog.Listings[line.Key];var p=s.Products.First(p=>p.Id==l.ProductId);if(!input.Versions.TryGetValue(p.Id,out var version)||version!=p.Version||line.Version!=p.Version)throw new IntegrationException("version_conflict","Önizlemeden sonra ürün değişti. Yeniden önizle.",409);job.Targets.Add(new(){Key=l.Key,StoreId=l.StoreId,Source=l.Source,ProductId=p.Id,Version=p.Version,Barcode=l.Barcode,Stock=p.Stock,Price=p.Price});}
            s.Catalog.Jobs.Insert(0,job);db.Event(s,"Mağaza işlemi sıraya alındı",$"{job.Targets.Count} demo mağaza kaydı");return job;
        });
    }
    public object Jobs(CatalogQuery q){Ensure();return db.CatalogRead(s=>ListingFilters.Page(s.Catalog.Jobs.Where(j=>ListingFilters.Exact(j.State,q.Status)).OrderByDescending(j=>j.CreatedAt),new ListingQuery(Page:q.Page,Size:q.Size)));}
    public async Task Tick(CancellationToken ct)
    {
        if(initialized==0)return;var jobs=db.CatalogRead(s=>s.Catalog.Jobs.Where(j=>j.State=="pending").Take(10).Select(j=>JsonSerializer.Deserialize<CatalogJob>(JsonSerializer.Serialize(j))!).ToArray());
        foreach(var job in jobs)foreach(var t in job.Targets.Where(t=>t.State is "pending" or "queued")){
            ct.ThrowIfCancellationRequested();try{
                if(!registry.Connections.TryGetValue(t.StoreId,out var store)||store.Settings.Mode!="demo"||Source(store)!=t.Source)throw new ArgumentException("Demo mağaza bağlantısı değişti.");
                if(t.State=="pending"){
                    var stale=db.CatalogRead(s=>s.Products.FirstOrDefault(p=>p.Id==t.ProductId)?.Version!=t.Version||s.Catalog.Listings.GetValueOrDefault(t.Key)?.ProductId!=t.ProductId);if(stale)throw new ArgumentException("Sıraya alındıktan sonra ürün veya eşleştirme değişti. Yeniden önizle.");
                    var sent=demo.Enqueue(store.Settings,new("stock","catalog-"+job.Id+"-"+Key(t.StoreId,t.Key)[^16..],t.Barcode,(int)t.Stock,t.Price));t.DemoJobId=sent.Id;t.State="queued";
                }
                var result=demo.Snapshot(store.Settings).Jobs.FirstOrDefault(j=>j.Id==t.DemoJobId);if(result?.State=="completed")t.State="completed";else if(result==null){t.State="failed";t.Error="Mağaza işlem kaydı bulunamadı; yeniden önizle.";}else if(result.State=="failed"){t.State="failed";t.Error=result.Error;}
            }catch(Exception e)when(e is ArgumentException or InvalidOperationException){t.State="failed";t.Error=e.Message;}
            db.Modify(s=>{var saved=s.Catalog.Jobs.Single(j=>j.Id==job.Id);var target=saved.Targets.Single(v=>v.Key==t.Key);target.State=t.State;target.DemoJobId=t.DemoJobId;target.Error=t.Error;saved.State=saved.Targets.Any(v=>v.State is "pending" or "queued")?"pending":saved.Targets.Any(v=>v.State=="failed")?"failed":"completed";return true;});
        }await Task.CompletedTask;
    }
}
public sealed class CatalogWorker(CatalogHub hub,ILogger<CatalogWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken){using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));try{while(await timer.WaitForNextTickAsync(stoppingToken)){try{await hub.Tick(stoppingToken);}catch(Exception e)when(e is not OperationCanceledException||!stoppingToken.IsCancellationRequested){log.LogError("Katalog işlem sırası tamamlanamadı: {Type}",e.GetType().Name);}}}catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}}
}
