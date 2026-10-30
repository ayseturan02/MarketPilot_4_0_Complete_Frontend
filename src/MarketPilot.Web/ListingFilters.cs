using System.Text.Json;
using MarketPilot.Web.Integrations;

public sealed record ListingQuery(string? Search = null, string? Barcode = null, string? Sku = null, string? Status = null,
    string? Approval = null, string? Category = null, string? Channel = null, string? Carrier = null,
    int? MinStock = null, int? MaxStock = null, decimal? MinPrice = null, decimal? MaxPrice = null,
    long? StartDate = null, long? EndDate = null, string Sort = "newest", int Page = 0, int Size = 25)
{
    public string? Error()
    {
        if (Page < 0 || Size is < 1 or > 100 || (long)Page * Size > int.MaxValue) return "Sayfa ve kayıt sayısı geçersiz (en fazla 100).";
        if (new[] { Search, Barcode, Sku, Status, Approval, Category, Channel, Carrier }.Any(x => x?.Length > 120)) return "Filtre değeri en fazla 120 karakter olabilir.";
        if (MinStock < 0 || MaxStock < 0 || MinStock > MaxStock || MinPrice < 0 || MaxPrice < 0 || MinPrice > MaxPrice) return "Stok ve fiyat aralıklarını kontrol et.";
        if (StartDate < 0 || EndDate < 0 || StartDate > EndDate) return "Tarih aralığını kontrol et.";
        if (!new[] { "newest", "oldest", "name", "stockAsc", "stockDesc", "priceAsc", "priceDesc" }.Contains(Sort)) return "Sıralama seçimi geçersiz.";
        return null;
    }
}
public sealed record ListingPage<T>(IReadOnlyList<T> Items, int Page, int Size, long TotalElements, int TotalPages);
public static class ListingFilters
{
    public static bool Text(string? value, string? term) => string.IsNullOrWhiteSpace(term) || value?.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase) == true;
    public static bool Exact(string? value, string? term) => string.IsNullOrWhiteSpace(term) || value == term.Trim();
    public static bool Range(decimal? value, decimal? min, decimal? max) => (!min.HasValue || value >= min) && (!max.HasValue || value <= max);
    public static bool Date(DateTimeOffset? date, ListingQuery q) => (!q.StartDate.HasValue || date?.ToUnixTimeMilliseconds() >= q.StartDate) && (!q.EndDate.HasValue || date?.ToUnixTimeMilliseconds() <= q.EndDate);
    public static ListingPage<T> Page<T>(IEnumerable<T> source, ListingQuery query)
    {
        var total = source.LongCount(); var rows = source.Skip(checked(query.Page * query.Size)).Take(query.Size).ToArray();
        return new(rows, query.Page, query.Size, total, (int)Math.Ceiling(total / (double)query.Size));
    }
    public static ListingPage<MarketplaceProduct> Products(IEnumerable<MarketplaceProduct> source, ListingQuery q)
    {
        var rows = source.Where(p => string.IsNullOrWhiteSpace(q.Search) || Text(p.Name, q.Search) || Text(p.Sku, q.Search) || Text(p.Barcode, q.Search))
            .Where(p => Exact(p.Barcode, q.Barcode) && Exact(p.Sku, q.Sku) && Text(p.CategoryName, q.Category))
            .Where(p => q.Approval is null or "" or "all" || q.Approval == "unapproved" && p.ApprovalStatus != "approved" || q.Approval == p.ApprovalStatus)
            .Where(p => string.IsNullOrWhiteSpace(q.Status) || p.ApprovalStatus == q.Status || q.Status == "onSale" && p.OnSale == true || q.Status == "notOnSale" && p.OnSale == false)
            .Where(p => Range(p.Stock, q.MinStock, q.MaxStock) && Range(p.SalePrice, q.MinPrice, q.MaxPrice));
        rows = q.Sort switch { "name" => rows.OrderBy(p => p.Name).ThenBy(p => p.Barcode), "stockAsc" => rows.OrderBy(p => p.Stock).ThenBy(p => p.Barcode), "stockDesc" => rows.OrderByDescending(p => p.Stock).ThenBy(p => p.Barcode), "priceAsc" => rows.OrderBy(p => p.SalePrice).ThenBy(p => p.Barcode), "priceDesc" => rows.OrderByDescending(p => p.SalePrice).ThenBy(p => p.Barcode), "oldest" => rows.OrderBy(p => p.Id), _ => rows.OrderByDescending(p => p.Id) };
        return Page(rows, q);
    }
    public static ListingPage<MarketplaceOrder> Orders(IEnumerable<MarketplaceOrder> source, ListingQuery q)
    {
        var rows = source.Where(o => string.IsNullOrWhiteSpace(q.Search) || Text(o.OrderNumber, q.Search) || Text(o.CustomerName, q.Search) || o.Lines.Any(l => Text(l.Name, q.Search)))
            .Where(o => Exact(o.Status, q.Status) && Date(o.OrderDate, q) && Text(o.Cargo.Provider, q.Carrier) && Range(o.Total, q.MinPrice, q.MaxPrice))
            .Where(o => string.IsNullOrWhiteSpace(q.Barcode) || o.Lines.Any(l => l.Barcode == q.Barcode));
        rows = q.Sort switch { "oldest" => rows.OrderBy(o => o.OrderDate).ThenBy(o => o.PackageId), "priceAsc" => rows.OrderBy(o => o.Total).ThenBy(o => o.PackageId), "priceDesc" => rows.OrderByDescending(o => o.Total).ThenBy(o => o.PackageId), _ => rows.OrderByDescending(o => o.OrderDate).ThenBy(o => o.PackageId) };
        return Page(rows, q);
    }
}

// Query the local demo store under its existing lock; clone only the returned page.
public partial class DemoStore
{
    public ListingPage<Product> SearchProducts(ListingQuery q)
    {
        lock (_gate)
        {
            var now=DateTimeOffset.UtcNow;
            decimal? AuthorityPrice(Product p)=>_data.Pricing.LastSourceReadAt.HasValue&&_data.Pricing.LastSourceReadAt.Value.AddMinutes(_data.Pricing.Policy.SourceMaxAgeMinutes)>now?MarketPilot.Web.Pricing.ErpPricingService.MasterEntry(_data,p,now)?.Amount:null;
            var rows = _data.Products.Where(p => p.SourceSystem == MarketPilot.Web.Catalog.ProductOwnershipPolicy.ErpSource)
                .Where(p => string.IsNullOrWhiteSpace(q.Search) || ListingFilters.Text(p.Name, q.Search) || ListingFilters.Text(p.Sku, q.Search) || ListingFilters.Text(p.Barcode, q.Search))
                .Where(p => ListingFilters.Exact(p.Barcode, q.Barcode) && ListingFilters.Exact(p.Sku, q.Sku) && ListingFilters.Exact(p.Status, q.Status) && ListingFilters.Text(p.Category, q.Category))
                .Where(p => string.IsNullOrWhiteSpace(q.Channel) || p.Channels.Contains(q.Channel))
                .Where(p => ListingFilters.Range(p.Stock, q.MinStock, q.MaxStock) && ListingFilters.Range(AuthorityPrice(p), q.MinPrice, q.MaxPrice));
            rows = q.Sort switch { "name" => rows.OrderBy(p => p.Name).ThenBy(p => p.Id), "stockAsc" => rows.OrderBy(p => p.Stock).ThenBy(p => p.Id), "stockDesc" => rows.OrderByDescending(p => p.Stock).ThenBy(p => p.Id), "priceAsc" => rows.OrderBy(p=>AuthorityPrice(p)==null).ThenBy(AuthorityPrice).ThenBy(p => p.Id), "priceDesc" => rows.OrderBy(p=>AuthorityPrice(p)==null).ThenByDescending(AuthorityPrice).ThenBy(p => p.Id), "oldest" => rows.OrderBy(p => p.Id), _ => rows.OrderByDescending(p => p.Id) };
            var result=JsonSerializer.Deserialize<ListingPage<Product>>(JsonSerializer.Serialize(ListingFilters.Page(rows, q), _json), _json)!;foreach(var item in result.Items)MarketPilot.Web.Pricing.PricingProjection.Apply(_data,item);return result;
        }
    }
    public ListingPage<Order> SearchOrders(ListingQuery q)
    {
        lock (_gate)
        {
            var rows = _data.Orders.Where(o => string.IsNullOrWhiteSpace(q.Search) || ListingFilters.Text(o.Id, q.Search) || ListingFilters.Text(o.Customer, q.Search) || ListingFilters.Text(o.Product, q.Search))
                .Where(o => ListingFilters.Exact(o.Status, q.Status) && ListingFilters.Exact(o.Channel, q.Channel) && ListingFilters.Date(o.Date, q) && ListingFilters.Range(o.Total, q.MinPrice, q.MaxPrice));
            rows = q.Sort switch { "oldest" => rows.OrderBy(o => o.Date).ThenBy(o => o.Id), "priceAsc" => rows.OrderBy(o => o.Total).ThenBy(o => o.Id), "priceDesc" => rows.OrderByDescending(o => o.Total).ThenBy(o => o.Id), _ => rows.OrderByDescending(o => o.Date).ThenBy(o => o.Id) };
            return JsonSerializer.Deserialize<ListingPage<Order>>(JsonSerializer.Serialize(ListingFilters.Page(rows, q), _json), _json)!;
        }
    }
    public ListingPage<Shipment> SearchShipments(ListingQuery q)
    {
        lock(_gate)
        {
            var rows=_data.Shipments.Where(x=>string.IsNullOrWhiteSpace(q.Search)||ListingFilters.Text(x.OrderId,q.Search)||ListingFilters.Text(x.TrackingNumber,q.Search)||ListingFilters.Text(x.Carrier,q.Search))
                .Where(x=>ListingFilters.Exact(x.Status,q.Status)&&ListingFilters.Text(x.Carrier,q.Carrier)&&ListingFilters.Date(x.CreatedAt,q));
            rows=q.Sort=="oldest"?rows.OrderBy(x=>x.CreatedAt).ThenBy(x=>x.Id):rows.OrderByDescending(x=>x.CreatedAt).ThenBy(x=>x.Id);
            return JsonSerializer.Deserialize<ListingPage<Shipment>>(JsonSerializer.Serialize(ListingFilters.Page(rows,q),_json),_json)!;
        }
    }

}
public static class ListingSearchEndpoints
{
    public static void MapListingSearch(this WebApplication app)
    {
        app.MapGet("/api/shipments/search", ([AsParameters] ListingQuery query, MarketPilot.Web.Catalog.OperationsHub hub) => query.Error() is { } error ? Results.BadRequest(new { error }) : Results.Ok(hub.Shipments(query)));
        app.MapGet("/api/products/search", ([AsParameters] ListingQuery query, DemoStore db) => query.Error() is { } error ? Results.BadRequest(new { error }) : Results.Ok(db.SearchProducts(query)));
        app.MapGet("/api/orders/search", ([AsParameters] ListingQuery query, MarketPilot.Web.Catalog.OperationsHub hub) => query.Error() is { } error ? Results.BadRequest(new { error }) : Results.Ok(hub.Orders(query)));
    }
}
