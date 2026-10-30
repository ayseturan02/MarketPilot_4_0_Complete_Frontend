using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Demo;
namespace MarketPilot.Web.Catalog;

public sealed record OperationLine(string Name, string Sku, string Barcode, int Quantity, decimal? Amount, string? ProductId,string? NativeLineId=null);
public sealed partial class OperationsHub(DemoStore db, IntegrationRegistry registry, DemoWorkspace demo, IConfiguration? configuration = null)
{
    public static string Name(string market) => market switch { "trendyol" => "Trendyol", "hepsiburada" => "Hepsiburada", "amazon" => "Amazon", "boyner" => "Boyner", "ticimax" => "Ticimax", _ => market };
    private static string Status(string status) => status switch { "Created" or "Unshipped" or "Pending" or "PendingAvailability" => "Yeni", "Picking" or "Invoiced" => "Hazırlanıyor", "Shipped" => "Kargolandı", "Delivered" => "Tamamlandı", "Cancelled" or "Canceled" or "UnPacked" => "İptal", "Returned" => "İade", _ => status };
    private static string ShipmentStatus(string status) => status switch { "Shipped" => "Kargoya verildi", "Delivered" => "Teslim edildi", "Cancelled" => "İptal", _ => "Hazırlanıyor" };
    private (ConnectionState Store, DemoWorkspaceState Workspace)[] Workspaces() => registry.Connections.Values.Where(c => c.Settings.Mode == "demo").GroupBy(CatalogHub.Source).Select(g => g.First()).Select(c => (c, demo.Snapshot(c.Settings))).ToArray();
    // Internal aggregation snapshot includes every order, independently of API paging limits.
    public IReadOnlyList<Order> AllOrders() => Orders(new ListingQuery(Size: int.MaxValue)).Items;
    public ListingPage<Order> Orders(ListingQuery q)
    {
        var spaces = Workspaces();
        return db.CatalogRead(s =>
        {
            var erpProductIds=s.Products.Where(p=>p.SourceSystem==ProductOwnershipPolicy.ErpSource).Select(p=>p.Id).ToHashSet();
            var byStore = s.Catalog.Listings.Values.Where(l=>erpProductIds.Contains(l.ProductId)).GroupBy(l => l.StoreId + "|" + l.Barcode).ToDictionary(g => g.Key, g => g.Select(l => l.ProductId).Distinct().Count() == 1 ? g.First().ProductId : null);
            var channels = spaces.SelectMany(w => w.Workspace.Orders.Select(o => Project(w.Store, o, byStore)));
            var observed = s.Catalog.Orders.Values.Where(v => ObservationActive(v)).Select(v => Project(registry.Connections[v.StoreId], v.Order, byStore, v.SourceVersion, v.ObservedAt));
            var rows = s.Orders.Concat(channels).Concat(observed).Where(o => string.IsNullOrWhiteSpace(q.Search) || ListingFilters.Text(o.Id, q.Search) || ListingFilters.Text(o.ChannelOrderNumber, q.Search) || ListingFilters.Text(o.Customer, q.Search) || ListingFilters.Text(o.Product, q.Search) || o.Lines.Any(l => ListingFilters.Text(l.Barcode, q.Search) || ListingFilters.Text(l.Sku, q.Search)))
                .Where(o => ListingFilters.Exact(o.Status, q.Status) && ListingFilters.Exact(o.Channel, q.Channel) && ListingFilters.Date(o.Date, q) && ListingFilters.Range(o.Total, q.MinPrice, q.MaxPrice));
            rows = q.Sort switch {
                "oldest" => rows.OrderBy(o => o.Date).ThenBy(o => o.Id),
                "priceAsc" => rows.OrderBy(o => o.Total).ThenBy(o => o.Id),
                "priceDesc" => rows.OrderByDescending(o => o.Total).ThenBy(o => o.Id),
                _ => rows.OrderByDescending(o => o.Date).ThenBy(o => o.Id)
            };
            return JsonSerializer.Deserialize<ListingPage<Order>>(JsonSerializer.Serialize(ListingFilters.Page(rows, q)))!;
        });
    }
    public ListingPage<Shipment> Shipments(ListingQuery q)
    {
        var spaces = Workspaces();
        return db.CatalogRead(s =>
        {
            var channels = spaces.SelectMany(w => w.Workspace.Orders.Where(o => o.Cargo.TrackingNumber.Length > 0).Select(o => MarketplaceShipment(w.Store, o)));
            var observed = s.Catalog.Orders.Values.Where(v => v.Order.Cargo.TrackingNumber.Length > 0 && ObservationActive(v)).Select(v => MarketplaceShipment(registry.Connections[v.StoreId], v.Order, v.SourceVersion, v.ObservedAt));
            var rows = s.Shipments.Where(sh=>sh.Source.Length==0||registry.Connections.TryGetValue(sh.StoreId,out var owner)&&CatalogHub.Source(owner)==sh.Source).Concat(channels).Concat(observed).GroupBy(sh=>sh.StoreId+"|"+sh.OrderId).Select(g=>g.First()).Where(sh => string.IsNullOrWhiteSpace(q.Search) || ListingFilters.Text(sh.OrderId, q.Search) || ListingFilters.Text(sh.TrackingNumber, q.Search) || ListingFilters.Text(sh.Carrier, q.Search)).Where(sh => ListingFilters.Exact(sh.Status, q.Status) && ListingFilters.Text(sh.Carrier, q.Carrier) && ListingFilters.Date(sh.CreatedAt, q));
            rows = q.Sort == "oldest" ? rows.OrderBy(sh => sh.CreatedAt).ThenBy(sh => sh.Id) : rows.OrderByDescending(sh => sh.CreatedAt).ThenBy(sh => sh.Id);
            return JsonSerializer.Deserialize<ListingPage<Shipment>>(JsonSerializer.Serialize(ListingFilters.Page(rows, q)))!;
        });
    }
    public Activity[] Activities() => Activities(Workspaces());
    private Activity[] Activities((ConnectionState Store, DemoWorkspaceState Workspace)[] spaces)
    {
        var local = db.CatalogRead(s => s.Activity.Select(a => new Activity { Title = a.Title, Detail = a.Detail, At = a.At }).ToArray());
        var events = spaces.SelectMany(w => w.Workspace.Events.Select(e => new Activity { Title = e.Title, Detail = w.Store.Settings.Name + (e.Reference is null ? "" : " · " + e.Reference), At = e.Date }));
        return local.Concat(events).OrderByDescending(a => a.At).Take(200).ToArray();
    }

    private static Order Project(ConnectionState store, MarketplaceOrder o, Dictionary<string, string?> links, long? sourceVersion = null, DateTimeOffset? observedAt = null)
    {
        var identity = o.PackageId.Length > 0 ? o.PackageId : o.OrderNumber;
        var references = new List<ExternalReference> { MarketplaceReference("marketplace-order", identity, o, sourceVersion ?? o.LastModifiedDate, observedAt ?? o.OrderDate) };
        if (!string.IsNullOrWhiteSpace(o.Invoice.Number))
            references.Add(MarketplaceReference("marketplace-invoice-observation", o.Invoice.Number, o.Invoice, sourceVersion ?? o.LastModifiedDate, observedAt ?? o.OrderDate));
        return new Order { Id = CatalogHub.Key(store.Id, "order:" + identity), StoreId = store.Id, PackageId = o.PackageId, ChannelOrderNumber = o.OrderNumber, SourceMode = store.Settings.Mode, Customer = o.CustomerName, Channel = Name(store.Settings.Marketplace), Product = string.Join(" · ", o.Lines.Select(l => l.Name)), Total = o.Total ?? 0, TotalKnown = o.Total.HasValue, Currency = o.Currency, Invoice = o.Invoice, Cargo = o.Cargo, ShipmentAddress = o.ShipmentAddress, CustomerEmail = o.CustomerEmail, SourceIdentity = CatalogHub.Source(store), Quantity = o.Lines.Sum(l => l.Quantity), Status = Status(o.Status), Date = o.OrderDate ?? DateTimeOffset.MinValue, Lines = o.Lines.Select(l => new OperationLine(l.Name, l.Sku, l.Barcode, l.Quantity, l.GrossAmount, links.GetValueOrDefault(store.Id + "|" + l.Barcode), l.Id)).ToArray(), ExternalReferences = references };
    }

    private static Shipment MarketplaceShipment(ConnectionState store, MarketplaceOrder order, long? sourceVersion = null, DateTimeOffset? observedAt = null)
    {
        var identity = order.PackageId.Length > 0 ? order.PackageId : order.OrderNumber;
        var detail = new { order.Cargo.Provider, order.Cargo.TrackingNumber, order.Status };
        return new Shipment { Id = CatalogHub.Key(store.Id, "shipment:" + identity), StoreId = store.Id, OrderId = order.OrderNumber, Carrier = order.Cargo.Provider, TrackingNumber = order.Cargo.TrackingNumber, Status = ShipmentStatus(order.Status), CreatedAt = order.OrderDate ?? DateTimeOffset.MinValue, ExternalReferences = [MarketplaceReference("marketplace-shipment-observation", identity, detail, sourceVersion ?? order.LastModifiedDate, observedAt ?? order.OrderDate)] };
    }

    private static ExternalReference MarketplaceReference(string entityType, string externalId, object content, long? sourceVersion, DateTimeOffset? observedAt)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content))));
        return new(SourceSystem.Marketplace, entityType, externalId, sourceVersion, null, observedAt ?? DateTimeOffset.UtcNow, hash);
    }

    public object Overview()
    {
        var orders = Orders(new(Size: 5));
        var spaces = Workspaces();
        var activities = Activities(spaces);
        return db.CatalogRead(s =>
        {
            var summary = s.Orders.Select(o => (Channel: o.Channel, Amount: o.TotalKnown ? o.Total : 0, Currency: o.Currency, Cancelled: o.Status == "İptal"))
                .Concat(spaces.SelectMany(w => w.Workspace.Orders.Select(o => (Name(w.Store.Settings.Marketplace), o.Total ?? 0, o.Currency, Status(o.Status) == "İptal"))))
                .Concat(s.Catalog.Orders.Values.Where(v => ObservationActive(v)).Select(v => (Name(registry.Connections[v.StoreId].Settings.Marketplace), v.Order.Total ?? 0, v.Order.Currency, Status(v.Order.Status) == "İptal"))).ToArray();
            return new {
                revenue = summary.Where(o => o.Currency == "TRY" && !o.Item4).Sum(o => o.Item2),
                orders = orders.TotalElements,
                newOrders = s.Orders.Count(o => o.Status == "Yeni") + spaces.Sum(w => w.Workspace.Orders.Count(o => Status(o.Status) == "Yeni")) + s.Catalog.Orders.Values.Count(v => ObservationActive(v) && Status(v.Order.Status) == "Yeni"),
                products = s.Products.Count,
                lowStock = s.Products.Count(p => p.StockKnown && p.Stock <= s.Settings.LowStockThreshold),
                activeConnections = registry.Connections.Values.Count(c => c.Settings.Mode == "real" && c.Status == "connected"),
                lastSync = s.LastSync,
                latestOrders = orders.Items,
                activity = activities.Take(5),
                channels = summary.GroupBy(o => o.Item1).Select(g => new { name = g.Key, total = g.Where(o => o.Currency == "TRY" && !o.Item4).Sum(o => o.Item2), count = g.Count() }).ToArray()
            };
        });
    }

}
