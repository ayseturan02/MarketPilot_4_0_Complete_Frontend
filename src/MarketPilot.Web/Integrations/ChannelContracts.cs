using MarketPilot.Web.Enterprise;

namespace MarketPilot.Web.Integrations;

public sealed record ChannelCapabilityMatrix(string Channel,string ProductIdentity,string OrderIdentity,
    string Pagination,string DateFilter,string TaxInformation,string ManagedStock,
    string AsyncAcceptance,string[] SupportedPreparation,string[] UnsupportedOperations,
    IReadOnlyDictionary<string,string> OrderStatuses)
{
    public IReadOnlyDictionary<string,string> PackageStatuses=>ChannelContracts.PackageStatusesFor(Channel);
}
public sealed record CentralProductProjection(string Channel,string AccountId,string ListingId,string ContentId,
    string NativeSku,string Barcode,string? ErpProductCode,string? ErpVariantCode,decimal? Price,
    string? Currency,decimal? VatRate,string State,string[] ReviewReasons,int? Stock=null,bool ChannelManagedStock=false,bool? VatIncluded=null,
    string? TenantId=null,string? ProductMainId=null,decimal? ListPrice=null);
public sealed record CentralOrderLineProjection(string SourceLineId,string NativeSku,string Barcode,string? ErpProductCode,
    string? ErpVariantCode,int Quantity,decimal? UnitPrice,decimal? GrossAmount,decimal? VatRate,string Currency);
public sealed record CentralOrderProjection(string Channel,string TenantId,string AccountId,string SourceOrderId,
    string OrderNumber,string PackageId,string? NativeStatus,string? CanonicalStatus,string Carrier,string TrackingNumber,
    string? FulfilledBy,bool ChannelManagedStock,string Currency,decimal? Total,decimal? GrossTotal,decimal? Discount,
    CentralOrderLineProjection[] Lines,MarketplaceOrder Original,string[] ReviewReasons,CentralPackageProjection[]? Packages=null);
public sealed record CentralPackageProjection(string Id,string Carrier,string TrackingNumber,string? NativeStatus,string? CanonicalStatus,DateTimeOffset? ShippedAt);
public sealed record CentralOperationReceipt(string State,bool Accepted,bool Processed,string? Reference);
public sealed record ChannelContractError(string Channel,string NativeCode,int HttpStatus,bool Retryable,bool RequiresReview);
public sealed record ChannelProductPreparation(string AccountId,MarketplaceProduct Product);
public sealed record ChannelOrderPreparation(string AccountId,MarketplaceOrder Order);

// Capabilities describe the CURRENT common adapter surface, not hypothetical native APIs.
public static class ChannelContracts
{
    public static IReadOnlyDictionary<string,string> PackageStatusesFor(string channel)=>channel switch
    {
        "amazon"=>Statuses(("SHIPPED","shipped"),("DELIVERED","delivered")),
        "trendyol" or "boyner" or "n11"=>Statuses(("Created","received"),("Picking","picking"),("Shipped","shipped"),("Delivered","delivered"),("Cancelled","cancelled"),("Returned","returned")),
        "hepsiburada"=>Statuses(("Open","received"),("Packaged","packed"),("InTransit","shipped")),
        _=>new Dictionary<string,string>()
    };
    private static Dictionary<string,string> Statuses(params (string Native,string Central)[] entries)=>entries.ToDictionary(x=>x.Native,x=>x.Central,StringComparer.Ordinal);
    public static readonly ChannelCapabilityMatrix[] All =
    [
        new("trendyol","product/content/main ID + SKU + barcode","packageId","page/product nextPageToken/order nextCursor","paired dates; bounded stream windows","nullable product/line VAT; unknown preserved","merchant","batch accepted != processed; poll receipt",
            ["read-products","read-orders","link-existing-listing","stock-outbox"],["create-product","infer-shipment-status","unverified-erp-write"],
            Statuses(("Created","received"),("Picking","picking"),("Invoiced","packing"),("Shipped","shipped"),("Delivered","delivered"),("Cancelled","cancelled"),("Returned","returned"),("UnDelivered","delivery-failed"))),
        new("hepsiburada","listingId/HB SKU + merchantSku + barcode","order line ID; packageNumber is separate","offset/page; packages <=10 rows, totals in headers","orders <=31 days; packages <=24h; order-number bypass","nullable order VAT; missing remains unknown","merchant","job receipt required",
            ["read-products","read-orders","link-existing-listing","stock-outbox"],["create-product","order-barcode-filter","order-stream","infer-shipment-status"],
            Statuses(("Created","received"),("Awaiting","pending-payment"),("Picking","picking"),("Shipped","shipped"),("Delivered","delivered"),("Cancelled","cancelled"),("Returned","returned"))),
        new("n11","product ID + seller SKU; barcode query unsupported","packageId or orderNumber","page","paired date filters","nullable; no default VAT","n11depom stock is channel managed","task accepted != applied",
            ["read-products","read-orders","link-existing-listing","stock-outbox"],["create-product","product-barcode-filter","infer-shipment-status"],
            Statuses(("Created","received"),("Picking","picking"),("Shipped","shipped"),("Delivered","delivered"),("Cancelled","cancelled"),("Returned","returned"))),
        new("amazon","ASIN/content + seller SKU; barcode may be missing","orderNumber; native packages retained by Amazon panel","nextToken via NextCursor","createdAfter/Before; native lag restriction","VAT/gross/discount may be unavailable; never infer","AMAZON/FBA stock not writable","feed/operation acceptance requires query",
            ["read-products","read-orders","link-existing-listing","stock-outbox"],["create-product","write-channel-managed-stock","infer-shipment-status"],
            Statuses(("PENDING_AVAILABILITY","pending"),("PENDING","pending-payment"),("UNSHIPPED","received"),("PARTIALLY_SHIPPED","partially-shipped"),("SHIPPED","shipped"),("CANCELLED","cancelled"),("UNFULFILLABLE","rejected"))),
        new("boyner","product/content ID + SKU + barcode","packageId","page","paired dates","nullable product/line VAT; detailed native fields retained","merchant","operation accepted != processed",
            ["read-products","read-orders","link-existing-listing","stock-outbox"],["create-product","infer-shipment-status"],
            Statuses(("Created","received"),("Picking","picking"),("Shipped","shipped"),("Delivered","delivered"),("Cancelled","cancelled"),("Returned","returned"))),
        new("ticimax","card ID + variant ID + SKU + barcode","orderNumber; no fabricated package ID","page token page:N","SOAP date filter; native status IDs","nullable variant VAT; unit price may be unavailable","merchant","SOAP transport success requires operation result",
            ["read-products","read-orders","link-existing-listing","stock-outbox"],["create-product","infer-shipment-status","invent-currency"],
            Statuses(("Yeni","received"),("Hazırlanıyor","picking"),("Kargoya Verildi","shipped"),("Teslim Edildi","delivered"),("İptal","cancelled"),("İade","returned")))
    ];
    public static ChannelContractMapper For(string channel)=>new(All.SingleOrDefault(x=>x.Channel==channel)??
        throw new IntegrationException("unsupported_marketplace","Kanalın ortak sözleşmesi yok.",501));
}
public sealed class ChannelContractMapper(ChannelCapabilityMatrix matrix)
{
    public ChannelCapabilityMatrix Capabilities=>matrix;
    private static ErpVariantLink? Link(IEnumerable<ErpVariantLink> mappings,string sku,string barcode)
    {
        if(string.IsNullOrWhiteSpace(sku))return null;
        var links=mappings.Where(x=>x.Sku==sku && !string.IsNullOrWhiteSpace(barcode) && x.Barcode==barcode).ToArray();
        return links.Length==1 && !string.IsNullOrWhiteSpace(links[0].ErpProductCode) && !string.IsNullOrWhiteSpace(links[0].ErpVariantCode)?links[0]:null;
    }
    public void Require(string operation)
    {if(!matrix.SupportedPreparation.Contains(operation))throw new IntegrationException("unsupported_operation",matrix.Channel+" bu ortak işlemi desteklemiyor: "+operation,501);}
    public CentralProductProjection Product(string account,MarketplaceProduct p,IEnumerable<ErpVariantLink>? links=null,string? tenant=null)
    {
        if(p==null||string.IsNullOrWhiteSpace(account))throw new ArgumentException("Ürün/hesap verisi eksik.");
        var link=Link(links??[],p.Sku,p.Barcode);var issues=new List<string>();
        if(link==null)issues.Add("ERP ürün/varyant eşlemesi eksik veya belirsiz.");
        if(string.IsNullOrWhiteSpace(p.Id))issues.Add("Mevcut ilan kimliği eksik; ürün oluşturulmaz.");
        if(string.IsNullOrWhiteSpace(p.Currency))issues.Add("Para birimi bildirilmedi.");
        if(p.SalePrice==null)issues.Add("Fiyat bildirilmedi.");
        if(p.VatRate==null||p.VatIncluded==null)issues.Add("KDV oranı/fiyat esası bildirilmedi.");
        if(p.SalePrice<0 || p.ListPrice<0 || p.VatRate is <0 or >100 || p.Stock<0)
            issues.Add("Fiyat, KDV oranı veya stok değeri geçersiz; yayın hazırlığı bloke.");
        return new(matrix.Channel,account,p.Id,p.ContentId,p.Sku,p.Barcode,link?.ErpProductCode,link?.ErpVariantCode,
            p.SalePrice,string.IsNullOrWhiteSpace(p.Currency)?null:p.Currency,p.VatRate,issues.Count==0?"publication-preparation":"review-required",issues.ToArray(),
            p.Stock,Managed(p.FulfilledBy),p.VatIncluded,tenant,p.ProductMainId,p.ListPrice);
    }
    public CentralOrderProjection Order(string tenant,string account,MarketplaceOrder order,IEnumerable<ErpVariantLink>? mappings=null)
    {
        if(order?.Lines==null||order.Cargo==null||order.Lines.Any(x=>x==null)||string.IsNullOrWhiteSpace(account)||string.IsNullOrWhiteSpace(tenant))
            throw new ArgumentException("Sipariş, hesap, kalem veya kargo yapısı geçersiz.");
        var issues=new List<string>();
        var status=order.Status is null?null:matrix.OrderStatuses.GetValueOrDefault(order.Status);
        if(status==null)issues.Add("Kanal durumu bilinmiyor; durum yazması bloke.");
        var lines=order.Lines.Select(l=>{var link=Link(mappings??[],l.Sku,l.Barcode);
            if(link==null)issues.Add("ERP varyant eşlemesi gerekli: "+l.Id);
            if(l.VatRate==null||l.UnitPrice==null)issues.Add("KDV/fiyat bilinmiyor: "+l.Id);
            if(string.IsNullOrWhiteSpace(l.Id)||l.Quantity<=0||l.UnitPrice<0||l.GrossAmount<0||l.VatRate is <0 or >100||l.Currency!=order.Currency)
                issues.Add("Kalem kimliği, miktarı, tutarı, KDV veya para birimi geçersiz: "+l.Id);
            return new CentralOrderLineProjection(l.Id,l.Sku,l.Barcode,link?.ErpProductCode,link?.ErpVariantCode,l.Quantity,l.UnitPrice,l.GrossAmount,l.VatRate,l.Currency);}).ToArray();
        if(order.Total==null||order.GrossTotal==null||order.Discount==null)issues.Add("Tutar/indirim mutabakatı için eksik veri var.");
        if(order.Lines.Count==0||order.Lines.Select(line=>line.Id).Distinct(StringComparer.Ordinal).Count()!=order.Lines.Count)
            issues.Add("Sipariş kalemleri eksik veya kalem kimliği tekrarlı.");
        if(string.IsNullOrWhiteSpace(order.Currency)||order.Total<0||order.GrossTotal<0||order.Discount<0)
            issues.Add("Sipariş para birimi veya tutarı geçersiz.");
        var packages=order.Packages?.Select(package=>
        {
            if(package is null)throw new ArgumentException("Paket kaydı eksik.");
            var packageStatus=package.NativeStatus==null?null:matrix.PackageStatuses.GetValueOrDefault(package.NativeStatus);
            if(string.IsNullOrWhiteSpace(package.Id)||packageStatus==null)issues.Add("Paket kimliği veya kanal durumu doğrulanamadı.");
            return new CentralPackageProjection(package.Id,package.Carrier,package.TrackingNumber,package.NativeStatus,packageStatus,package.ShippedAt);
        }).ToArray();
        var managed=Managed(order.FulfilledBy);
        return new(matrix.Channel,tenant,account,string.IsNullOrWhiteSpace(order.PackageId)?order.OrderNumber:order.PackageId,
            order.OrderNumber,order.PackageId,order.Status,status,order.Cargo.Provider,order.Cargo.TrackingNumber,order.FulfilledBy,
            managed,order.Currency,order.Total,order.GrossTotal,order.Discount,lines,order,issues.Distinct().ToArray(),
            packages);
    }
    public CentralOperationReceipt Receipt(string nativeState,string? reference,bool? processingConfirmed)
    {
        if(nativeState=="completed"&&processingConfirmed==true&&!string.IsNullOrWhiteSpace(reference))return new("processed",true,true,reference);
        if(nativeState is "accepted" or "pending" && !string.IsNullOrWhiteSpace(reference))return new("waiting",true,false,reference);
        return new(nativeState=="rejected"?"rejected":"review-required",false,false,reference);
    }
    public void RequireMerchantStock(CentralProductProjection product)
    {Require("stock-outbox");if(product.Channel!=matrix.Channel)throw new ArgumentException("Ürün projeksiyonu farklı kanala ait.");if(product.ChannelManagedStock)throw new IntegrationException("channel_managed_stock","Kanal tarafından yönetilen stok yazılamaz.",409);}
    public ChannelContractError Error(IntegrationException error)=>new(matrix.Channel,error.Code,error.Status,
        error.Status is 408 or 429 or 500 or 502 or 503 or 504,error.Status is 400 or 401 or 403 or 404 or 409 or 422 or 501);
    private bool Managed(string? mode)
    {
        var normalized=mode?.Trim().ToUpperInvariant();
        return matrix.Channel=="amazon" && normalized is "AMAZON" or "AFN" or "FBA" ||
            matrix.Channel=="n11" && (normalized=="N11" || normalized?.Contains("DEPO",StringComparison.Ordinal)==true);
    }
    public async Task<MarketplacePage<CentralProductProjection>> ReadProductsAsync(IMarketplaceAdapter adapter,ConnectionSettings settings,
        ProductQuery query,string tenant,string accountId,ErpVariantLink[] links,CancellationToken ct)
    {
        Require("read-products");
        if(adapter.Marketplace!=matrix.Channel||settings.Marketplace!=matrix.Channel||string.IsNullOrWhiteSpace(tenant))
            throw new ArgumentException("Kanal adaptörü veya işletme kimliği uyuşmuyor.");
        var page=await adapter.ProductsAsync(settings,query,ct);
        return new(page.Items.Select(product=>Product(accountId,product,links,tenant)).ToArray(),page.Page,page.Size,
            page.TotalElements,page.TotalPages,page.NextPageToken,page.HasMore,page.NextCursor);
    }
    public async Task<MarketplacePage<CentralOrderProjection>> ReadOrdersAsync(IMarketplaceAdapter adapter,ConnectionSettings settings,
        OrderQuery query,string tenant,string accountId,ErpVariantLink[] links,CancellationToken ct)
    {
        Require("read-orders");
        if(adapter.Marketplace!=matrix.Channel||settings.Marketplace!=matrix.Channel||string.IsNullOrWhiteSpace(tenant)||string.IsNullOrWhiteSpace(accountId))throw new ArgumentException("Kanal adaptörü veya işletme/hesap kimliği uyuşmuyor.");
        var page=await adapter.OrdersAsync(settings,query,ct);
        return new(page.Items.Select(x=>Order(tenant,accountId,x,links)).ToArray(),page.Page,page.Size,
            page.TotalElements,page.TotalPages,page.NextPageToken,page.HasMore,page.NextCursor);
    }
}
