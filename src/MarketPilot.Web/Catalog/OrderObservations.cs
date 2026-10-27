using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Amazon;
using MarketPilot.Web.Integrations.N11;
using MarketPilot.Web.Integrations.Boyner;
using MarketPilot.Web.Integrations.Ticimax;
namespace MarketPilot.Web.Catalog;
public sealed record ObservedOrder(string StoreId,string Source,MarketplaceOrder Order,DateTimeOffset ObservedAt);
public sealed partial class OperationsHub
{
    // Capture only the page actually read by a centre. Never scan an entire remote account.
    public void Observe(ConnectionState store,object result)
    {
        if(store.Settings.Mode!="real")return;
        IEnumerable<MarketplaceOrder> rows=result switch{
            MarketplacePage<MarketplaceOrder> p=>p.Items,
            AmazonPage<AmazonOrder> p=>p.Items.Select(r=>r.Order),
            N11Page<N11Order> p=>p.Items.Select(r=>r.Order),
            BoynerPage<BoynerOrder> p=>p.Items.Select(r=>r.Order),
            TicimaxPage<TicimaxOrder> p=>p.Items.Select(r=>new MarketplaceOrder(r.Id,r.Number,r.StatusName.Length>0?r.StatusName:TicimaxStatus(r.Status),r.Date,r.CustomerName,"",r.Total,null,r.Discount,r.Currency,"","",null,[],new("",r.TrackingNumber,"","","",null,null),new(r.InvoiceNumber,"","",false,"","","",null,[]))),
            _=>[]};var all=rows.ToArray();if(all.Length==0)return;
        db.Modify(s=>{foreach(var order in all){var identity=order.PackageId.Length>0?order.PackageId:order.OrderNumber;if(identity.Length==0)continue;var key=CatalogHub.Key("observed",CatalogHub.Source(store)+"|"+identity);s.Catalog.Orders[key]=new(store.Id,CatalogHub.Source(store),order,DateTimeOffset.UtcNow);}return true;});
    }
    private static string TicimaxStatus(int? s)=>s switch{0 or 1 or 2 or 3=>"Created",4 or 5=>"Picking",6=>"Shipped",7=>"Delivered",8=>"Cancelled",9 or 11 or 12 or 13 or 16 or 17=>"Returned",_=>"Bildirilmedi"};
    private Order Project(ConnectionState store,MarketplaceOrder o,Dictionary<string,string?> links)=>new(){Id=CatalogHub.Key(store.Id,"order:"+(o.PackageId.Length>0?o.PackageId:o.OrderNumber)),StoreId=store.Id,PackageId=o.PackageId,ChannelOrderNumber=o.OrderNumber,SourceMode=store.Settings.Mode,Customer=o.CustomerName,Channel=Name(store.Settings.Marketplace),Product=string.Join(" · ",o.Lines.Select(l=>l.Name)),Total=o.Total??0,TotalKnown=o.Total.HasValue,Currency=o.Currency,Quantity=o.Lines.Sum(l=>l.Quantity),Status=Status(o.Status),Date=o.OrderDate??DateTimeOffset.MinValue,Lines=o.Lines.Select(l=>new OperationLine(l.Name,l.Sku,l.Barcode,l.Quantity,l.GrossAmount,links.GetValueOrDefault(store.Id+"|"+l.Barcode))).ToArray()};
}
