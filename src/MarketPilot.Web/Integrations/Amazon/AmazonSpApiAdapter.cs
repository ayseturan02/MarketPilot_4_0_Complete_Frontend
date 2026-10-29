using System.Globalization;
using System.Text.Json;

namespace MarketPilot.Web.Integrations.Amazon;

public record AmazonIssue(string Code,string Severity,string Message);
public record AmazonListing(MarketplaceProduct Product,string Asin,string FnSku,string ProductType,string[] Statuses,AmazonIssue[] Issues,string Description,Dictionary<string,string> Attributes,string FulfilledBy);
public record AmazonPackage(string Id,string Carrier,string TrackingNumber,string Status,DateTimeOffset? ShipTime);
public record AmazonOrder(MarketplaceOrder Order,string FulfilledBy,string NativeStatus,DateTimeOffset? ShipBy,AmazonPackage[] Packages);
public record AmazonPage<T>(IReadOnlyList<T> Items,string? NextToken=null,long? Total=null);
public record AmazonQuery(string? Search=null,string IdentifierType="SKU",string? Status=null,string? FulfilledBy=null,string? Token=null,long? StartDate=null,long? EndDate=null,string? ReportType=null)
{
    public string? Error()
    {
        if(new[]{Search,Status,ReportType}.Any(v=>v?.Length>120)||Token?.Length>8192)return "Filtre değeri çok uzun.";
        if(StartDate.HasValue!=EndDate.HasValue||StartDate<0||EndDate>253402300799999||StartDate>EndDate||EndDate-StartDate>TimeSpan.FromDays(180).TotalMilliseconds)return "Tarihleri birlikte seçin; aralık en fazla 180 gün olabilir.";
        if(!new[]{"SKU","ASIN","EAN","GTIN","FNSKU","UPC"}.Contains(IdentifierType))return "Geçersiz ürün kimliği türü.";
        if(!string.IsNullOrEmpty(FulfilledBy)&&FulfilledBy is not ("MERCHANT" or "AMAZON"))return "Geçersiz gönderim türü.";
        return null;
    }
}
public sealed class AmazonSpApiAdapter(AmazonApiClient client) : IMarketplaceAdapter
{
    public string Marketplace=>"amazon";
    private static string Iso(long ms)=>DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O",CultureInfo.InvariantCulture);
    private static Dictionary<string,string> Market()=>new(){["marketplaceIds"]=AmazonApiClient.MarketplaceId};
    public async Task<MarketplaceAccount> TestAsync(ConnectionSettings s,CancellationToken ct)
    {
        using var doc=await client.Get(s.Environment,"/sellers/v1/marketplaceParticipations",new(),ct);
        var participation=Aj.A(doc.RootElement,"payload").FirstOrDefault(e=>Aj.S(Aj.F(e,"marketplace"),"id")==AmazonApiClient.MarketplaceId);
        if(Aj.F(Aj.F(participation,"participation"),"isParticipating").ValueKind!=JsonValueKind.True)throw new IntegrationException("marketplace_inactive","Amazon Türkiye mağazası için aktif satış yetkisi doğrulanamadı.",409);
        return new("amazon",client.Seller(s.Environment),s.Environment,"TR",[],DateTimeOffset.UtcNow);
    }
    public Task<MarketplaceAccount> AccountAsync(ConnectionSettings s,CancellationToken ct)=>TestAsync(s,ct);
    public async Task<AmazonPage<AmazonListing>> Listings(ConnectionSettings s,AmazonQuery q,CancellationToken ct)
    {
        var query=Market();query["includedData"]="summaries,attributes,issues,offers,fulfillmentAvailability,relationships";query["pageSize"]="20";
        if(!string.IsNullOrWhiteSpace(q.Search)){query["identifiers"]=q.Search.Trim();query["identifiersType"]=q.IdentifierType;}
        if(q.Status is "BUYABLE" or "DISCOVERABLE")query["withStatus"]=q.Status;
        else if(q.Status is "ERROR" or "WARNING")query["withIssueSeverity"]=q.Status;
        else if(!string.IsNullOrWhiteSpace(q.Status))throw new IntegrationException("invalid_filter","Geçersiz ilan durumu.",400);
        if(!string.IsNullOrWhiteSpace(q.FulfilledBy))throw new IntegrationException("unsupported_filter","İlan listesinde gönderim türüyle filtreleme desteklenmiyor. FBA stoklarını ayrı bölümden açın.",400);
        if(q.StartDate.HasValue){query["lastUpdatedAfter"]=Iso(q.StartDate.Value);query["lastUpdatedBefore"]=Iso(q.EndDate!.Value);}
        if(!string.IsNullOrEmpty(q.Token))query["pageToken"]=q.Token;
        using var doc=await client.Get(s.Environment,"/listings/2021-08-01/items/"+Uri.EscapeDataString(client.Seller(s.Environment)),query,ct);
        var root=doc.RootElement;if(Aj.F(root,"items").ValueKind!=JsonValueKind.Array)throw new IntegrationException("invalid_response","Amazon ilan listesi okunamadı.");
        return new(Aj.A(root,"items").Select(MapListing).ToArray(),Empty(Aj.S(Aj.F(root,"pagination"),"nextToken")),(long?)Aj.N(root,"numberOfResults"));
    }
    public static AmazonListing MapListing(JsonElement e)
    {
        var summary=Aj.A(e,"summaries").FirstOrDefault(x=>Aj.S(x,"marketplaceId")==AmazonApiClient.MarketplaceId);
        var attributes=Aj.F(e,"attributes");
        string Attribute(string key)=>Aj.S(Aj.A(attributes,key).FirstOrDefault(),"value");
        var id=Aj.S(e,"sku");var asin=Aj.S(summary,"asin");var statuses=Aj.A(summary,"status").Select(v=>v.ToString()).ToArray();
        var availability=Aj.A(e,"fulfillmentAvailability");var merchant=availability.FirstOrDefault(x=>Aj.S(x,"fulfillmentChannelCode")=="DEFAULT");
        var offers=Aj.A(e,"offers");var offer=offers.FirstOrDefault(x=>Aj.S(x,"marketplaceId")==AmazonApiClient.MarketplaceId&&Aj.S(x,"offerType")=="B2C");
        var price=Aj.F(offer,"price");var issues=Aj.A(e,"issues").Select(x=>new AmazonIssue(Aj.S(x,"code"),Aj.S(x,"severity"),Aj.S(x,"message"))).ToArray();
        var image=Aj.S(Aj.F(summary,"mainImage"),"link");var barcode=Attribute("externally_assigned_product_identifier");
        var attrs=new Dictionary<string,string>();foreach(var (key,label) in new[]{("color","Renk"),("size","Beden"),("material","Malzeme"),("brand","Marka")}){var value=Attribute(key);if(value.Length>0)attrs[label]=value;}
        var stock=Aj.N(merchant,"quantity");
        return new(new(id,asin,"",id,barcode,Aj.S(summary,"itemName"),"",Aj.S(summary,"productType"),Attribute("brand"),null,Aj.Money(price),null,stock.HasValue?checked((int)stock):null,Aj.S(price,"currencyCode") is {Length:>0} currency?currency:"TRY",statuses.Contains("BUYABLE"),asin.Length>0?"https://www.amazon.com.tr/dp/"+Uri.EscapeDataString(asin):"",image.Length>0?[image]:[],statuses.Contains("BUYABLE")?"buyable":"inactive",issues.Where(i=>i.Severity=="ERROR").Select(i=>i.Message).ToArray()),asin,Aj.S(summary,"fnSku"),Aj.S(summary,"productType"),statuses,issues,Attribute("product_description"),attrs,merchant.ValueKind!=JsonValueKind.Undefined?"MERCHANT":availability.Length>0?"AMAZON":"UNKNOWN");
    }
    public async Task<AmazonPage<AmazonOrder>> SearchOrders(ConnectionSettings s,AmazonQuery q,CancellationToken ct)
    {
        var query=Market();query["includedData"]="FULFILLMENT,PROCEEDS,PACKAGES";query["maxResultsPerPage"]="20";
        string path="/orders/2026-01-01/orders";
        if(!string.IsNullOrWhiteSpace(q.Search))
        {
            if(!System.Text.RegularExpressions.Regex.IsMatch(q.Search,@"^\d{3}-\d{7}-\d{7}$"))throw new IntegrationException("invalid_order","Amazon sipariş numarası 000-0000000-0000000 biçiminde olmalı.",400);
            if(!string.IsNullOrWhiteSpace(q.Status)||!string.IsNullOrWhiteSpace(q.FulfilledBy)||q.StartDate.HasValue)throw new IntegrationException("invalid_filter","Sipariş numarasıyla ararken diğer filtreleri temizleyin.",400);
            path+="/"+q.Search;query.Remove("maxResultsPerPage");query.Remove("marketplaceIds");
        }
        else
        {
            var end=Math.Min(q.EndDate??DateTimeOffset.UtcNow.AddMinutes(-3).ToUnixTimeMilliseconds(),DateTimeOffset.UtcNow.AddMinutes(-3).ToUnixTimeMilliseconds());
            var start=q.StartDate??end-(long)TimeSpan.FromDays(7).TotalMilliseconds;if(start>end)throw new IntegrationException("invalid_dates","Başlangıç tarihi Amazon'un güncel veri sınırından sonra olamaz.",400);
            query["createdAfter"]=Iso(start);query["createdBefore"]=Iso(end);
            if(!string.IsNullOrEmpty(q.Token))query["paginationToken"]=q.Token;
            if(!string.IsNullOrWhiteSpace(q.Status)){if(!new[]{"PENDING_AVAILABILITY","PENDING","UNSHIPPED","PARTIALLY_SHIPPED","SHIPPED","CANCELLED","UNFULFILLABLE"}.Contains(q.Status))throw new IntegrationException("invalid_status","Geçersiz Amazon sipariş durumu.",400);query["fulfillmentStatuses"]=q.Status;}
            if(!string.IsNullOrWhiteSpace(q.FulfilledBy))query["fulfilledBy"]=q.FulfilledBy;
        }
        using var doc=await client.Get(s.Environment,path,query,ct);var root=doc.RootElement;
        if(!string.IsNullOrWhiteSpace(q.Search)){var order=Aj.F(root,"order");if(order.ValueKind!=JsonValueKind.Object)throw new IntegrationException("invalid_response","Amazon sipariş yanıtı okunamadı.");return new([MapOrder(order)]);}
        if(Aj.F(root,"orders").ValueKind!=JsonValueKind.Array)throw new IntegrationException("invalid_response","Amazon sipariş listesi okunamadı.");
        return new(Aj.A(root,"orders").Select(MapOrder).ToArray(),Empty(Aj.S(Aj.F(root,"pagination"),"nextToken")));
    }
    public static AmazonOrder MapOrder(JsonElement e)
    {
        var fulfillment=Aj.F(e,"fulfillment");var status=Aj.S(fulfillment,"fulfillmentStatus");var buyer=Aj.F(e,"buyer");var address=Aj.F(Aj.F(e,"recipient"),"deliveryAddress");
        var total=Aj.F(Aj.F(e,"proceeds"),"grandTotal");var currency=Aj.S(total,"currencyCode");if(currency.Length==0)currency="TRY";
        var lines=Aj.A(e,"orderItems").Select(l=>{var product=Aj.F(l,"product");var amount=Aj.F(Aj.F(product,"price"),"unitPrice");return new MarketplaceOrderLine(Aj.S(l,"orderItemId"),Aj.S(product,"sellerSku"),"",Aj.S(product,"title"),checked((int)(Aj.N(l,"quantityOrdered")??0)),Aj.Money(amount),Aj.Money(Aj.F(Aj.F(l,"proceeds"),"proceedsTotal")),null,currency,status,"");}).ToArray();
        var packages=Aj.A(e,"packages").Select(p=>new AmazonPackage(Aj.S(p,"packageReferenceId"),Aj.S(p,"carrier"),Aj.S(p,"trackingNumber"),Aj.S(p,"packageStatus"),Aj.D(p,"shipTime"))).ToArray();var cargo=packages.FirstOrDefault();
        var order=new MarketplaceOrder("",Aj.S(e,"orderId"),status,Aj.D(e,"createdTime"),Aj.S(buyer,"buyerName"),Aj.S(buyer,"buyerEmail"),Aj.Money(total),null,null,currency,"","",address.ValueKind==JsonValueKind.Object?new("","Shipment",Aj.S(address,"countryCode"),Aj.S(address,"city"),Aj.S(address,"districtOrCounty"),Aj.S(address,"postalCode"),string.Join(' ',new[]{Aj.S(address,"addressLine1"),Aj.S(address,"addressLine2"),Aj.S(address,"addressLine3")}.Where(v=>v.Length>0)),false):null,lines,new(cargo?.Carrier??"",cargo?.TrackingNumber??"","","",cargo?.Id??"",null,null),new("","Bildirilmedi","",false,"","","",null,[]));
        return new(order,Aj.S(fulfillment,"fulfilledBy"),status,Aj.D(Aj.F(fulfillment,"shipByWindow"),"latestDateTime"),packages);
    }
    public async Task<MarketplacePage<MarketplaceProduct>> ProductsAsync(ConnectionSettings s,ProductQuery q,CancellationToken ct)
    {
        if(q.Page>0&&string.IsNullOrEmpty(q.NextPageToken))throw new IntegrationException("token_required","Amazon listeleri için önceki sayfanın devam bilgisi gerekir.",400);
        var result=await Listings(s,new(q.StockCode??q.Barcode,q.StockCode!=null?"SKU":"EAN",q.Status,Token:q.NextPageToken),ct);
        return new(result.Items.Select(i=>i.Product).ToArray(),q.Page,20,result.Total,null,result.NextToken);
    }
    public async Task<MarketplacePage<MarketplaceOrder>> OrdersAsync(ConnectionSettings s,OrderQuery q,CancellationToken ct)
    {
        var result=await SearchOrders(s,new(q.OrderNumber,Status:q.Status,Token:q.NextCursor,StartDate:q.StartDate,EndDate:q.EndDate),ct);
        // Without a next token Amazon has no further page; say so, otherwise a full last page of 20 is requested again.
        return new(result.Items.Select(i=>i.Order with{FulfilledBy=i.FulfilledBy}).ToArray(),q.Page,20,null,null,result.NextToken,HasMore:string.IsNullOrEmpty(result.NextToken)?false:null);
    }
    public async Task<object> Read(ConnectionSettings s,string kind,AmazonQuery q,CancellationToken ct)
    {
        if(q.Error() is {} error)throw new IntegrationException("invalid_query",error,400);
        if(kind is "products" or "inventory")return await Listings(s,q,ct);
        if(kind is "orders" or "shipping" or "invoices")return await SearchOrders(s,q,ct);
        var query=Market();string path;
        if(kind=="fba")
        {
            if(q.Status is {Length:>0}||q.StartDate.HasValue||q.FulfilledBy is {Length:>0})throw new IntegrationException("unsupported_filter","FBA stoklarında tam satıcı SKU'suyla arayın.",400);
            path="/fba/inventory/v1/summaries";query["details"]="true";query["granularityType"]="Marketplace";query["granularityId"]=AmazonApiClient.MarketplaceId;
            if(!string.IsNullOrWhiteSpace(q.Search))query["sellerSkus"]=q.Search;if(!string.IsNullOrEmpty(q.Token))query["nextToken"]=q.Token;
            using var doc=await client.Get(s.Environment,path,query,ct);var root=doc.RootElement;
            Aj.RequireArray(Aj.F(root,"payload"),"inventorySummaries");var rows=Aj.A(Aj.F(root,"payload"),"inventorySummaries").Select(e=>{var d=Aj.F(e,"inventoryDetails");return new{asin=Aj.S(e,"asin"),sku=Aj.S(e,"sellerSku"),fnSku=Aj.S(e,"fnSku"),name=Aj.S(e,"productName"),available=Aj.N(d,"fulfillableQuantity"),reserved=Aj.N(Aj.F(d,"reservedQuantity"),"totalReservedQuantity"),inbound=SumKnown(Aj.N(d,"inboundWorkingQuantity"),Aj.N(d,"inboundShippedQuantity"),Aj.N(d,"inboundReceivingQuantity")),unfulfillable=Aj.N(Aj.F(d,"unfulfillableQuantity"),"totalUnfulfillableQuantity"),total=Aj.N(e,"totalQuantity"),updatedAt=Aj.D(e,"lastUpdatedTime")};}).ToArray();
            return new AmazonPage<object>(rows,Empty(Aj.S(Aj.F(root,"pagination"),"nextToken")));
        }
        if(kind=="finance")
        {
            query=new(){["marketplaceId"]=AmazonApiClient.MarketplaceId};path="/finances/2024-06-19/transactions";
            if(!string.IsNullOrWhiteSpace(q.Search)){query["relatedIdentifierName"]="ORDER_ID";query["relatedIdentifierValue"]=q.Search;}
            var end=Math.Min(q.EndDate??DateTimeOffset.UtcNow.AddMinutes(-3).ToUnixTimeMilliseconds(),DateTimeOffset.UtcNow.AddMinutes(-3).ToUnixTimeMilliseconds());query["postedAfter"]=Iso(q.StartDate??end-(long)TimeSpan.FromDays(14).TotalMilliseconds);query["postedBefore"]=Iso(end);
            if(!string.IsNullOrWhiteSpace(q.Status)){if(!new[]{"DEFERRED","RELEASED","DEFERRED_RELEASED"}.Contains(q.Status))throw new IntegrationException("invalid_status","Geçersiz finans durumu.",400);query["transactionStatus"]=q.Status;}
            if(!string.IsNullOrEmpty(q.Token))query["nextToken"]=q.Token;
            using var doc=await client.Get(s.Environment,path,query,ct);var payload=Aj.F(doc.RootElement,"payload");
            Aj.RequireArray(payload,"transactions");var rows=Aj.A(payload,"transactions").Select(e=>new{id=Aj.S(e,"transactionId"),type=Aj.S(e,"transactionType"),status=Aj.S(e,"transactionStatus"),description=Aj.S(e,"description"),date=Aj.D(e,"postedDate"),amount=Aj.Money(Aj.F(e,"totalAmount")),currency=Aj.S(Aj.F(e,"totalAmount"),"currencyCode"),orderNumber=Aj.S(Aj.A(e,"relatedIdentifiers").FirstOrDefault(i=>Aj.S(i,"relatedIdentifierName")=="ORDER_ID"),"relatedIdentifierValue")}).ToArray();return new AmazonPage<object>(rows,Empty(Aj.S(payload,"nextToken")));
        }
        if(kind=="reports")
        {
            path="/reports/2021-06-30/reports";
            if(!string.IsNullOrEmpty(q.Token))query=new(){["nextToken"]=q.Token};
            else{query["reportTypes"]=q.ReportType is null or ""?"GET_MERCHANT_LISTINGS_ALL_DATA":q.ReportType;query["pageSize"]="20";if(!string.IsNullOrWhiteSpace(q.Status))query["processingStatuses"]=q.Status;}
            using var doc=await client.Get(s.Environment,path,query,ct);Aj.RequireArray(doc.RootElement,"reports");var rows=Aj.A(doc.RootElement,"reports").Select(e=>new{id=Aj.S(e,"reportId"),type=Aj.S(e,"reportType"),status=Aj.S(e,"processingStatus"),createdAt=Aj.D(e,"createdTime"),startDate=Aj.D(e,"dataStartTime"),endDate=Aj.D(e,"dataEndTime"),hasDocument=Aj.S(e,"reportDocumentId").Length>0}).ToArray();return new AmazonPage<object>(rows,Empty(Aj.S(doc.RootElement,"nextToken")));
        }
        if(kind=="catalog")
        {
            path="/definitions/2020-09-01/productTypes";query["locale"]="tr_TR";if(!string.IsNullOrWhiteSpace(q.Search))query["keywords"]=q.Search;
            using var doc=await client.Get(s.Environment,path,query,ct);Aj.RequireArray(doc.RootElement,"productTypes");return new AmazonPage<object>(Aj.A(doc.RootElement,"productTypes").Select(e=>new{name=Aj.S(e,"name"),displayName=Aj.S(e,"displayName")}).ToArray());
        }
        throw new IntegrationException("not_available","Bu bölümün gerçek Amazon işlemleri henüz tamamlanmadı. Demo mağazada örnek akışı inceleyebilirsiniz.",501);
    }
    private static decimal? SumKnown(params decimal?[] values)=>values.All(v=>v.HasValue)?values.Sum(v=>v!.Value):null;
    private static string? Empty(string value)=>value.Length==0?null:value;
}
