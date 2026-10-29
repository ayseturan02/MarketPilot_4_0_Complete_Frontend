using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketPilot.Web.Contracts;
using MarketPilot.Web.Enterprise.Nebim;
using MarketPilot.Web.Integrations;

namespace MarketPilot.Web.Catalog;

public sealed record ErpMasterRow(string ErpProductId,string ErpVariantId,string Sku,string Barcode,string Name,string Brand,string Category,string Description,string Color,string Size,string Status,string[] Images,Dictionary<string,long> Versions);
public sealed record ErpCatalogDifference(string ProductId,string ErpVariantId,string Action,string? Reason,ErpMasterRow? Incoming,Product? Before);
public sealed record ErpReadPreview(string Token,string ProfileId,string TenantId,DateTimeOffset CreatedAt,string BaseHash,ErpCatalogDifference[] Changes);
public sealed class ErpCatalogReadModel(DemoStore db,INebimErpProvider provider)
{
    private readonly ConcurrentDictionary<string,ErpReadPreview> previews=new();
    private readonly ConcurrentDictionary<string,object> completed=new();
    private readonly object applyGate=new();
    private static string Hash(object value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static string Base(Database s)=>Hash(s.Products.OrderBy(p=>p.Id).ToArray());
    private static IntegrationException Error(string message)=>new("erp_catalog_conflict",message,409);
    private static bool Text(string? value,int max=300)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=max&&value==value.Trim();
    public static bool ApprovedImage(string? value)=>value=="/assets/mock-erp/ekru.svg"||Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.UserInfo.Length==0;
    public async Task<ErpReadPreview> Preview(CancellationToken ct)
    {
        if(provider.Profile.Mode!="mock"||!provider.Capabilities.IsMock)throw Error("Yalnızca tam mock ERP görüntüsü destekleniyor; gerçek bağlantı yok.");
        var products=(await provider.ReadProductsAsync(ct)).Data;
        var variants=(await provider.ReadVariantsAsync(ct)).Data;
        var identifiers=(await provider.ReadIdentifiersAsync(ct)).Data;
        if(products==null||variants==null||identifiers?.Barcodes==null||products.Count>10000||variants.Count>100000)throw Error("ERP tam görüntüsü geçersiz.");
        var all=products.Cast<CanonicalRecord>().Concat(variants).Concat(identifiers.Barcodes).ToArray();
        if(all.Any(r=>r==null||r.Context==null||r.Context.TenantId!=provider.Profile.TenantId||!Text(r.Id,200)||!Text(r.Context.SourceRecordId,200)||!Text(r.Context.SourceSystem,100)||r.Context.SourceVersion<1||r.Context.SourceSystem!="mock-nebim-v3"||r.Context.OccurredAt==default||r.Context.ReceivedAt<r.Context.OccurredAt)||
            products.GroupBy(p=>p.Id).Any(g=>g.Count()>1)||products.GroupBy(p=>p.Context.SourceRecordId).Any(g=>g.Count()>1)||identifiers.Barcodes.GroupBy(b=>b.Context.SourceRecordId).Any(g=>g.Count()>1)||variants.GroupBy(v=>v.Id).Any(g=>g.Count()>1)||variants.GroupBy(v=>v.Context.SourceRecordId).Any(g=>g.Count()>1))throw Error("ERP kaynak kimlikleri eksik, yinelenmiş veya başka işletmeye ait; görüntü uygulanmadı.");
        if(identifiers.Variants==null||identifiers.Variants.Count!=variants.Count||variants.Any(v=>!identifiers.Variants.Any(i=>i!=null&&i.Context!=null&&i.Id==v.Id&&CanonicalStore.Fingerprint(i)==CanonicalStore.Fingerprint(v))))throw Error("ERP variant reads changed during snapshot; retry.");
        var rows=new List<(ProductVariant Variant,ErpMasterRow? Row,string? Error)>();
        foreach(var v in variants)
        {
            var p=products.SingleOrDefault(p=>p.Id==v.ProductId);var codes=identifiers.Barcodes.Where(b=>b.VariantId==v.Id&&b.UnitCode==v.BaseUnit).ToArray();
            string? reason=p==null?"ERP ürün referansı bulunamadı.":!Text(p.Name)||!Text(p.Brand,200)||!Text(p.Category,200)||!CatalogHub.IsClothingCategory(p.Category)||!Text(v.Sku,100)||!Text(v.Color,100)||!Text(v.Size,100)?"Ürün, SKU, marka, kategori, renk veya beden eksik/geçersiz.":
                codes.Length!=1||!Text(codes[0].Value,120)?"Tekil temel birim barkodu eksik veya belirsiz.":variants.Count(x=>string.Equals(x.Sku,v.Sku,StringComparison.OrdinalIgnoreCase))>1||identifiers.Barcodes.Any(b=>b.VariantId!=v.Id&&b.Value==codes[0].Value)?"SKU/barkod başka varyantla çakışıyor.":
                p.Description?.Length>20000||p.ApprovedImages?.Count>20||p.ApprovedImages?.Any(i=>!ApprovedImage(i))==true?"Açıklama veya onaylı görseller geçersiz.":p.Status is not ("active" or "inactive" or "archived")||v.Status is not ("active" or "inactive" or "archived")?"ERP ürün durumu geçersiz.":null;
            var versions=new Dictionary<string,long>();foreach(var r in new CanonicalRecord?[]{p,v,codes.FirstOrDefault()}.Where(r=>r!=null)){var key=r!.GetType().Name+"|"+r.Context.SourceRecordId;versions[key]=r.Context.SourceVersion;}
            rows.Add((v,reason==null?new(p!.Context.SourceRecordId,v.Context.SourceRecordId,v.Sku,codes[0].Value,p.Name,p.Brand,p.Category,p.Description??"",v.Color,v.Size,p.Status=="active"&&v.Status=="active"?"Aktif":"Pasif",p.ApprovedImages?.ToArray()??[],versions):null,reason));
        }
        var preview=db.CatalogRead(s=>{
            var changes=new List<ErpCatalogDifference>();
            foreach(var entry in rows)
            {
                var incoming=entry.Row;var sourceId=entry.Variant.Context.SourceRecordId;
                var matches=s.Products.Where(p=>p.ErpProfileId==provider.Profile.Id&&p.ErpVariantId==sourceId).ToArray();
                if(matches.Length>1)throw Error("Yerel ERP kimliği birden fazla ürüne bağlı.");
                var before=matches.SingleOrDefault();
                if(before==null&&incoming!=null)
                {
                    var candidates=s.Products.Where(p=>p.SourceMode=="demo"&&p.ErpProfileId.Length==0&&p.Sku==incoming.Sku&&p.Barcode==incoming.Barcode).ToArray();
                    if(candidates.Length==1)before=candidates[0];
                }
                var action=before==null?"added":"updated";var reason=entry.Error;
                if(incoming!=null&&s.Products.Any(p=>p.Id!=before?.Id&&p.SourceMode=="demo"&&(p.Sku==incoming.Sku||p.Barcode==incoming.Barcode)))reason="SKU/barkod mevcut başka kayıtla çakışıyor; manuel inceleme gerekli.";
                if(incoming!=null&&before?.ErpProfileId.Length>0)
                {
                    if(incoming.Versions.Any(v=>before.ErpVersions.GetValueOrDefault(v.Key)>v.Value))reason="Eski ERP sürümü; ana kayıt değiştirilmedi.";
                    else if(before.ErpContentHash==Hash(incoming))action=before.ErpReviewReason.Length==0?"unchanged":"updated";
                    else if(incoming.Versions.All(v=>v.Value<=before.ErpVersions.GetValueOrDefault(v.Key)))reason="Aynı ERP sürümünde farklı içerik; inceleme gerekli.";
                }
                if(reason!=null)action="review";
                var id=before?.Id??CatalogHub.Key("erp",provider.Profile.TenantId+"|"+provider.Profile.Id+"|"+sourceId);
                var clone=before==null?null:JsonSerializer.Deserialize<Product>(JsonSerializer.Serialize(before));changes.Add(new(id,sourceId,action,reason,incoming,clone));
            }
            var seen=variants.Select(v=>v.Context.SourceRecordId).ToHashSet();
            foreach(var old in s.Products.Where(p=>p.ErpProfileId==provider.Profile.Id&&!seen.Contains(p.ErpVariantId)))changes.Add(new(old.Id,old.ErpVariantId,"missing","Kayıt tam ERP görüntüsünde bulunamadı; fiziksel silme yapılmaz.",null,JsonSerializer.Deserialize<Product>(JsonSerializer.Serialize(old))));
            return new ErpReadPreview(Guid.NewGuid().ToString("N"),provider.Profile.Id,provider.Profile.TenantId,DateTimeOffset.UtcNow,Base(s),changes.ToArray());
        });
        foreach(var p in previews.Where(p=>DateTimeOffset.UtcNow-p.Value.CreatedAt>TimeSpan.FromMinutes(10)).ToArray()){previews.TryRemove(p.Key,out _);completed.TryRemove(p.Key,out _);}if(previews.Count>=100)throw Error("Önizleme sınırı dolu; eski önizlemelerin süresinin dolmasını bekleyin.");
        previews[preview.Token]=preview;return preview;
    }
    public object Apply(string token)
    {
        if(token==null||!previews.TryGetValue(token,out var preview)||DateTimeOffset.UtcNow-preview.CreatedAt>TimeSpan.FromMinutes(10)||preview.ProfileId!=provider.Profile.Id||preview.TenantId!=provider.Profile.TenantId)throw Error("ERP önizlemesi geçersiz veya süresi dolmuş.");
        if(provider.Profile.Mode!="mock"||!provider.Profile.Enabled)throw Error("Mock ERP profili kapalı veya değişmiş.");
        lock(applyGate){var applied=db.Modify(s=>{
            if(completed.TryGetValue(token,out var result))return result;
            if(Base(s)!=preview.BaseHash)throw Error("Katalog önizlemeden sonra değişmiş; farkları yeniden okuyun.");
            var now=DateTimeOffset.UtcNow;
            foreach(var change in preview.Changes)
            {
                var p=s.Products.SingleOrDefault(p=>p.Id==change.ProductId);
                if(change.Action is "missing" or "review")
                {
                    if(p!=null){p.ErpReviewReason=change.Reason??"İnceleme gerekli.";p.Status="İnceleme";p.Version++;}
                    s.Catalog.ErpReviews[change.ErpVariantId]=change.Reason??"İnceleme gerekli.";continue;
                }
                var row=change.Incoming!;
                if(p==null){p=new Product{Id=change.ProductId,SourceMode="demo",StockKnown=false,PriceKnown=false,StockState=StockValueState.Unknown,Image="",Currency="",VatIncluded=null};s.Products.Add(p);}
                p.SourceSystem=ProductOwnershipPolicy.ErpSource;p.ErpProfileId=preview.ProfileId;p.ErpProductId=row.ErpProductId;p.ErpVariantId=row.ErpVariantId;p.VariantId=row.ErpVariantId;
                p.Name=row.Name;p.Sku=row.Sku;p.Barcode=row.Barcode;p.Category=row.Category;p.Brand=row.Brand;p.Description=row.Description;p.Images=row.Images.ToList();p.Attributes["Renk"]=row.Color;p.Attributes["Beden"]=row.Size;
                p.Status=row.Status;p.ErpSyncedAt=now;p.ErpSourceVersion=row.Versions.Values.Max();p.ErpVersions=row.Versions;p.ErpContentHash=Hash(row);p.ErpReviewReason="";s.Catalog.ErpReviews.Remove(change.ErpVariantId);if(change.Action!="unchanged")p.Version++;
            }
            s.Catalog.LastErpSyncAt=now;db.Event(s,"Mock ERP kataloğu okundu",preview.Changes.Length+" kayıt kontrol edildi; pazaryeri eşleştirmeleri korundu.");
            return (object)new{syncedAt=now,review=preview.Changes.Count(c=>c.Action is "review" or "missing"),changes=preview.Changes.Select(c=>new{c.ProductId,c.Action})};
        });completed[token]=applied;return applied;}
    }
    public object Status()=>db.CatalogRead(s=>new{provider.Profile,provider.Capabilities,lastSyncAt=s.Catalog.LastErpSyncAt,reviews=s.Catalog.ErpReviews.ToArray()});
}
