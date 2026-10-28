using MarketPilot.Web.Integrations.Demo;
namespace MarketPilot.Web.Integrations.Boyner;
public static class BoynerEndpoints
{
    public static void AddBoynerIntegration(this IServiceCollection services){services.AddHttpClient("Boyner",c=>c.Timeout=TimeSpan.FromSeconds(45)).ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false});services.AddSingleton<BoynerAdapter>(p=>new(p.GetRequiredService<IHttpClientFactory>().CreateClient("Boyner"),p.GetRequiredService<IConfiguration>()));services.AddTransient<IMarketplaceAdapter>(p=>p.GetRequiredService<BoynerAdapter>());}
    public static void MapBoyner(this RouteGroupBuilder group)
    {
        group.MapGet("/boyner/connections/{id}/search/{kind}",async(string id,string kind,[AsParameters] BoynerQuery query,IntegrationRegistry registry,DemoWorkspace demo,MarketplaceClients clients,MarketPilot.Web.Catalog.OperationsHub operations,CancellationToken ct)=>
        {
            if(!registry.Connections.TryGetValue(id,out var store)||store.Settings.Marketplace!="boyner")return Results.NotFound();if(BoynerAdapter.Validate(query) is {} error)return Results.BadRequest(new{error});var gate=registry.Gates.GetOrAdd(id,_=>new(1,1));if(!await gate.WaitAsync(0,ct))return Results.Json(new{error="Mağazada başka bir işlem sürüyor. Yeniden deneyin."},statusCode:409);
            try
            {
                var adapter = store.Settings.Mode == "demo" ? null : clients.Adapter<BoynerAdapter>(store.Settings);var result=store.Settings.Mode=="demo"?BoynerDemo.Read(demo,store.Settings,kind,query):await adapter!.Read(store.Settings,kind,query,ct);if(kind is "orders" or "shipping" or "invoices" or "finance" or "addresses")operations.Observe(store,result);registry.Connections[id]=store with{Status=store.Settings.Mode=="demo"?"demo":"connected",LastAction=kind,LastOperation=DateTimeOffset.UtcNow,ErrorCode=null,Message="Boyner çalışma alanı okundu."};registry.Save();return Results.Ok(result);}
            catch(Exception ex)when(ex is IntegrationException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or OverflowException){var e=ex as IntegrationException??new IntegrationException("read_failed","Boyner verileri okunamadı. Mağaza bağlantısını kontrol edip yeniden deneyin.");if(e.Status!=400){registry.Connections[id]=store with{Status="error",LastOperation=DateTimeOffset.UtcNow,LastAction=kind,ErrorCode=e.Code,Message=e.Message};registry.Save();}return Results.Json(new{error=e.Message,code=e.Code,connection=registry.Connections[id]},statusCode:e.Status);}finally{gate.Release();}
        });
    }
}
public static class BoynerDemo
{
    public static object Read(DemoWorkspace demo,ConnectionSettings s,string kind,BoynerQuery q)
    {
        var w=demo.Snapshot(s);BoynerPage<T> Page<T>(IEnumerable<T> source){var all=source.ToArray();return new(all.Skip(q.Page*q.Size).Take(q.Size).ToArray(),q.Page,q.Size,all.Length,(int)Math.Ceiling(all.Length/(double)q.Size));}bool Date(DateTimeOffset? d)=>(!q.StartDate.HasValue||d?.ToUnixTimeMilliseconds()>=q.StartDate)&&(!q.EndDate.HasValue||d?.ToUnixTimeMilliseconds()<=q.EndDate);
        if(kind is "products" or "inventory")return Page(w.Products.Where(p=>(string.IsNullOrWhiteSpace(q.Search)||(q.SearchType switch{"BARCODE"=>p.Barcode,"MODEL"=>p.ProductMainId,_=>p.Sku}).Equals(q.Search,StringComparison.OrdinalIgnoreCase))&&(string.IsNullOrWhiteSpace(q.Status)||q.Status=="unapproved"&&p.ApprovalStatus!="approved"||p.ApprovalStatus==q.Status)&&(string.IsNullOrWhiteSpace(q.SaleStatus)||p.OnSale==(q.SaleStatus=="true"))).Select(p=>new BoynerProduct(p,w.ProductInfo[p.Barcode].Description,w.ProductInfo[p.Barcode].Attributes,3,8,20)));
        if(kind is "orders" or "shipping" or "invoices" or "finance")return Page(w.Orders.Where(o=>Date(o.OrderDate)&&(string.IsNullOrWhiteSpace(q.Search)||o.OrderNumber==q.Search)&&(string.IsNullOrWhiteSpace(q.Status)||o.Status==q.Status)).Select(o=>new BoynerOrder(o with{Cargo=o.Cargo with{Provider="Satıcı anlaşmalı kargo · Demo"}},o.PackageId,o.OrderDate?.AddDays(3),w.Events.Where(e=>e.Reference==o.PackageId).Select(e=>new BoynerHistory(e.Title,e.Date)).ToArray())));
        if(kind=="claims")return Page(w.Claims.Where(c=>Date(c.ClaimDate)&&(string.IsNullOrWhiteSpace(q.Search)||c.OrderNumber==q.Search)&&(string.IsNullOrWhiteSpace(q.Status)||c.Status==q.Status)).Select(c=>new BoynerClaim(c.Id,c.OrderNumber,c.CustomerName,c.ClaimDate.ToString("dd.MM.yyyy"),[new(c.Id,w.Products.Find(p=>p.Barcode==c.Barcode)?.Name??"",w.Products.Find(p=>p.Barcode==c.Barcode)?.Sku??"",c.Barcode,c.Quantity,c.Status,c.Reason)])));
        if(kind=="history")return Page(w.Jobs.Where(j=>(string.IsNullOrWhiteSpace(q.Status)||j.State==q.Status)&&Date(j.CreatedAt)).OrderByDescending(j=>j.CreatedAt));
        if(kind=="catalog")return Page(new[]{new{id="textile",name="Günlük koleksiyon",path="Giyim / Günlük koleksiyon · Demo"}}.Where(c=>string.IsNullOrWhiteSpace(q.Search)||c.path.Contains(q.Search,StringComparison.OrdinalIgnoreCase)));
        if(kind=="brands")return Page(new[]{new{id="DEMO-BRAND",name="İnternet Tekstil · Demo"}});
        if(kind=="attributes")return Page(new[]{new{id="demo-color",name="Renk",required=true,varianter=true,allowCustom=false,values=new[]{new{id="demo-ekru",name="Ekru"}},valueCount=1},new{id="demo-size",name="Beden",required=true,varianter=true,allowCustom=false,values=new[]{new{id="demo-s",name="S"}},valueCount=1}});
        if(kind=="addresses")return Page(new[]{new MarketplaceAddress("demo-shipping","Sevkiyat","Türkiye","İstanbul","Örnek ilçe","34000","Örnek Sanayi Mahallesi, Demo Cadde No: 10, İstanbul — Gerçek adres değildir.",true),new MarketplaceAddress("demo-return","İade","Türkiye","İstanbul","Örnek ilçe","34000","Örnek Merkez Mahallesi, Test Sokak No: 2, İstanbul — Gerçek adres değildir.",false)});
        throw new IntegrationException("not_available","Bu bölüm bulunamadı.",404);
    }
}
