using System.Text.Json;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Demo;
namespace MarketPilot.Web.Catalog;

public sealed record OperationLine(string Name,string Sku,string Barcode,int Quantity,decimal? Amount,string? ProductId);
public sealed partial class OperationsHub(DemoStore db,IntegrationRegistry registry,DemoWorkspace demo)
{
    public static string Name(string market)=>market switch{"trendyol"=>"Trendyol","hepsiburada"=>"Hepsiburada","amazon"=>"Amazon","boyner"=>"Boyner","ticimax"=>"Ticimax",_=>market};
    private static string Status(string status)=>status switch{"Created" or "Unshipped" or "Pending" or "PendingAvailability"=>"Yeni","Picking" or "Invoiced"=>"Hazırlanıyor","Shipped"=>"Kargolandı","Delivered"=>"Tamamlandı","Cancelled" or "Canceled"=>"İptal","Returned"=>"İade",_=>status};
    private static string ShipmentStatus(string status)=>status switch{"Shipped"=>"Kargoya verildi","Delivered"=>"Teslim edildi","Cancelled"=>"İptal",_=>"Hazırlanıyor"};
    private (ConnectionState Store,DemoWorkspaceState Workspace)[] Workspaces()=>registry.Connections.Values.Where(c=>c.Settings.Mode=="demo").GroupBy(CatalogHub.Source).Select(g=>g.First()).Select(c=>(c,demo.Snapshot(c.Settings))).ToArray();
    public ListingPage<Order> Orders(ListingQuery q)
    {
        var spaces=Workspaces();return db.CatalogRead(s=>{
            var byStore=s.Catalog.Listings.Values.GroupBy(l=>l.StoreId+"|"+l.Barcode).ToDictionary(g=>g.Key,g=>g.Select(l=>l.ProductId).Distinct().Count()==1?g.First().ProductId:null);
            var channels=spaces.SelectMany(w=>w.Workspace.Orders.Select(o=>new Order{Id=CatalogHub.Key(w.Store.Id,"order:"+o.PackageId),StoreId=w.Store.Id,PackageId=o.PackageId,ChannelOrderNumber=o.OrderNumber,SourceMode="demo",Customer=o.CustomerName,Channel=Name(w.Store.Settings.Marketplace),Product=string.Join(" · ",o.Lines.Select(l=>l.Name)),Total=o.Total??0,Currency=o.Currency,Quantity=o.Lines.Sum(l=>l.Quantity),Status=Status(o.Status),Date=o.OrderDate??DateTimeOffset.MinValue,Lines=o.Lines.Select(l=>new OperationLine(l.Name,l.Sku,l.Barcode,l.Quantity,l.GrossAmount,byStore.GetValueOrDefault(w.Store.Id+"|"+l.Barcode))).ToArray()}));
            var observed=s.Catalog.Orders.Values.Where(v=>registry.Connections.TryGetValue(v.StoreId,out var c)&&CatalogHub.Source(c)==v.Source).Select(v=>Project(registry.Connections[v.StoreId],v.Order,byStore));
            var rows=s.Orders.Concat(channels).Concat(observed).Where(o=>string.IsNullOrWhiteSpace(q.Search)||ListingFilters.Text(o.Id,q.Search)||ListingFilters.Text(o.ChannelOrderNumber,q.Search)||ListingFilters.Text(o.Customer,q.Search)||ListingFilters.Text(o.Product,q.Search)||o.Lines.Any(l=>ListingFilters.Text(l.Barcode,q.Search)||ListingFilters.Text(l.Sku,q.Search)))
                .Where(o=>ListingFilters.Exact(o.Status,q.Status)&&ListingFilters.Exact(o.Channel,q.Channel)&&ListingFilters.Date(o.Date,q)&&ListingFilters.Range(o.Total,q.MinPrice,q.MaxPrice));
            rows=q.Sort switch{"oldest"=>rows.OrderBy(o=>o.Date).ThenBy(o=>o.Id),"priceAsc"=>rows.OrderBy(o=>o.Total).ThenBy(o=>o.Id),"priceDesc"=>rows.OrderByDescending(o=>o.Total).ThenBy(o=>o.Id),_=>rows.OrderByDescending(o=>o.Date).ThenBy(o=>o.Id)};
            return JsonSerializer.Deserialize<ListingPage<Order>>(JsonSerializer.Serialize(ListingFilters.Page(rows,q)))!;
        });
    }
    public ListingPage<Shipment> Shipments(ListingQuery q)
    {
        var spaces=Workspaces();return db.CatalogRead(s=>{
            var channels=spaces.SelectMany(w=>w.Workspace.Orders.Where(o=>o.Cargo.TrackingNumber.Length>0).Select(o=>new Shipment{Id=CatalogHub.Key(w.Store.Id,"shipment:"+o.PackageId),StoreId=w.Store.Id,OrderId=o.OrderNumber,Carrier=o.Cargo.Provider,TrackingNumber=o.Cargo.TrackingNumber,Status=ShipmentStatus(o.Status),CreatedAt=o.OrderDate??DateTimeOffset.MinValue}));
            var observed=s.Catalog.Orders.Values.Where(v=>v.Order.Cargo.TrackingNumber.Length>0&&registry.Connections.TryGetValue(v.StoreId,out var c)&&CatalogHub.Source(c)==v.Source).Select(v=>new Shipment{Id=CatalogHub.Key(v.StoreId,"shipment:"+(v.Order.PackageId.Length>0?v.Order.PackageId:v.Order.OrderNumber)),StoreId=v.StoreId,OrderId=v.Order.OrderNumber,Carrier=v.Order.Cargo.Provider,TrackingNumber=v.Order.Cargo.TrackingNumber,Status=ShipmentStatus(v.Order.Status),CreatedAt=v.Order.OrderDate??DateTimeOffset.MinValue});var rows=s.Shipments.Concat(channels).Concat(observed).Where(sh=>string.IsNullOrWhiteSpace(q.Search)||ListingFilters.Text(sh.OrderId,q.Search)||ListingFilters.Text(sh.TrackingNumber,q.Search)||ListingFilters.Text(sh.Carrier,q.Search)).Where(sh=>ListingFilters.Exact(sh.Status,q.Status)&&ListingFilters.Text(sh.Carrier,q.Carrier)&&ListingFilters.Date(sh.CreatedAt,q));rows=q.Sort=="oldest"?rows.OrderBy(sh=>sh.CreatedAt).ThenBy(sh=>sh.Id):rows.OrderByDescending(sh=>sh.CreatedAt).ThenBy(sh=>sh.Id);return JsonSerializer.Deserialize<ListingPage<Shipment>>(JsonSerializer.Serialize(ListingFilters.Page(rows,q)))!;
        });
    }
    public object Overview()
    {
        var orders=Orders(new(Size:5));var spaces=Workspaces();return db.CatalogRead(s=>{
            var summary=s.Orders.Select(o=>(Channel:o.Channel,Amount:o.Total,Currency:o.Currency,Cancelled:o.Status=="İptal"))
                .Concat(spaces.SelectMany(w=>w.Workspace.Orders.Select(o=>(Name(w.Store.Settings.Marketplace),o.Total??0,o.Currency,Status(o.Status)=="İptal"))))
                .Concat(s.Catalog.Orders.Values.Where(v=>registry.Connections.TryGetValue(v.StoreId,out var c)&&CatalogHub.Source(c)==v.Source).Select(v=>(Name(registry.Connections[v.StoreId].Settings.Marketplace),v.Order.Total??0,v.Order.Currency,Status(v.Order.Status)=="İptal"))).ToArray();
            return new{revenue=summary.Where(o=>o.Currency=="TRY"&&!o.Item4).Sum(o=>o.Item2),orders=orders.TotalElements,products=s.Products.Count,lowStock=s.Products.Count(p=>p.StockKnown&&p.Stock<=s.Settings.LowStockThreshold),activeConnections=registry.Connections.Values.Count(c=>c.Settings.Mode=="real"&&c.Status=="connected"),lastSync=s.LastSync,latestOrders=orders.Items,activity=s.Activity.Take(5),channels=summary.GroupBy(o=>o.Item1).Select(g=>new{name=g.Key,total=g.Where(o=>o.Currency=="TRY"&&!o.Item4).Sum(o=>o.Item2),count=g.Count()}).ToArray()};
        });
    }

}
