using System.Collections.Concurrent;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Demo;
using MarketPilot.Web.Integrations.Ticimax;
namespace MarketPilot.Web.Catalog;

public sealed class CatalogSource(IntegrationRegistry registry,DemoMarketplaceAdapter demo,IEnumerable<IMarketplaceAdapter> adapters,TicimaxAdapter ticimax,MarketplaceClients? clients=null)
{
    private readonly ConcurrentDictionary<string,CandidateBatch> batches=new();
    public CandidateBatch Get(string token)
    {
        if(!batches.TryGetValue(token,out var batch)||batch.ObservedAt<DateTimeOffset.UtcNow.AddMinutes(-10))throw new IntegrationException("preview_expired","Aktarım listesi eskidi. Mağazadan yeniden getir.",409);return batch;
    }
    public object Page(string token,int page){if(page<0||(long)page*25>int.MaxValue)throw new IntegrationException("invalid_page","Geçersiz sayfa.",400);var b=Get(token);return Page(b,page);}
    private static object Page(CandidateBatch b,int part)=>new{token=b.Token,storeId=b.StoreId,observedAt=b.ObservedAt,items=b.Rows.Skip(part*25).Take(25),part,totalParts=(int)Math.Ceiling(b.Rows.Length/25d),nativePage=b.NativePage,nextToken=b.NextToken,hasMore=b.HasMore};
    public async Task<object> Read(string id,SourceQuery q,CancellationToken ct)
    {
        if(q.Page<0||q.Page>100000||q.Search?.Length>100||q.Token?.Length>2048||q.SearchType is not("SKU" or "BARCODE" or "CARD_ID")||q.Approval is not("approved" or "unapproved"))throw new IntegrationException("invalid_query","Mağaza arama alanlarını kontrol et.",400);
        if(!registry.Connections.TryGetValue(id,out var store))throw new IntegrationException("not_found","Mağaza bulunamadı.",404);
        var gate=registry.Gates.GetOrAdd(id,_=>new(1,1));if(!await gate.WaitAsync(0,ct))throw new IntegrationException("busy","Mağazada başka bir işlem sürüyor. Yeniden dene.",409);
        try{
            CatalogCandidate[] rows;string? next=null;bool more;var size=store.Settings.Marketplace=="amazon"&&store.Settings.Mode=="real"?20:25;
            if(store.Settings.Marketplace=="ticimax"&&store.Settings.Mode=="real"){
                if(q.Approval!="approved")throw new IntegrationException("unsupported_filter","Ticimax ürün kartlarında onay bekleyen başvuru filtresi bulunmuyor.",400);
                var cards=await (clients?.Adapter<TicimaxAdapter>(store.Settings)??ticimax).Cards(store.Settings,new(q.Search,q.SearchType,Page:q.Page,Size:25),ct);rows=cards.Items.SelectMany(c=>c.Variants.Select(v=>new CatalogCandidate(CatalogHub.Key(id,CatalogHub.Identity(v.Id,v.Sku,v.Barcode)),v.Id,v.Sku,v.Barcode,c.Name,c.Category,c.Brand,v.Quantity,v.Price,"",v.VatIncluded,c.Active==true?"active":"inactive",c.Images,v.Attributes,c.Description))).ToArray();more=cards.HasMore;
            }else{
                if(q.Search is {Length:>0}&&q.SearchType=="CARD_ID")throw new IntegrationException("unsupported_filter","Bu mağazada SKU veya barkodla ara.",400);
                var adapter=store.Settings.Mode=="demo"?demo:clients?.Adapter(store.Settings)??adapters.FirstOrDefault(a=>a.Marketplace==store.Settings.Marketplace)??throw new IntegrationException("unsupported_marketplace","Bu mağaza desteklenmiyor.",409);
                var page=await adapter.ProductsAsync(store.Settings,new(q.Page,size,q.SearchType=="BARCODE"?q.Search:null,q.SearchType=="SKU"?q.Search:null,NextPageToken:q.Token,Approval:q.Approval),ct);rows=page.Items.Select(p=>CatalogHub.Candidate(store,p)).ToArray();next=page.NextPageToken;more=page.HasMore??(page.TotalPages.HasValue?q.Page+1<page.TotalPages:next!=null||page.Items.Count>=size);
            }
            if(rows.Length>5000)throw new IntegrationException("page_too_large","Bu sayfada çok fazla varyant var. Tam ürün kimliğiyle filtrele.",409);
            var repeated=rows.GroupBy(r=>r.Key).Any(g=>g.Count()>1);if(repeated)throw new IntegrationException("ambiguous_identity","Mağaza aynı kimlikle birden fazla varyant döndürdü; aktarım yapılmadı.",409);
            foreach(var old in batches.Where(b=>b.Value.ObservedAt<DateTimeOffset.UtcNow.AddMinutes(-10)).ToArray())batches.TryRemove(old.Key,out _);while(batches.Count>=32){var oldest=batches.MinBy(b=>b.Value.ObservedAt);batches.TryRemove(oldest.Key,out _);}
            var batch=new CandidateBatch(Guid.NewGuid().ToString("N"),id,CatalogHub.Source(store),DateTimeOffset.UtcNow,rows,q.Page,next,more);batches[batch.Token]=batch;registry.Connections[id]=store with{Status=store.Settings.Mode=="demo"?"demo":"connected",LastAction="catalog-products",LastOperation=DateTimeOffset.UtcNow,LastCount=rows.Length,ErrorCode=null,Message="Katalog aktarımı için ürün sayfası okundu."};registry.Save();return Page(batch,0);
        }finally{gate.Release();}
    }
}
