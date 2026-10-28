using MarketPilot.Web.Integrations.Demo;

namespace MarketPilot.Web.Integrations.N11;
public static class N11Endpoints
{
    public static void AddN11Integration(this IServiceCollection services){services.AddHttpClient("N11",c=>c.Timeout=TimeSpan.FromSeconds(45)).ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false});services.AddSingleton<N11Adapter>(p=>new N11Adapter(p.GetRequiredService<IHttpClientFactory>().CreateClient("N11"),p.GetRequiredService<IConfiguration>()));services.AddTransient<IMarketplaceAdapter>(p=>p.GetRequiredService<N11Adapter>());}
    public static void MapN11(this RouteGroupBuilder group)
    {
        group.MapGet("/n11/connections/{id}/search/{kind}",async(string id,string kind,[AsParameters] N11Query query,IntegrationRegistry registry,DemoWorkspace demo,MarketplaceClients clients,MarketPilot.Web.Catalog.OperationsHub operations,CancellationToken ct)=>
        {
            if(!registry.Connections.TryGetValue(id,out var store)||store.Settings.Marketplace!="n11")return Results.NotFound();if(query.Error() is {} error)return Results.BadRequest(new{error});
            var gate=registry.Gates.GetOrAdd(id,_=>new(1,1));if(!await gate.WaitAsync(0,ct))return Results.Json(new{error="Mağazada başka bir işlem sürüyor; yeniden deneyin."},statusCode:409);
            try
            {
                var adapter = store.Settings.Mode == "demo" ? null : clients.Adapter<N11Adapter>(store.Settings);var result=store.Settings.Mode=="demo"?N11Demo.Read(demo,store.Settings,kind,query):await adapter!.Read(store.Settings,kind,query,ct);if(kind is "orders" or "shipping" or "invoices" or "finance" or "addresses")operations.Observe(store,result);registry.Connections[id]=store with{Status=store.Settings.Mode=="demo"?"demo":"connected",LastAction=kind,LastOperation=DateTimeOffset.UtcNow,ErrorCode=null,Message=store.Settings.Mode=="demo"?"Demo kayıtları okundu.":"n11 verileri okundu."};registry.Save();return Results.Ok(result);}
            catch(Exception ex)when(ex is IntegrationException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or System.Xml.XmlException or OverflowException){var e=ex as IntegrationException??new IntegrationException("read_failed","n11 verileri okunamadı. Mağaza bağlantısını kontrol edip yeniden deneyin.");registry.Connections[id]=store with{Status="error",LastAction=kind,LastOperation=DateTimeOffset.UtcNow,ErrorCode=e.Code,Message=e.Message};registry.Save();return Results.Json(new{error=e.Message,code=e.Code},statusCode:e.Status);}finally{gate.Release();}
        });
    }
}
public static class N11Demo
{
    public static bool Depot(string barcode)=>barcode=="8691000000005";
    public static bool DepotOrder(MarketplaceOrder o)=>o.Lines.Any(l=>Depot(l.Barcode));
    public static object Read(DemoWorkspace demo,ConnectionSettings settings,string kind,N11Query q)
    {
        var w=demo.Snapshot(settings);bool Sender(string value)=>q.Sender=="ALL"||q.Sender==value;bool Date(DateTimeOffset? date)=>(!q.StartDate.HasValue||date?.ToUnixTimeMilliseconds()>=q.StartDate)&&(!q.EndDate.HasValue||date?.ToUnixTimeMilliseconds()<=q.EndDate);
        N11Page<T> Page<T>(IEnumerable<T> source){var all=source.ToArray();return new(all.Skip(q.Page*q.Size).Take(q.Size).ToArray(),q.Page,q.Size,all.Length,(int)Math.Ceiling(all.Length/(double)q.Size));}
        N11Product Product(MarketplaceProduct p){var info=w.ProductInfo[p.Barcode];var status=p.ApprovalStatus switch{"approved"=>"Active","pendingApproval"=>"InCatalogApproval",_=>"CatalogRejected"};return new(p with{ApprovalStatus=status},info.Description,info.Attributes,Depot(p.Barcode)?"N11":"SELLER",3,"İstanbul standart gönderim · Demo",20,8);}
        N11Order Order(MarketplaceOrder o){var amounts=o.Lines.Select(l=>new N11Amount(l.Id,l.Name,l.Quantity,l.UnitPrice,l.GrossAmount,8,0)).ToArray();return new(o with{Cargo=o.Cargo with{Provider="Satıcı anlaşmalı kargo · Demo"}},DepotOrder(o)?"N11":"SELLER",amounts.Sum(l=>l.InvoiceAmount),amounts,w.Events.Where(e=>e.Reference==o.PackageId).Select(e=>new N11History(e.Title,e.Date)).ToArray(),o.OrderDate?.AddDays(3),false);}
        if(kind is "products" or "inventory" or "depot")return Page(w.Products.Select(Product).Where(p=>(kind!="depot"||p.Sender=="N11")&&Sender(p.Sender)&&(string.IsNullOrWhiteSpace(q.Search)||(q.SearchType switch{"MODEL"=>p.Product.ProductMainId,"ID"=>p.Product.Id,_=>p.Product.Sku}).Equals(q.Search,StringComparison.OrdinalIgnoreCase))&&(string.IsNullOrWhiteSpace(q.Status)||p.Product.ApprovalStatus==q.Status)&&(string.IsNullOrWhiteSpace(q.Brand)||p.Product.BrandName.Equals(q.Brand,StringComparison.OrdinalIgnoreCase))&&(string.IsNullOrWhiteSpace(q.SaleStatus)||q.SaleStatus=="On_Sale"&&p.Product.OnSale==true||q.SaleStatus=="Out_Of_Stock"&&p.Product.Stock==0)));
        if(kind is "orders" or "shipping" or "invoices" or "finance")return Page(w.Orders.Select(Order).Where(o=>Sender(o.Sender)&&Date(o.Order.OrderDate)&&(string.IsNullOrWhiteSpace(q.Search)||o.Order.OrderNumber==q.Search)&&(string.IsNullOrWhiteSpace(q.Status)||o.Order.Status==q.Status)));
        if(kind=="claims")return Page(w.Claims.Where(c=>Date(c.ClaimDate)&&(string.IsNullOrWhiteSpace(q.Status)||c.Status==q.Status)&&(string.IsNullOrWhiteSpace(q.Search)||c.OrderNumber==q.Search)).Select(c=>new{c.Id,c.OrderNumber,c.CustomerName,productName=w.Products.Find(p=>p.Barcode==c.Barcode)?.Name,c.Quantity,c.Reason,c.Status,sender="SELLER",claimDate=c.ClaimDate.ToString("dd/MM/yyyy")}));
        if(kind=="history")return Page(w.Jobs.Where(j=>(string.IsNullOrWhiteSpace(q.Status)||j.State==q.Status)&&Date(j.CreatedAt)).OrderByDescending(j=>j.CreatedAt));
        if(kind=="catalog")return Page(new[]{new{id="textile",name="Günlük koleksiyon",path="Giyim / Günlük koleksiyon · Demo"}});
        throw new IntegrationException("not_available","Bu bölümün gerçek bağlantısı henüz tamamlanmadı.",501);
    }
}
