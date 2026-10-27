using MarketPilot.Web.Integrations.Demo;
namespace MarketPilot.Web.Integrations.Ticimax;
public static class TicimaxEndpoints
{
    public static void AddTicimaxIntegration(this IServiceCollection services){services.AddHttpClient("Ticimax",c=>c.Timeout=TimeSpan.FromSeconds(45)).ConfigurePrimaryHttpMessageHandler(TicimaxSoap.Handler);services.AddSingleton<TicimaxSoap>();services.AddTransient<TicimaxAdapter>();services.AddTransient<IMarketplaceAdapter>(p=>p.GetRequiredService<TicimaxAdapter>());}
    public static void MapTicimax(this RouteGroupBuilder group)
    {
        group.MapGet("/ticimax/connections/{id}/search/{kind}",async(string id,string kind,[AsParameters] TicimaxQuery query,IntegrationRegistry registry,DemoWorkspace demo,TicimaxAdapter adapter,CancellationToken ct)=>
        {
            if(!registry.Connections.TryGetValue(id,out var store)||store.Settings.Marketplace!="ticimax")return Results.NotFound();if(TicimaxAdapter.Validate(query) is {} error)return Results.BadRequest(new{error});var gate=registry.Gates.GetOrAdd(id,_=>new(1,1));if(!await gate.WaitAsync(0,ct))return Results.Json(new{error="Mağazada başka bir işlem sürüyor; yeniden deneyin."},statusCode:409);
            try{var result=store.Settings.Mode=="demo"?TicimaxDemo.Read(demo,store.Settings,kind,query):await adapter.Read(store.Settings,kind,query,ct);registry.Connections[id]=store with{Status=store.Settings.Mode=="demo"?"demo":"connected",LastAction=kind,LastOperation=DateTimeOffset.UtcNow,ErrorCode=null,Message="Ticimax çalışma alanı okundu."};registry.Save();return Results.Ok(result);}
            catch(Exception ex)when(ex is IntegrationException or HttpRequestException or OperationCanceledException or System.Xml.XmlException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException){var e=ex as IntegrationException??new IntegrationException("contract_read_failed","Ticimax sözleşmesi veya yanıtı okunamadı. Mağaza servis yetkisini ve WSDL uyumunu kontrol edin.");if(e.Status!=400){registry.Connections[id]=store with{Status="error",LastAction=kind,LastOperation=DateTimeOffset.UtcNow,ErrorCode=e.Code,Message=e.Message};registry.Save();}return Results.Json(new{error=e.Message,code=e.Code,connection=registry.Connections[id]},statusCode:e.Status);}finally{gate.Release();}
        });
    }
}
public static class TicimaxDemo
{
    public static object Read(DemoWorkspace demo,ConnectionSettings s,string kind,TicimaxQuery q)
    {
        var w=demo.Snapshot(s);TicimaxPage<T> Page<T>(IEnumerable<T> rows){var all=rows.ToArray();return new(all.Skip(q.Page*q.Size).Take(q.Size).ToArray(),q.Page,q.Size,all.Length>(q.Page+1)*q.Size,all.Length);}bool Date(DateTimeOffset? d)=>(!q.StartDate.HasValue||d?.ToUnixTimeMilliseconds()>=q.StartDate)&&(!q.EndDate.HasValue||d?.ToUnixTimeMilliseconds()<=q.EndDate);
        var cards=w.Products.GroupBy(p=>p.ProductMainId).Select((g,i)=>new TicimaxCard((i+1).ToString(),g.First().Name.Split('·')[0].Trim(),g.First().BrandName,g.First().CategoryName,w.ProductInfo[g.First().Barcode].Description,g.Any(p=>p.ApprovalStatus=="approved"),[],g.Select(p=>new TicimaxVariant(p.Id,p.Sku,p.Barcode,p.Stock,p.SalePrice,null,"1",true,20,w.ProductInfo[p.Barcode].Attributes)).ToArray())).ToArray();
        if(kind is "products" or "inventory")return Page(cards.Where(c=>(!q.Active.HasValue||c.Active==(q.Active==1))&&(string.IsNullOrWhiteSpace(q.Search)||q.SearchType=="CARD_ID"&&c.Id==q.Search||q.SearchType=="SKU"&&c.Variants.Any(v=>v.Sku.Equals(q.Search,StringComparison.OrdinalIgnoreCase))||q.SearchType=="BARCODE"&&c.Variants.Any(v=>v.Barcode==q.Search))));
        var orders=w.Orders.Select(o=>{var code=o.Status switch{"Created"=>1,"Picking" or "Invoiced"=>4,"Shipped"=>6,"Delivered"=>7,"Returned"=>9,_=>8};return new TicimaxOrder(o.OrderNumber.Split('-').Last(),o.OrderNumber,code,"",o.OrderDate,o.CustomerName,o.ShipmentAddress?.FullAddress??"","TRY",o.Total,o.Discount,o.Invoice.Number,o.Cargo.TrackingNumber,"Örnek mağaza siparişi.",[new("demo-pay-"+o.PackageId,0,o.Status=="Created"?0:1,o.Total,"TRY",o.OrderDate)],o.PackageId,o.Status);}).ToArray();
        if(kind is "orders" or "finance" or "invoices" or "addresses" or "claims")return Page(orders.Where(o=>Date(o.Date)&&(string.IsNullOrWhiteSpace(q.Search)||o.Number==q.Search)&&(!q.OrderId.HasValue||o.Id==q.OrderId.ToString())&&(!q.Status.HasValue||o.Status==q.Status)&&(kind!="claims"||q.Status.HasValue||o.Status>=9)));
        if(kind is "shipping" or "payments" or "lines")
        {
            var o=orders.FirstOrDefault(o=>o.Id==q.OrderId?.ToString())??throw new IntegrationException("order_required","Önce sipariş listesinden bir kayıt seçin.",400);if(kind=="payments")return Page(o.Payments);
            var raw=w.Orders.Single(r=>r.PackageId==o.DemoReference);if(kind=="shipping")return Page(raw.Cargo.TrackingNumber.Length==0?Array.Empty<TicimaxPackage>():[new TicimaxPackage(raw.PackageId,o.Id,"Satıcı kargosu · Demo",raw.Cargo.TrackingNumber,"",raw.Cargo.TrackingNumber)]);
            return Page(raw.Lines.Select(l=>new{id=l.Id,name=l.Name,sku=l.Sku,barcode=l.Barcode,quantity=(decimal?)l.Quantity,amount=l.GrossAmount,vat=l.VatRate}));
        }
        if(kind=="history")return Page(w.Jobs.Where(j=>Date(j.CreatedAt)).OrderByDescending(j=>j.CreatedAt));
        if(kind is "catalog" or "brands" or "currencies" or "suppliers")return Page(new[]{new{id="1",name=kind switch{"brands"=>"İnternet Tekstil", "currencies"=>"Türk Lirası", "suppliers"=>"Örnek tekstil tedarikçisi",_=>"Giyim / Günlük koleksiyon"},parentId="0",currency=kind=="currencies"?"TRY":"",active=true}}.Where(r=>string.IsNullOrWhiteSpace(q.Search)||r.name.Contains(q.Search,StringComparison.OrdinalIgnoreCase)));
        throw new IntegrationException("not_available","Bu bölüm bulunamadı.",404);
    }
}
