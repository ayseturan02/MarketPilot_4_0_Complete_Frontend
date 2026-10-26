namespace MarketPilot.Web.Integrations;

public record MarketplaceAddress(string Id, string Type, string Country, string City, string District, string PostCode, string FullAddress, bool IsDefault);
public record MarketplaceAccount(string Marketplace, string SellerId, string Environment, string StoreFrontCode, IReadOnlyList<MarketplaceAddress> Addresses, DateTimeOffset ReadAt);
public record MarketplaceProduct(
    string Id, string ContentId, string ProductMainId, string Sku, string Barcode, string Name,
    string CategoryId, string CategoryName, string BrandName, decimal? ListPrice, decimal? SalePrice,
    decimal? CustomerPrice, int? Stock, string Currency, bool? OnSale, string ProductUrl, IReadOnlyList<string> Images,
    string ApprovalStatus, IReadOnlyList<string> RejectReasons);
public record MarketplaceOrderLine(
    string Id, string Sku, string Barcode, string Name, int Quantity, decimal? UnitPrice,
    decimal? GrossAmount, decimal? VatRate, string Currency, string Status, string CategoryId);
public record MarketplaceCargo(string Provider, string TrackingNumber, string TrackingLink, string SenderNumber, string ShipmentNumber, decimal? Deci, int? BoxQuantity);
public record MarketplaceInvoice(string Number, string Status, string Link, bool Commercial, string Company, string TaxOffice, string TaxNumber, bool? EInvoiceAvailable, IReadOnlyList<string> RejectedReasons);
public record MarketplaceOrder(
    string PackageId, string OrderNumber, string Status, DateTimeOffset? OrderDate, string CustomerName,
    string CustomerEmail, decimal? Total, decimal? GrossTotal, decimal? Discount, string Currency,
    string PaymentMethod, string DeliveryAddressType, MarketplaceAddress? ShipmentAddress,
    IReadOnlyList<MarketplaceOrderLine> Lines, MarketplaceCargo Cargo, MarketplaceInvoice Invoice);
public record MarketplacePage<T>(IReadOnlyList<T> Items, int Page, int Size, long? TotalElements, int? TotalPages, string? NextPageToken, bool? HasMore = null, string? NextCursor = null);
public record ProductQuery(int Page = 0, int Size = 50, string? Barcode = null, string? StockCode = null, string? Status = null, string? NextPageToken = null, string Approval = "approved");
public record OrderQuery(int Page = 0, int Size = 50, string? Status = null, string? OrderNumber = null, string? Barcode = null, long? StartDate = null, long? EndDate = null, string OrderByDirection = "DESC", bool Stream = false, string? NextCursor = null);
public record StockPriceDraft(string Barcode, int Stock, decimal Price);
public record UpdatePreview(string Marketplace, bool Sent, IReadOnlyList<StockPriceDraft> Items);
public record ConnectionSettings(string Name, string Marketplace, string Mode, string Environment);
public record ConnectionState(string Id, ConnectionSettings Settings, string Status, DateTimeOffset? LastOperation, string? LastAction, string? ErrorCode, string? Message, int? LastCount, string? SellerId = null);

public interface IMarketplaceAdapter
{
    string Marketplace { get; }
    Task<MarketplaceAccount> TestAsync(ConnectionSettings settings, CancellationToken ct);
    Task<MarketplaceAccount> AccountAsync(ConnectionSettings settings, CancellationToken ct);
    Task<MarketplacePage<MarketplaceProduct>> ProductsAsync(ConnectionSettings settings, ProductQuery query, CancellationToken ct);
    Task<MarketplacePage<MarketplaceOrder>> OrdersAsync(ConnectionSettings settings, OrderQuery query, CancellationToken ct);
}

public sealed class IntegrationException(string code, string message, int status = 502) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public abstract class PlannedAdapter(string marketplace) : IMarketplaceAdapter
{
    public string Marketplace => marketplace;
    private static IntegrationException Unavailable() => new("adapter_not_ready", "Bu pazaryerinin gerçek API adaptörü henüz hazır değil.", 501);
    public Task<MarketplaceAccount> TestAsync(ConnectionSettings s, CancellationToken ct) => throw Unavailable();
    public Task<MarketplaceAccount> AccountAsync(ConnectionSettings s, CancellationToken ct) => throw Unavailable();
    public Task<MarketplacePage<MarketplaceProduct>> ProductsAsync(ConnectionSettings s, ProductQuery q, CancellationToken ct) => throw Unavailable();
    public Task<MarketplacePage<MarketplaceOrder>> OrdersAsync(ConnectionSettings s, OrderQuery q, CancellationToken ct) => throw Unavailable();
}
public sealed class HepsiburadaAdapter() : PlannedAdapter("hepsiburada");
public sealed class N11Adapter() : PlannedAdapter("n11");
public sealed class AmazonSpApiAdapter() : PlannedAdapter("amazon");

public sealed class DemoMarketplaceAdapter : IMarketplaceAdapter
{
    public string Marketplace => "demo";
    private static MarketplaceAccount Account(ConnectionSettings s) => new(s.Marketplace, "DEMO-2738", s.Environment, "TR",
        [new("demo-address", "Shipment", "Türkiye", "İstanbul", "Kadıköy", "34710", "Demo sevkiyat adresi", true)], DateTimeOffset.UtcNow);
    public Task<MarketplaceAccount> TestAsync(ConnectionSettings s, CancellationToken ct) => Task.FromResult(Account(s));
    public Task<MarketplaceAccount> AccountAsync(ConnectionSettings s, CancellationToken ct) => Task.FromResult(Account(s));
    public Task<MarketplacePage<MarketplaceProduct>> ProductsAsync(ConnectionSettings s, ProductQuery q, CancellationToken ct)
    {
        var all = new[]
        {
            new MarketplaceProduct("701", "501", "DEMO-MAIN-1", "DEMO-001", "8690000000001", "Kablosuz Demo Kulaklık", "1001", "Elektronik", "MarketPilot", 2299m, 1999m, 1899m, 12, "TRY", true, "https://www.trendyol.com/", ["https://cdn.dsmcdn.com/web/logo/ty-web.svg"], "approved", []),
            new MarketplaceProduct("702", "502", "DEMO-MAIN-2", "DEMO-002", "8690000000002", "Demo Seramik Vazo", "1002", "Ev ve Yaşam", "MarketPilot", 849m, 749m, 749m, 4, "TRY", true, "https://www.trendyol.com/", [], "approved", [])
        };
        var source = q.Approval == "unapproved" ? [new MarketplaceProduct("703", "", "DEMO-DRAFT", "DEMO-DRAFT-1", "8690000000003", "Onay bekleyen demo ürün", "1001", "Elektronik", "MarketPilot", 500m, 450m, null, 2, "TRY", false, "", [], "pendingApproval", [])] : all;
        var filtered = source.Where(x => string.IsNullOrWhiteSpace(q.Barcode) || x.Barcode.Contains(q.Barcode, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(q.StockCode) || x.Sku.Contains(q.StockCode, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(q.Status) || x.ApprovalStatus == q.Status || q.Status == "onSale" && x.OnSale == true || q.Status == "notOnSale" && x.OnSale == false).ToArray();
        var items = filtered.Skip(q.Page * q.Size).Take(q.Size).ToArray();
        return Task.FromResult(new MarketplacePage<MarketplaceProduct>(items, q.Page, q.Size, filtered.Length, (int)Math.Ceiling((double)filtered.Length / q.Size), null));
    }
    public Task<MarketplacePage<MarketplaceOrder>> OrdersAsync(ConnectionSettings s, OrderQuery q, CancellationToken ct)
    {
        var order = new MarketplaceOrder("92087137", "DEMO-1001", "Created", DateTimeOffset.UtcNow.AddHours(-2), "Demo Müşteri", "demo@example.com", 1999m, 2299m, 300m, "TRY", "Kredi Kartı", "Shipment",
            new("shipment", "Shipment", "Türkiye", "İstanbul", "Kadıköy", "34710", "Demo teslimat adresi", false),
            [new("line-1", "DEMO-001", "8690000000001", "Kablosuz Demo Kulaklık", 1, 1999m, 2299m, 20m, "TRY", "Created", "1001")],
            new("Trendyol Express", "7280027504000001", "https://tracking.trendyol.com/", "210090000001", "606404425", 4.4m, 1),
            new("FTR-DEMO-1", "Invoiced", "https://www.trendyol.com/", false, "", "", "", false, []));
        var matches = (string.IsNullOrWhiteSpace(q.Status) || order.Status == q.Status) &&
            (string.IsNullOrWhiteSpace(q.OrderNumber) || order.OrderNumber.Contains(q.OrderNumber, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(q.Barcode) || order.Lines.Any(x => x.Barcode.Contains(q.Barcode, StringComparison.OrdinalIgnoreCase)));
        var items = matches && q.Page == 0 ? new[] { order } : [];
        return Task.FromResult(new MarketplacePage<MarketplaceOrder>(items, q.Page, q.Size, q.Stream ? null : matches ? 1 : 0, q.Stream ? null : matches ? 1 : 0, null, q.Stream ? false : null, null));
    }
}
