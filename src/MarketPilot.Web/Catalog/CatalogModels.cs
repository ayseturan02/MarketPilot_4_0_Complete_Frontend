using MarketPilot.Web.Integrations;
namespace MarketPilot.Web.Catalog;

public sealed class CatalogState
{
    public Dictionary<string,ObservedOrder> Orders {get;set;}=new();
    public long OrderRevision{get;set;}
    public int Version {get;set;}
    public Dictionary<string,CatalogListing> Listings {get;set;}=new();
    public List<CatalogJob> Jobs {get;set;}=new();
    public Dictionary<string,CategoryBinding> Categories {get;set;}=new();
}
public sealed record CatalogListing(string Key,string ProductId,string StoreId,string Source,string ExternalId,string Sku,string Barcode,string Name,string Category,string Brand,decimal? Stock,decimal? Price,string Currency,bool? VatIncluded,string Status,DateTimeOffset ObservedAt);
public sealed record CategoryBinding(string Category,string StoreId,string ExternalCategoryId,string ExternalCategoryName,string BrandId,Dictionary<string,string> Attributes);
public sealed class CatalogJob
{
    public string Id{get;set;}=Guid.NewGuid().ToString("N");public string RequestId{get;set;}="";public string Fingerprint{get;set;}="";public string State{get;set;}="pending";public DateTimeOffset CreatedAt{get;set;}=DateTimeOffset.UtcNow;public List<CatalogJobTarget> Targets{get;set;}=new();
}
public sealed class CatalogJobTarget
{
    public string Key{get;set;}="";public string StoreId{get;set;}="";public string Source{get;set;}="";public string ProductId{get;set;}="";public int Version{get;set;}public string Barcode{get;set;}="";public decimal Stock{get;set;}public decimal Price{get;set;}public string State{get;set;}="pending";public string? DemoJobId{get;set;}public string? Error{get;set;}
}
public sealed record CatalogQuery(string? Search=null,string? StoreId=null,string? Status=null,string? Category=null,string? Mode=null,string? Mapped=null,int Page=0,int Size=25)
{
    public string? Error()=>Page<0||Size is <1 or >100||(long)Page*Size>int.MaxValue||new[]{Search,StoreId,Status,Category,Mode,Mapped}.Any(v=>v?.Length>120)?"Geçersiz filtre veya sayfa bilgisi.":null;
}
public sealed record CatalogProductRow(Product Product,IReadOnlyList<CatalogListing> Listings,int ListingCount);
public sealed record CatalogEdit(string Name,string Sku,string Barcode,string Category,string Brand,string Description,decimal? Stock,decimal? Price,string Currency,bool? VatIncluded,string Status,int Version);
public sealed record SourceQuery(string? Search=null,string SearchType="SKU",string Approval="approved",int Page=0,string? Token=null);
public sealed record CatalogCandidate(string Key,string ExternalId,string Sku,string Barcode,string Name,string Category,string Brand,decimal? Stock,decimal? Price,string Currency,bool? VatIncluded,string Status,IReadOnlyList<string>? Images=null,Dictionary<string,string>? Attributes=null,string? Description=null);
public sealed record CandidateBatch(string Token,string StoreId,string Source,DateTimeOffset ObservedAt,CatalogCandidate[] Rows,int NativePage,string? NextToken,bool HasMore);
public sealed record CatalogImport(string Token,string[] Keys);
public sealed record CatalogRemap(string ListingKey,string ProductId);
public sealed record CatalogPublish(string RequestId,string[] Keys,Dictionary<string,int> Versions);
public sealed record PublishLine(string Key,string ProductId,int Version,string StoreId,string ProductName,decimal? CurrentStock,decimal? CurrentPrice,decimal? TargetStock,decimal? TargetPrice,bool Ready,string? Reason);
