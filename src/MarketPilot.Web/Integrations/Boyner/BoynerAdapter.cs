using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MarketPilot.Web.Integrations.Boyner;
public record BoynerQuery(string? Search=null,string SearchType="SKU",string? Status=null,string? SaleStatus=null,long? StartDate=null,long? EndDate=null,int Page=0,int Size=25,string? BatchId=null);
public record BoynerProduct(MarketplaceProduct Product,string Description,Dictionary<string,string> Attributes,int? DeliveryDuration,decimal? Commission,decimal? VatRate);
public record BoynerHistory(string Status,DateTimeOffset? Date);
public record BoynerOrder(MarketplaceOrder Order,string PackageNumber,DateTimeOffset? ShipBy,BoynerHistory[] History);
public record BoynerClaimLine(string Id,string ProductName,string Sku,string Barcode,int? Quantity,string Status,string Reason);
public record BoynerClaim(string Id,string OrderNumber,string CustomerName,string ClaimDate,BoynerClaimLine[] Lines);
public record BoynerPage<T>(IReadOnlyList<T> Items,int Page,int Size,long? TotalElements=null,int? TotalPages=null,string? TaskStatus=null);

public sealed partial class BoynerAdapter(HttpClient http,IConfiguration configuration):IMarketplaceAdapter
{
    public string Marketplace=>"boyner";
    private JsonElement categories;private DateTimeOffset categoriesUntil;private readonly SemaphoreSlim categoryGate=new(1,1);
    public static bool IsConfigured(IConfiguration c,string environment)=>environment=="production"&&long.TryParse(c["Marketplaces:Boyner:production:SupplierId"],out var supplier)&&supplier>0&&new[]{"ApiKey","ApiSecretKey"}.All(k=>!string.IsNullOrWhiteSpace(c[$"Marketplaces:Boyner:production:{k}"])&&!c[$"Marketplaces:Boyner:production:{k}"]!.Any(char.IsControl));
    private string Supplier(ConnectionSettings s){if(!IsConfigured(configuration,s.Environment))throw new IntegrationException("configuration_missing",s.Environment=="stage"?"Boyner için doğrulanmış ayrı bir test API ortamı yapılandırılmadı. Demo mağazayı seçin.":"Boyner mağaza bağlantısı henüz tanımlanmadı.",409);return long.Parse(configuration["Marketplaces:Boyner:production:SupplierId"]!).ToString(CultureInfo.InvariantCulture);}
    public static string? Validate(BoynerQuery q)
    {
        if(q.Page<0||q.Page>2500||q.Size is <1 or >100)return "Sayfa 0–2500, kayıt sayısı 1–100 arasında olmalı. Daha büyük listeler için sorguyu daraltın.";
        if(new[]{q.Search,q.Status,q.BatchId}.Any(v=>v?.Length>120)||q.SearchType is not("SKU" or "BARCODE" or "MODEL"))return "Geçersiz filtre.";
        if(q.StartDate.HasValue!=q.EndDate.HasValue||q.StartDate<0||q.EndDate>253402300799999||q.StartDate>q.EndDate||q.EndDate-q.StartDate>TimeSpan.FromDays(31).TotalMilliseconds)return "Tarihleri birlikte seçin; aralık en fazla 31 gün olabilir.";
        return null;
    }
    private Task<JsonDocument> Get(ConnectionSettings s,string path,Dictionary<string,string> query,CancellationToken ct)=>Send(s,path,query,null,ct);
    private async Task<JsonDocument> Send(ConnectionSettings s,string path,Dictionary<string,string> query,HttpContent? body,CancellationToken ct)
    {
        var supplier=Supplier(s);var qs=string.Join('&',query.Select(p=>Uri.EscapeDataString(p.Key)+"="+Uri.EscapeDataString(p.Value)));
        using var request=new HttpRequestMessage(body==null?HttpMethod.Get:HttpMethod.Post,"https://merchantapi.boyner.com.tr"+path+(qs.Length>0?"?"+qs:"")){Content=body};
        // The official security scheme describes supplierId:apiKey:apiSecretKey.
        var basic=$"{supplier}:{configuration["Marketplaces:Boyner:production:ApiKey"]}:{configuration["Marketplaces:Boyner:production:ApiSecretKey"]}";
        request.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(basic)));request.Headers.TryAddWithoutValidation("User-Agent",supplier+" SelfIntegration");request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);if(!response.IsSuccessStatusCode){var code=(int)response.StatusCode;throw new IntegrationException(code==429?"rate_limited":"boyner_error",code switch{401 or 403=>"Boyner mağaza yetkisi doğrulanamadı.",429=>"Boyner istek sınırına ulaşıldı. Biraz sonra yeniden deneyin.",404=>"Boyner kaydı bulunamadı.",_=>"Boyner verileri alınamadı. Daha sonra yeniden deneyin."},code is 401 or 403?409:code is 400 or 404 or 409 or 422 or 429?(code==422?400:code):502);}
        const int limit=16*1024*1024;if(response.Content.Headers.ContentLength>limit)throw new IntegrationException("response_large","Boyner yanıtı boyut sınırını aştı.");await using var stream=await response.Content.ReadAsStreamAsync(ct);using var output=new MemoryStream();var block=new byte[8192];int n;while((n=await stream.ReadAsync(block,ct))>0){if(output.Length+n>limit)throw new IntegrationException("response_large","Boyner yanıtı boyut sınırını aştı.");output.Write(block,0,n);}var doc=JsonDocument.Parse(output.ToArray());if(Bj.F(doc.RootElement,"success").ValueKind==JsonValueKind.False){doc.Dispose();throw new IntegrationException("boyner_rejected","Boyner isteği tamamlayamadı. Mağaza yetkisini ve sorguyu kontrol edin.");}return doc;
    }
    private static Dictionary<string,string> Paging(BoynerQuery q)=>new(){["page"]=q.Page.ToString(CultureInfo.InvariantCulture),["size"]=q.Size.ToString(CultureInfo.InvariantCulture)};
    private static void Dates(Dictionary<string,string> p,BoynerQuery q){if(q.StartDate.HasValue){p["startDate"]=q.StartDate.Value.ToString(CultureInfo.InvariantCulture);p["endDate"]=q.EndDate!.Value.ToString(CultureInfo.InvariantCulture);}}
    private static BoynerPage<T> Page<T>(JsonElement root,T[] rows,BoynerQuery q)=>new(rows,q.Page,q.Size,(long?)Bj.N(root,"totalElements"),(int?)Bj.N(root,"totalPages"));
    public async Task<MarketplaceAccount> TestAsync(ConnectionSettings s,CancellationToken ct)
    {
        using var doc=await Get(s,"/sapigw/suppliers/addresses",new(){["supplierId"]=Supplier(s)},ct);if(doc.RootElement.ValueKind!=JsonValueKind.Array)throw new IntegrationException("invalid_response","Boyner adres yanıtı okunamadı.");return new("boyner",Supplier(s),s.Environment,"TR",doc.RootElement.EnumerateArray().Select(Address).ToArray(),DateTimeOffset.UtcNow);
    }
    public Task<MarketplaceAccount> AccountAsync(ConnectionSettings s,CancellationToken ct)=>TestAsync(s,ct);
    public static MarketplaceAddress Address(JsonElement e)=>new(Bj.S(e,"id"),Bj.S(e,"addressType"),Bj.S(e,"country"),Bj.S(e,"city"),Bj.S(e,"district"),Bj.S(e,"postCode"),Bj.S(e,"fullAddress"),Bj.F(e,"default").ValueKind==JsonValueKind.True);
    public async Task<BoynerPage<BoynerProduct>> Products(ConnectionSettings s,BoynerQuery q,CancellationToken ct)
    {
        var query=Paging(q);Dates(query,q);if(q.Search is {Length:>0})query[q.SearchType switch{"BARCODE"=>"barcode","MODEL"=>"productMainId",_=>"stockCode"}]=q.Search;
        if(q.Status is {Length:>0}){var key=q.Status switch{"approved"=>"approved","unapproved"=>"approved","rejected"=>"rejected","archived"=>"archived","blacklisted"=>"blacklisted",_=>throw new IntegrationException("invalid_status","Geçersiz Boyner ürün filtresi.",400)};query[key]=q.Status=="unapproved"?"false":"true";}
        if(q.SaleStatus is {Length:>0}){if(q.SaleStatus is not("true" or "false"))throw new IntegrationException("invalid_status","Geçersiz satış filtresi.",400);query["onSale"]=q.SaleStatus;}
        using var doc=await Get(s,$"/sapigw/suppliers/{Supplier(s)}/products",query,ct);Bj.Require(doc.RootElement,"content");return Page(doc.RootElement,Bj.A(doc.RootElement,"content").Select(MapProduct).ToArray(),q);
    }
    public static BoynerProduct MapProduct(JsonElement e)
    {
        var attrs=new Dictionary<string,string>();foreach(var a in Bj.A(e,"attributes")){var name=Bj.S(a,"attributeName");if(name.Length>0)attrs[name]=Bj.S(a,"attributeValue") is {Length:>0} value?value:Bj.S(a,"customAttributeValue");}
        var status=Bj.F(e,"archived").ValueKind==JsonValueKind.True?"archived":Bj.F(e,"rejected").ValueKind==JsonValueKind.True?"rejected":Bj.F(e,"approved").ValueKind==JsonValueKind.True?"approved":"unapproved";
        var p=new MarketplaceProduct(Bj.S(e,"id"),Bj.S(e,"productContentId"),Bj.S(e,"productMainId"),Bj.S(e,"stockCode"),Bj.S(e,"barcode"),Bj.S(e,"title"),Bj.S(e,"pimCategoryId"),Bj.S(e,"categoryName"),Bj.S(e,"brand"),Bj.N(e,"listPrice"),Bj.N(e,"salePrice"),null,(int?)Bj.N(e,"quantity"),"TRY",Bj.F(e,"onsale").ValueKind switch{JsonValueKind.True=>true,JsonValueKind.False=>false,_=>(bool?)null},"",Bj.A(e,"images").Select(x=>Bj.S(x,"url")).Where(v=>v.Length>0).ToArray(),status,[]);
        return new(p,Bj.S(e,"description"),attrs,(int?)Bj.N(e,"deliveryDuration"),Bj.N(e,"commission"),Bj.N(e,"vatRate"));
    }
    public async Task<BoynerPage<BoynerOrder>> Orders(ConnectionSettings s,BoynerQuery q,CancellationToken ct)
    {
        var query=Paging(q);Dates(query,q);query["OrderByField"]="2";query["OrderByDirection"]="DESC";if(q.Search is {Length:>0})query["orderNumber"]=q.Search;
        if(q.Status is {Length:>0}){if(!new[]{"Created","Picking","Shipped","Cancelled","Delivered","UnDelivered","Repack","Unpackaged","Awaiting"}.Contains(q.Status))throw new IntegrationException("invalid_status","Geçersiz Boyner paket durumu.",400);query[q.Status=="Awaiting"?"status":"shipmentPackageStatus"]=q.Status;}
        using var doc=await Get(s,$"/sapigw/suppliers/{Supplier(s)}/orders",query,ct);Bj.Require(doc.RootElement,"content");return Page(doc.RootElement,Bj.A(doc.RootElement,"content").Select(MapOrder).ToArray(),q);
    }
    public static BoynerOrder MapOrder(JsonElement e)
    {
        var address=Bj.F(e,"shipmentAddress");var invoice=Bj.F(e,"invoiceAddress");var currency=Bj.S(e,"currencyCode");if(currency.Length==0)currency="TRY";var status=Bj.S(e,"shipmentPackageStatus");
        var order=new MarketplaceOrder(Bj.S(e,"id"),Bj.S(e,"orderNumber"),status,Bj.D(e,"orderDate"),Bj.S(e,"customerFullName"),Bj.S(e,"customerEmail"),Bj.N(e,"totalPrice"),Bj.N(e,"grossAmount"),Bj.N(e,"totalDiscount"),currency,"",Bj.S(e,"deliveryAddressType"),address.ValueKind==JsonValueKind.Object?new(Bj.S(address,"id"),"Shipment",Bj.S(address,"countryName"),Bj.S(address,"cityName"),Bj.S(address,"districtName"),Bj.S(address,"postalCode"),Bj.S(address,"fullAddress"),false):null,
            Bj.A(e,"lines").Select(l=>new MarketplaceOrderLine(Bj.S(l,"id"),Bj.S(l,"merchantSku"),Bj.S(l,"barcode"),Bj.S(l,"productName"),(int)(Bj.N(l,"quantity")??0),Bj.N(l,"price"),Bj.N(l,"amount"),null,currency,Bj.S(l,"orderLineItemStatusName"),"")).ToArray(),new(Bj.S(e,"cargoProviderName"),Bj.S(e,"cargoTrackingNumber"),Bj.S(e,"cargoTrackingLink"),Bj.S(e,"cargoSenderNumber"),Bj.S(e,"packageNumber"),null,null),new("",Bj.S(e,"invoiceLink").Length>0?"Bağlantı bildirildi":"Bildirilmedi",Bj.S(e,"invoiceLink"),Bj.F(e,"commercial").ValueKind==JsonValueKind.True,Bj.S(invoice,"company"),Bj.S(invoice,"taxOffice"),Bj.S(invoice,"taxNumber"),null,[]));
        return new(order,Bj.S(e,"packageNumber"),Bj.D(e,"agreedDeliveryDate"),Bj.A(e,"packageHistories").Select(x=>new BoynerHistory(Bj.S(x,"status"),Bj.D(x,"createdDate"))).ToArray());
    }
    public async Task<object> Read(ConnectionSettings s,string kind,BoynerQuery q,CancellationToken ct)
    {
        Supplier(s);if(Validate(q) is {} error)throw new IntegrationException("invalid_query",error,400);
        if(kind is "products" or "inventory")return await Products(s,q,ct);if(kind is "orders" or "shipping" or "invoices" or "finance")return await Orders(s,q,ct);
        if(kind=="addresses"){var account=await AccountAsync(s,ct);return new BoynerPage<MarketplaceAddress>(account.Addresses.Skip(q.Page*q.Size).Take(q.Size).ToArray(),q.Page,q.Size,account.Addresses.Count,(int)Math.Ceiling(account.Addresses.Count/(double)q.Size));}
        if(kind=="brands"){using var doc=await Get(s,"/sapigw/brands",new(){["Page"]=q.Page.ToString(),["Size"]=q.Size.ToString()},ct);if(doc.RootElement.ValueKind!=JsonValueKind.Array)throw new IntegrationException("invalid_response","Boyner marka listesi okunamadı.");return new BoynerPage<object>(doc.RootElement.EnumerateArray().Select(e=>new{id=Bj.S(e,"id"),name=Bj.S(e,"name")}).ToArray(),q.Page,q.Size);}
        if(kind=="catalog")
        {
            await categoryGate.WaitAsync(ct);try{if(categoriesUntil<DateTimeOffset.UtcNow){using var doc=await Get(s,"/sapigw/product-categories",new(),ct);if(doc.RootElement.ValueKind!=JsonValueKind.Array)throw new IntegrationException("invalid_response","Boyner kategori ağacı okunamadı.");categories=doc.RootElement.Clone();categoriesUntil=DateTimeOffset.UtcNow.AddMinutes(15);}}finally{categoryGate.Release();}
            var rows=new List<object>();void Walk(JsonElement e,string parent,int depth){if(depth>20)throw new IntegrationException("invalid_response","Kategori ağacı çok derin.");var name=Bj.S(e,"name");var path=parent.Length>0?parent+" / "+name:name;var children=Bj.A(e,"subCategories");if(children.Length==0&&(string.IsNullOrWhiteSpace(q.Search)||path.Contains(q.Search,StringComparison.OrdinalIgnoreCase)))rows.Add(new{id=Bj.S(e,"id"),name,path});foreach(var child in children)Walk(child,path,depth+1);}foreach(var e in categories.EnumerateArray())Walk(e,"",0);return new BoynerPage<object>(rows.Skip(q.Page*q.Size).Take(q.Size).ToArray(),q.Page,q.Size,rows.Count,(int)Math.Ceiling(rows.Count/(double)q.Size));
        }
        if(kind=="attributes")
        {
            if(!long.TryParse(q.Search,out var id)||id<=0)throw new IntegrationException("invalid_category","Kategori numarası geçersiz.",400);using var doc=await Get(s,$"/sapigw/product-categories/{id}/attributes",new(),ct);Bj.Require(doc.RootElement,"categoryAttributes");var rows=Bj.A(doc.RootElement,"categoryAttributes").Select(e=>{var a=Bj.F(e,"attribute");return new{id=Bj.S(a,"id"),name=Bj.S(a,"name"),required=Bj.F(e,"required").ValueKind==JsonValueKind.True,varianter=Bj.F(e,"varianter").ValueKind==JsonValueKind.True,allowCustom=Bj.F(e,"allowCustom").ValueKind==JsonValueKind.True,values=Bj.A(a,"attributeValues").Take(100).Select(v=>new{id=Bj.S(v,"id"),name=Bj.S(v,"name")}).ToArray(),valueCount=Bj.A(a,"attributeValues").Length};}).ToArray();return new BoynerPage<object>(rows,q.Page,rows.Length,rows.Length,1);
        }
        if(kind=="claims")
        {
            var query=Paging(q);Dates(query,q);if(q.Search is {Length:>0})query["orderNumber"]=q.Search;if(q.Status is {Length:>0}){if(!new[]{"Created","WaitingInAction","InCargo","Accepted","Rejected","Cancelled","Unresolved","InAnalysis"}.Contains(q.Status))throw new IntegrationException("invalid_status","Geçersiz Boyner iade durumu.",400);query["claimItemStatus"]=q.Status;}
            using var doc=await Get(s,$"/sapigw/suppliers/{Supplier(s)}/v2/claims",query,ct);Bj.Require(doc.RootElement,"content");return Page(doc.RootElement,Bj.A(doc.RootElement,"content").Select(MapClaim).ToArray(),q);
        }
        if(kind=="history")
        {
            if(!Guid.TryParse(q.BatchId,out var id))throw new IntegrationException("batch_required","Geçerli işlem takip numarası girin.",400);using var doc=await Get(s,$"/sapigw/suppliers/{Supplier(s)}/products/batch-requests/{id}",new(),ct);Bj.Require(doc.RootElement,"items");var all=Bj.A(doc.RootElement,"items").Select(e=>new{sku=Bj.S(Bj.F(e,"requestItem"),"stockCode"),barcode=Bj.S(Bj.F(e,"requestItem"),"barcode"),status=Bj.S(e,"state"),success=Bj.F(e,"status").ValueKind==JsonValueKind.True,reasons=Reasons(Bj.F(e,"failureReasons")).Take(30).ToArray()}).ToArray();return new BoynerPage<object>(all.Skip(q.Page*q.Size).Take(q.Size).ToArray(),q.Page,q.Size,all.Length,(int)Math.Ceiling(all.Length/(double)q.Size),Bj.S(doc.RootElement,"status"));
        }
        throw new IntegrationException("not_available","Bu bölümün gerçek Boyner işlemleri henüz tamamlanmadı.",501);
    }
    public static BoynerClaim MapClaim(JsonElement e)
    {
        var lines=new List<BoynerClaimLine>();foreach(var group in Bj.A(e,"items")){var orders=Bj.A(group,"orderLine");foreach(var item in Bj.A(group,"claimItems")){var order=orders.FirstOrDefault(o=>Bj.S(o,"id")==Bj.S(item,"orderLineItemId"));lines.Add(new(Bj.S(item,"id"),Bj.S(order,"productName"),Bj.S(order,"merchantSku"),Bj.S(order,"barcode"),(int?)Bj.N(item,"quantity"),Bj.S(Bj.F(item,"claimItemStatus"),"name"),Bj.S(Bj.F(item,"reason"),"description")));}}return new(Bj.S(e,"id"),Bj.S(e,"orderNumber"),Bj.S(e,"customerFullName"),Bj.S(e,"claimDate"),lines.ToArray());
    }
    private static IEnumerable<string> Reasons(JsonElement e,int depth=0){if(depth>5)yield break;if(e.ValueKind==JsonValueKind.String)yield return e.GetString()!;else if(e.ValueKind==JsonValueKind.Array)foreach(var v in e.EnumerateArray())foreach(var r in Reasons(v,depth+1))yield return r;else if(e.ValueKind==JsonValueKind.Object)foreach(var p in e.EnumerateObject())foreach(var r in Reasons(p.Value,depth+1))yield return r;}
    public async Task<MarketplacePage<MarketplaceProduct>> ProductsAsync(ConnectionSettings s,ProductQuery q,CancellationToken ct){var query=new BoynerQuery(q.StockCode??q.Barcode,q.StockCode!=null?"SKU":"BARCODE",q.Approval=="unapproved"?"unapproved":"approved",Page:q.Page,Size:q.Size);if(Validate(query) is {} error)throw new IntegrationException("invalid_query",error,400);var r=await Products(s,query,ct);return new(r.Items.Select(p=>p.Product).ToArray(),r.Page,r.Size,r.TotalElements,r.TotalPages,null);}
    public async Task<MarketplacePage<MarketplaceOrder>> OrdersAsync(ConnectionSettings s,OrderQuery q,CancellationToken ct){var query=new BoynerQuery(q.OrderNumber,Status:q.Status,StartDate:q.StartDate,EndDate:q.EndDate,Page:q.Page,Size:q.Size);if(Validate(query) is {} error)throw new IntegrationException("invalid_query",error,400);var r=await Orders(s,query,ct);return new(r.Items.Select(p=>p.Order).ToArray(),r.Page,r.Size,r.TotalElements,r.TotalPages,null);}
}
internal static class Bj
{
    public static JsonElement F(JsonElement e,string k)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(k,out var v)?v:default;public static string S(JsonElement e,string k)=>F(e,k).ValueKind is JsonValueKind.String or JsonValueKind.Number?F(e,k).ToString():"";public static decimal? N(JsonElement e,string k)=>decimal.TryParse(F(e,k).ToString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var n)?n:null;public static JsonElement[] A(JsonElement e,string k)=>F(e,k).ValueKind==JsonValueKind.Array?F(e,k).EnumerateArray().ToArray():[];public static void Require(JsonElement e,string k){if(F(e,k).ValueKind!=JsonValueKind.Array)throw new IntegrationException("invalid_response","Boyner liste yanıtı beklenen biçimde değil.");}
    public static DateTimeOffset? D(JsonElement e,string k){var n=N(e,k);if(n is >0 and <=253402300799999)return n<100000000000?DateTimeOffset.FromUnixTimeSeconds((long)n):DateTimeOffset.FromUnixTimeMilliseconds((long)n);return DateTimeOffset.TryParse(S(e,k),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var d)?d:null;}
}
