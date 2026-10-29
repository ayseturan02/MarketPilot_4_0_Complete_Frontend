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
    IReadOnlyList<MarketplaceOrderLine> Lines, MarketplaceCargo Cargo, MarketplaceInvoice Invoice,long? LastModifiedDate=null,string? FulfilledBy=null);
public record MarketplacePage<T>(IReadOnlyList<T> Items, int Page, int Size, long? TotalElements, int? TotalPages, string? NextPageToken, bool? HasMore = null, string? NextCursor = null);
public record ProductQuery(int Page = 0, int Size = 50, string? Barcode = null, string? StockCode = null, string? Status = null, string? NextPageToken = null, string Approval = "approved");
public record OrderQuery(int Page = 0, int Size = 50, string? Status = null, string? OrderNumber = null, string? Barcode = null, long? StartDate = null, long? EndDate = null, string OrderByDirection = "DESC", bool Stream = false, string? NextCursor = null);
public record StockPriceDraft(string Barcode, int Stock, decimal Price);
public record UpdatePreview(string Marketplace, bool Sent, IReadOnlyList<StockPriceDraft> Items);
public record ConnectionSettings(string Name, string Marketplace, string Mode, string Environment, string? CredentialProfile = null);
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
