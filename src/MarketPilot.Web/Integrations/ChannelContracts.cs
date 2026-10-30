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
    string? Currency,decimal? VatRate,string State,string[] ReviewReasons,int? Stock=null,bool ChannelManagedStock=false,bool? VatIncluded=null);
public sealed record CentralOrderLineProjection(string SourceLineId,string NativeSku,string Barcode,string? ErpProductCode,
    string? ErpVariantCode,int Quantity,decimal? UnitPrice,decimal? GrossAmount,decimal? VatRate,string Currency);
public sealed record CentralOrderProjection(string Channel,string TenantId,string AccountId,string SourceOrderId,
    string OrderNumber,string PackageId,string NativeStatus,string? CanonicalStatus,string Carrier,string TrackingNumber,
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
        new("trendyol","product/content/main ID + SKU + barcode","packageId","page/product nextPageToken/order nextCursor","paired dates; bounded stream windows","order line VAT; product VAT not in common surface","merchant","batch accepted != processed; poll receipt",
            ["read-products","read-orders","link-existing-listing","stock-outbox"],["create-product","infer-shipment-status","unverified-erp-write"],
            Statuses(("Created","received"),("Picking","picking"),("Invoiced","packing"),("Shipped","shipped"),("Delivered","delivered"),("Cancelled","cancelled"),("Returned","returned"),("UnDelivered","delivery-failed"))),
        new("hepsiburada","listingId/HB SKU + merchantSku + barcode","packageId or orderNumber","offset/page","paired dates <=31 days; order-number bypass","nullable order VAT; missing remains unknown","merchant","job receipt required",
            ["read-products","read-orders","link-existing-listing","stock-outbox"],["create-product","order-barcode-filter","order-stream","infer-shipment-status"],
            Statuses(("Created","received"),("Awaiting","pending-payment"),("Picking","picking"),("Shipped","shipped"),("Delivered","delivered"),("Cancelled","cancelled"),("Returned","returned"))),
        new("n11","product ID + seller SKU; barcode query unsupported","packageId or orderNumber","page","paired date filters","nullable; no default VAT","n11depom stock is channel managed","task accepted != applied",
            ["read-products","read-orders","link-existing-listing","stock-outbox"],["create-product","product-barcode-filter","infer-shipment-status"],
            Statuses(("Created","received"),("Picking","picking"),("Shipped","shipped"),("Delivered","delivered"),("Cancelled","cancelled"),("Returned","returned"))),
        new("amazon","ASIN/content + seller SKU; barcode may be missing","orderNumber; native packages retained by Amazon panel","nextToken via NextCursor","createdAfter/Before; native lag restriction","VAT/gross/discount may be unavailable; never infer","AMAZON/FBA stock not writable","feed/operation acceptance requires query",
            ["read-products","read-orders","link-existing-listing","stock-outbox"],["create-product","write-channel-managed-stock","infer-shipment-status"],
            Statuses(("PENDING_AVAILABILITY","pending"),("PENDING","pending-payment"),("UNSHIPPED","received"),("PARTIALLY_SHIPPED","partially-shipped"),("SHIPPED","shipped"),("CANCELLED","cancelled"),("UNFULFILLABLE","rejected"))),
        new("boyner","product/content ID + SKU + barcode","packageId","page","paired dates","native product VAT available only in detailed model; common product VAT unknown","merchant","operation accepted != processed",
            ["read-products","read-orders","link-existing-listing","stock-outbox"],["create-product","infer-shipment-status"],
            Statuses(("Created","received"),("Picking","picking"),("Shipped","shipped"),("Delivered","delivered"),("Cancelled","cancelled"),("Returned","returned"))),
        new("ticimax","card ID + variant ID + SKU + barcode","orderNumber; no fabricated package ID","page token page:N","SOAP date filter; native status IDs","variant VAT in detailed model; unit price may be unavailable","merchant","SOAP transport success requires operation result",
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
        return links.Length==1?links[0]:null;
    }
    public void Require(string operation)
    {if(!matrix.SupportedPreparation.Contains(operation))throw new IntegrationException("unsupported_operation",matrix.Channel+" bu ortak işlemi desteklemiyor: "+operation,501);}
    public CentralProductProjection Product(string account,MarketplaceProduct p,IEnumerable<ErpVariantLink>? links=null)
    {
        if(p==null||string.IsNullOrWhiteSpace(account))throw new ArgumentException("Ürün/hesap verisi eksik.");
        var link=Link(links??[],p.Sku,p.Barcode);var issues=new List<string>();
        if(link==null)issues.Add("ERP ürün/varyant eşlemesi eksik veya belirsiz.");
        if(string.IsNullOrWhiteSpace(p.Id))issues.Add("Mevcut ilan kimliği eksik; ürün oluşturulmaz.");
        if(string.IsNullOrWhiteSpace(p.Currency))issues.Add("Para birimi bildirilmedi.");
        if(p.SalePrice==null)issues.Add("Fiyat bildirilmedi.");
        if(p.VatRate==null||p.VatIncluded==null)issues.Add("KDV oranı/fiyat esası bildirilmedi.");
        return new(matrix.Channel,account,p.Id,p.ContentId,p.Sku,p.Barcode,link?.ErpProductCode,link?.ErpVariantCode,
            p.SalePrice,string.IsNullOrWhiteSpace(p.Currency)?null:p.Currency,p.VatRate,issues.Count==0?"publication-preparation":"review-required",issues.ToArray(),
            p.Stock,Managed(p.FulfilledBy),p.VatIncluded);
    }
    public CentralOrderProjection Order(string tenant,string account,MarketplaceOrder order,IEnumerable<ErpVariantLink>? mappings=null)
    {
        if(order?.Lines==null||order.Cargo==null||order.Lines.Any(x=>x==null)||string.IsNullOrWhiteSpace(account))
            throw new ArgumentException("Sipariş, hesap, kalem veya kargo yapısı geçersiz.");
        var issues=new List<string>();
        var status=matrix.OrderStatuses.GetValueOrDefault(order.Status);
        if(status==null)issues.Add("Kanal durumu bilinmiyor; durum yazması bloke.");
        var lines=order.Lines.Select(l=>{var link=Link(mappings??[],l.Sku,l.Barcode);
            if(link==null)issues.Add("ERP varyant eşlemesi gerekli: "+l.Id);
            if(l.VatRate==null||l.UnitPrice==null)issues.Add("KDV/fiyat bilinmiyor: "+l.Id);
            return new CentralOrderLineProjection(l.Id,l.Sku,l.Barcode,link?.ErpProductCode,link?.ErpVariantCode,l.Quantity,l.UnitPrice,l.GrossAmount,l.VatRate,l.Currency);}).ToArray();
        if(order.Total==null||order.GrossTotal==null||order.Discount==null)issues.Add("Tutar/indirim mutabakatı için eksik veri var.");
        var managed=Managed(order.FulfilledBy);
        return new(matrix.Channel,tenant,account,string.IsNullOrWhiteSpace(order.PackageId)?order.OrderNumber:order.PackageId,
            order.OrderNumber,order.PackageId,order.Status,status,order.Cargo.Provider,order.Cargo.TrackingNumber,order.FulfilledBy,
            managed,order.Currency,order.Total,order.GrossTotal,order.Discount,lines,order,issues.Distinct().ToArray(),
            order.Packages?.Select(p=>new CentralPackageProjection(p.Id,p.Carrier,p.TrackingNumber,p.NativeStatus,
                p.NativeStatus==null?null:matrix.PackageStatuses.GetValueOrDefault(p.NativeStatus),p.ShippedAt)).ToArray());
    }
    public CentralOperationReceipt Receipt(string nativeState,string? reference,bool? processingConfirmed)
    {
        if(nativeState=="completed"&&processingConfirmed==true&&!string.IsNullOrWhiteSpace(reference))return new("processed",true,true,reference);
        if(nativeState is "accepted" or "pending" && !string.IsNullOrWhiteSpace(reference))return new("waiting",true,false,reference);
        return new(nativeState=="rejected"?"rejected":"review-required",false,false,reference);
    }
    public void RequireMerchantStock(CentralProductProjection product)
    {Require("stock-outbox");if(product.ChannelManagedStock)throw new IntegrationException("channel_managed_stock","Kanal tarafından yönetilen stok yazılamaz.",409);}
    public ChannelContractError Error(IntegrationException error)=>new(matrix.Channel,error.Code,error.Status,
        error.Status==429||error.Status>=500,error.Status is 400 or 401 or 403 or 409||error.Status==501);
    private bool Managed(string? mode)=>matrix.Channel=="amazon" && mode is "AMAZON" or "AFN" or "FBA" ||
        matrix.Channel=="n11" && (mode=="N11" || mode?.Contains("depom",StringComparison.OrdinalIgnoreCase)==true);
    public async Task<MarketplacePage<CentralOrderProjection>> ReadOrdersAsync(IMarketplaceAdapter adapter,ConnectionSettings settings,
        OrderQuery query,string tenant,string accountId,ErpVariantLink[] links,CancellationToken ct)
    {
        Require("read-orders");
        if(adapter.Marketplace!=matrix.Channel||settings.Marketplace!=matrix.Channel)throw new ArgumentException("Kanal adaptörü uyuşmuyor.");
        var page=await adapter.OrdersAsync(settings,query,ct);
        return new(page.Items.Select(x=>Order(tenant,accountId,x,links)).ToArray(),page.Page,page.Size,
            page.TotalElements,page.TotalPages,page.NextPageToken,page.HasMore,page.NextCursor);
    }
}
