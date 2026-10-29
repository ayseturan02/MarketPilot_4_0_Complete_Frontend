using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketPilot.Web.Integrations;

namespace MarketPilot.Web.Enterprise;
public sealed class ErpCatalog(DemoStore db,IConfiguration config)
{
    public object Import(ErpCatalogBatch batch,bool preserveStock=false)
    {
        if(string.IsNullOrWhiteSpace(batch.RequestId)||batch.RequestId.Length>150||batch.Items==null||batch.Items.Length>200000)throw new ArgumentException("Kaynak işlem kimliği ve ürün listesi geçersiz.");
        if(batch.Items.Any(p=>p==null||string.IsNullOrWhiteSpace(p.Sku)||p.Sku.Length>100||string.IsNullOrWhiteSpace(p.Barcode)||p.Barcode.Length>120||string.IsNullOrWhiteSpace(p.Name)||p.Name.Length>300||p.Stock is <0 or >10000000||p.Price is <0 or >10000000||p.Currency==null||!System.Text.RegularExpressions.Regex.IsMatch(p.Currency,"^[A-Z]{3}$")||p.Images?.Length>20||p.Images?.Any(i=>!EnterpriseBridge.SafeLink(i))==true))throw new ArgumentException("Kaynak ürünün barkod, SKU, fiyat, stok, para birimi veya görsel bağlantısı geçersiz.");
        // Match the normalized identity that is persisted, including on subsequent imports.
        batch=batch with {Items=batch.Items.Select(item=>item with {Sku=item.Sku.Trim(),Barcode=item.Barcode.Trim(),Name=item.Name.Trim()}).ToArray()};
        if(batch.Items.GroupBy(i=>i.Barcode,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1)||batch.Items.GroupBy(i=>i.Sku,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new ArgumentException("Kaynak listesinde tekrarlayan SKU veya barkod var; hiçbir ürün uygulanmadı.");
        if(batch.StockAcknowledgements?.Length>10000||batch.Items.Any(i=>i.StockAcknowledgements?.Length>100))throw new ArgumentException("Stok onayı kayıt sınırı aşıldı.");
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(((batch.StockAcknowledgements==null||batch.StockAcknowledgements.Length==0)&&batch.Items.All(i=>i.StockAcknowledgements==null)?JsonSerializer.Serialize(batch.Items):JsonSerializer.Serialize(new {batch.Items,batch.StockAcknowledgements})))));
        var mode=config["Enterprise:Erp:DataMode"]=="demo"?"demo":"real";
        return db.Modify(s=>{
            if(s.ErpReceipts.TryGetValue(batch.RequestId,out var prior)){if(prior.Hash!=hash)throw new IntegrationException("source_conflict","Kaynak işlem kimliği farklı içerikle tekrar kullanıldı.",409);return new {updated=prior.ProductIds.Length,products=s.Products.Where(p=>prior.ProductIds.Contains(p.Id)).ToArray()};}
            var applied=new List<Product>();
            foreach(var item in batch.Items)
            {
                var matches=s.Products.Where(p=>p.SourceMode==mode&&
                    (p.Barcode.Equals(item.Barcode,StringComparison.OrdinalIgnoreCase)||p.Sku.Equals(item.Sku,StringComparison.OrdinalIgnoreCase))).Take(2).ToArray();
                if(matches.Length>1)throw new IntegrationException("source_ambiguous","Kaynak ürün için birden fazla merkezi kayıt eşleşti.",409);
                var p=matches.FirstOrDefault();
                if(p==null){p=new Product{Id=Guid.NewGuid().ToString("N"),SourceMode=mode,StockKnown=false,CreatedAt=DateTimeOffset.UtcNow};s.Products.Add(p);}
                p.SourceSystem="nebim-v3";p.Name=item.Name.Trim();p.Sku=item.Sku.Trim();p.Barcode=item.Barcode.Trim();p.Currency=item.Currency;
                p.PriceKnown=item.Price.HasValue;p.Price=item.Price??0;if(!preserveStock){p.StockKnown=item.Stock.HasValue;p.Stock=item.Stock??0;}
                if(item.Images!=null)p.Images=item.Images.ToList();p.Category=item.Category??p.Category;p.Brand=item.Brand??p.Brand;p.Description=item.Description??p.Description;p.VatIncluded=item.VatIncluded;p.Version++;
                applied.Add(p);
            }
            if(!preserveStock)MarketPilot.Web.Workspace.OperationalInventory.Acknowledge(s,(batch.StockAcknowledgements??[]).Concat(batch.Items.SelectMany(i=>i.StockAcknowledgements??[])).ToArray(),applied.Where(p=>p.StockKnown).Select(p=>p.Id).ToHashSet());
            s.ErpReceipts[batch.RequestId]=new(){Hash=hash,ProductIds=applied.Select(p=>p.Id).ToArray()};
            foreach(var key in s.ErpReceipts.Keys.Take(Math.Max(0,s.ErpReceipts.Count-2000)).ToArray())s.ErpReceipts.Remove(key);
            db.Event(s,"Nebim V3 verileri alındı",applied.Count+" ürün · fiyat, stok ve görsel bağlantıları kaynaktan işlendi.");
            return new {updated=applied.Count,products=applied.ToArray()};
        });
    }
}
