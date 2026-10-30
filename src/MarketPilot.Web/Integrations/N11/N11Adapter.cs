using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace MarketPilot.Web.Integrations.N11;

public record N11Query(string? Search=null,string SearchType="SKU",string? Status=null,string? SaleStatus=null,string Sender="ALL",string? Brand=null,string? CategoryId=null,long? StartDate=null,long? EndDate=null,int Page=0,int Size=25,long? TaskId=null)
{
    public string? Error()
    {
        if(Page<0||Size is <1 or >100||(long)Page*Size>int.MaxValue)return "Geçersiz sayfa bilgisi.";
        if(new[]{Search,Brand,CategoryId,Status}.Any(v=>v?.Length>120))return "Filtre değeri çok uzun.";
        if(Sender is not("ALL" or "SELLER" or "N11")||SearchType is not("SKU" or "MODEL" or "ID"))return "Geçersiz gönderici veya arama türü.";
        if(StartDate.HasValue!=EndDate.HasValue||StartDate<0||EndDate>253402300799999||StartDate>EndDate||EndDate-StartDate>TimeSpan.FromDays(15).TotalMilliseconds)return "Tarihleri birlikte seçin; n11 sorgusu en fazla 15 günlük olmalı.";
        if(CategoryId is {Length:>0}&&!long.TryParse(CategoryId,out _))return "Kategori numarası geçersiz.";
        return null;
    }
}
public record N11Product(MarketplaceProduct Product,string Description,Dictionary<string,string> Attributes,string Sender,int? PreparingDay,string ShipmentTemplate,decimal? VatRate,decimal? CommissionRate);
public record N11History(string Status,DateTimeOffset? Date);
public record N11Amount(string Id,string ProductName,int Quantity,decimal? UnitPrice,decimal? InvoiceAmount,decimal? CommissionRate,decimal? CampaignRate);
public record N11Order(MarketplaceOrder Order,string Sender,decimal? SellerInvoiceAmount,N11Amount[] Amounts,N11History[] History,DateTimeOffset? ShipBy,bool MicroExport);
public record N11Page<T>(IReadOnlyList<T> Items,int Page,int Size,long? TotalElements=null,int? TotalPages=null,string? TaskStatus=null);

// n11 REST and the one allowlisted SOAP read operation; no live seller mutations.
public sealed partial class N11Adapter(HttpClient http,IConfiguration configuration) : IMarketplaceAdapter
{
    public string Marketplace=>"n11";
    private JsonElement categories;private DateTimeOffset categoriesUntil;private readonly SemaphoreSlim categoryGate=new(1,1);
    public static bool IsConfigured(IConfiguration c,string environment)=>environment=="production"&&new[]{"AppKey","AppSecret"}.All(k=>!string.IsNullOrWhiteSpace(c[$"Marketplaces:N11:production:{k}"])&&!c[$"Marketplaces:N11:production:{k}"]!.Any(char.IsControl));
    private void Credentials(ConnectionSettings s)
    {
        if(s.Environment!="production")throw new IntegrationException("sandbox_unavailable","n11 için doğrulanmış ayrı test ortamı yapılandırılmadı. Hesapsız denemek için demo mağazayı seçin.",409);
        if(!IsConfigured(configuration,s.Environment))throw new IntegrationException("configuration_missing","n11 mağaza bağlantısı henüz tanımlanmadı.",409);
    }
    private async Task<byte[]> Send(ConnectionSettings s,string path,Dictionary<string,string>? query,HttpContent? body,CancellationToken ct)
    {
        Credentials(s);var allowed=new[]{"/ms/product/tasks/price-stock-update","/ms/product-query","/rest/delivery/v1/shipmentPackages","/cdn/categories","/ms/product/task-details/page-query","/ws/returnService/"};if(!allowed.Contains(path))throw new IntegrationException("invalid_operation","Geçersiz n11 işlemi.",400);
        var qs=string.Join('&',(query??new()).Select(p=>Uri.EscapeDataString(p.Key)+"="+Uri.EscapeDataString(p.Value)));
        using var request=new HttpRequestMessage(body==null?HttpMethod.Get:HttpMethod.Post,"https://api.n11.com"+path+(qs.Length>0?"?"+qs:"")){Content=body};
        if(path!="/ws/returnService/"){request.Headers.TryAddWithoutValidation("appKey",configuration["Marketplaces:N11:production:AppKey"]);request.Headers.TryAddWithoutValidation("appSecret",configuration["Marketplaces:N11:production:AppSecret"]);}
        else request.Headers.TryAddWithoutValidation("SOAPAction","\"\"");
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!response.IsSuccessStatusCode){var code=(int)response.StatusCode;throw new IntegrationException(code==429?"rate_limited":"n11_error",code switch{401 or 403=>"n11 mağaza yetkisi doğrulanamadı.",429=>"n11 istek sınırına ulaşıldı. Biraz sonra yeniden deneyin.",404=>"n11 kaydı bulunamadı.",_=>"n11 verileri alınamadı. Daha sonra yeniden deneyin."},code is 401 or 403?409:code is 400 or 404 or 409 or 422 or 429?(code==422?400:code):502,code==429?IntegrationException.ReadRetryAfter(response):null);}
        const int limit=16*1024*1024;if(response.Content.Headers.ContentLength>limit)throw new IntegrationException("response_large","n11 yanıtı boyut sınırını aştı.");
        await using var stream=await response.Content.ReadAsStreamAsync(ct);using var output=new MemoryStream();var block=new byte[8192];int count;while((count=await stream.ReadAsync(block,ct))>0){if(output.Length+count>limit)throw new IntegrationException("response_large","n11 yanıtı boyut sınırını aştı.");output.Write(block,0,count);}return output.ToArray();
    }
    private async Task<JsonDocument> Get(ConnectionSettings s,string path,Dictionary<string,string> q,CancellationToken ct)=>JsonDocument.Parse(await Send(s,path,q,null,ct));
    private static Dictionary<string,string> Paging(N11Query q)=>new(){["page"]=q.Page.ToString(CultureInfo.InvariantCulture),["size"]=q.Size.ToString(CultureInfo.InvariantCulture)};
    private static N11Page<T> Page<T>(JsonElement root,T[] rows,N11Query q)=>new(rows,q.Page,q.Size,(long?)Nj.N(root,"totalElements"),(int?)Nj.N(root,"totalPages"));
    public async Task<MarketplaceAccount> TestAsync(ConnectionSettings s,CancellationToken ct)
    {
        var q=new N11Query(Size:1);using var doc=await Get(s,"/ms/product-query",new(){["page"]="0",["size"]="1",["sender"]="ALL"},ct);Nj.Require(doc.RootElement,"content");var row=Nj.A(doc.RootElement,"content").FirstOrDefault();return new("n11",Nj.S(row,"sellerId"),s.Environment,"TR",[],DateTimeOffset.UtcNow);
    }
    public Task<MarketplaceAccount> AccountAsync(ConnectionSettings s,CancellationToken ct)=>TestAsync(s,ct);
    public async Task<N11Page<N11Product>> Products(ConnectionSettings s,N11Query q,CancellationToken ct)
    {
        var query=Paging(q);query["sender"]=q.Sender;
        if(!string.IsNullOrWhiteSpace(q.Search)){var key=q.SearchType switch{"MODEL"=>"productMainId","ID"=>"id",_=>"stockCode"};if(key=="id"&&!long.TryParse(q.Search,out _))throw new IntegrationException("invalid_id","Ürün numarası geçersiz.",400);query[key]=q.Search;}
        if(!string.IsNullOrWhiteSpace(q.Status)){if(!new[]{"Active","InCatalogApproval","Suspended","CatalogRejected","Prohibited","Unlisted","InApproval"}.Contains(q.Status))throw new IntegrationException("invalid_status","Geçersiz n11 ürün durumu.",400);query["productStatus"]=q.Status;}
        if(!string.IsNullOrWhiteSpace(q.SaleStatus)){if(q.SaleStatus is not("On_Sale" or "Out_Of_Stock"))throw new IntegrationException("invalid_status","Geçersiz satış durumu.",400);query["saleStatus"]=q.SaleStatus;}
        if(!string.IsNullOrWhiteSpace(q.Brand))query["brandName"]=q.Brand;if(!string.IsNullOrWhiteSpace(q.CategoryId))query["categoryIds"]=q.CategoryId;
        using var doc=await Get(s,"/ms/product-query",query,ct);Nj.Require(doc.RootElement,"content");return Page(doc.RootElement,Nj.A(doc.RootElement,"content").Select(MapProduct).ToArray(),q);
    }
    public static N11Product MapProduct(JsonElement e)
    {
        var attrs=new Dictionary<string,string>();foreach(var a in Nj.A(e,"attributes")){var name=Nj.S(a,"attributeName");if(name.Length>0)attrs[name]=Nj.S(a,"attributeValue");}
        var status=Nj.S(e,"status");var reject=Nj.F(e,"rejectInfo");var reasons=reject.ValueKind switch{JsonValueKind.String=>new[]{reject.GetString()!},JsonValueKind.Array=>reject.EnumerateArray().Select(v=>v.ValueKind==JsonValueKind.String?v.ToString():Nj.S(v,"reason")).Where(v=>v.Length>0).ToArray(),JsonValueKind.Object=>new[]{Nj.S(reject,"reason")}.Where(v=>v.Length>0).ToArray(),_=>Array.Empty<string>()};
        var currency=Nj.S(e,"currencyType");if(currency is "TL" or "")currency="TRY";
        var p=new MarketplaceProduct(Nj.S(e,"n11ProductId"),Nj.S(e,"catalogId"),Nj.S(e,"productMainId"),Nj.S(e,"stockCode"),Nj.S(e,"barcode"),Nj.S(e,"title"),Nj.S(e,"categoryId"),"",attrs.GetValueOrDefault("Marka")??"",Nj.N(e,"listPrice"),Nj.N(e,"salePrice"),null,(int?)Nj.N(e,"quantity"),currency,Nj.S(e,"saleStatus") switch{"On_Sale"=>true,"Out_Of_Stock"=>false,_=>(bool?)null},"",Nj.A(e,"imageUrls").Where(x=>x.ValueKind==JsonValueKind.String).Select(x=>x.ToString()).ToArray(),status,reasons);
        p=p with{FulfilledBy=Nj.S(e,"sender").ToUpperInvariant(),VatRate=Nj.N(e,"vatRate")};
        return new(p,Nj.S(e,"description"),attrs,Nj.S(e,"sender").ToUpperInvariant(),(int?)Nj.N(e,"preparingDay"),Nj.S(e,"shipmentTemplate"),Nj.N(e,"vatRate"),Nj.N(e,"commissionRate"));
    }
    public async Task<N11Page<N11Order>> Orders(ConnectionSettings s,N11Query q,CancellationToken ct)
    {
        var query=Paging(q);query["sender"]=q.Sender=="N11"?"n11":q.Sender;query["orderByDirection"]="DESC";query["orderByField"]="true";
        if(q.Search is {Length:>0})query["orderNumber"]=q.Search;
        if(q.Status is {Length:>0}){if(!new[]{"Created","Picking","Shipped","Cancelled","Delivered","Unpacked","UnSupplied"}.Contains(q.Status))throw new IntegrationException("invalid_status","Geçersiz n11 sipariş durumu.",400);query["status"]=q.Status;}
        if(q.StartDate.HasValue){query["startDate"]=q.StartDate.Value.ToString(CultureInfo.InvariantCulture);query["endDate"]=q.EndDate!.Value.ToString(CultureInfo.InvariantCulture);}
        using var doc=await Get(s,"/rest/delivery/v1/shipmentPackages",query,ct);Nj.Require(doc.RootElement,"content");return Page(doc.RootElement,Nj.A(doc.RootElement,"content").Select(MapOrder).ToArray(),q);
    }
    public static N11Order MapOrder(JsonElement e)
    {
        var lines=Nj.A(e,"lines");var status=Nj.S(e,"shipmentPackageStatus");var addr=Nj.F(e,"shippingAddress");var billing=Nj.F(e,"billingAddress");var histories=Nj.A(e,"packageHistories").Select(h=>new N11History(Nj.S(h,"status").Trim(),Nj.D(h,"createdDate"))).ToArray();
        var amounts=lines.Select(l=>new N11Amount(Nj.S(l,"orderLineId"),Nj.S(l,"productName"),(int)(Nj.N(l,"quantity")??0),Nj.N(l,"price"),Nj.N(l,"sellerInvoiceAmount"),Nj.N(l,"commissionRate"),Nj.N(l,"sellerCampaignCommissionRate"))).ToArray();var invoiceTotal=amounts.Length>0&&amounts.All(l=>l.InvoiceAmount.HasValue)?amounts.Sum(l=>l.InvoiceAmount):null;
        var p=new MarketplaceOrder(Nj.S(e,"id"),Nj.S(e,"orderNumber"),status,histories.FirstOrDefault(h=>h.Status=="Created")?.Date,Nj.S(e,"customerfullName"),Nj.S(e,"customerEmail"),Nj.N(e,"totalAmount"),null,Nj.N(e,"totalDiscountAmount"),"TRY","",Nj.S(e,"deliveryAddressType"),addr.ValueKind==JsonValueKind.Object?new("","Shipment",Nj.S(addr,"countryCode"),Nj.S(addr,"city"),Nj.S(addr,"district"),Nj.S(addr,"postalCode"),Nj.S(addr,"address"),false):null,
            lines.Select(l=>new MarketplaceOrderLine(Nj.S(l,"orderLineId"),Nj.S(l,"stockCode"),Nj.S(l,"barcode"),Nj.S(l,"productName"),(int)(Nj.N(l,"quantity")??0),Nj.N(l,"price"),Nj.N(l,"sellerInvoiceAmount"),Nj.N(l,"vatRate"),"TRY",Nj.S(l,"orderItemLineItemStatusName"),"")).ToArray(),
            new(Nj.S(e,"cargoProviderName"),Nj.S(e,"cargoTrackingNumber"),Nj.S(e,"cargoTrackingLink"),Nj.S(e,"cargoSenderNumber"),Nj.S(e,"id"),null,null),new("",Nj.S(e,"invoiceLink").Length>0?"Bağlantı bildirildi":"Bildirilmedi",Nj.S(e,"invoiceLink"),Nj.N(billing,"invoiceType")==2,Nj.N(billing,"invoiceType")==2?Nj.S(e,"customerfullName"):"",Nj.S(e,"taxOffice"),Nj.S(e,"taxId"),null,[]));
        var senders=lines.Select(l=>Nj.S(l,"sender").ToUpperInvariant()).Distinct().ToArray();return new(p,senders.Length==1?senders[0]:senders.Length>1?"MIXED":"UNKNOWN",invoiceTotal,amounts,histories,Nj.D(e,"agreedDeliveryDate"),Nj.F(e,"micro").ValueKind==JsonValueKind.True);
    }
    public async Task<object> Claims(ConnectionSettings s,N11Query q,CancellationToken ct)
    {
        var status=q.Status is null or ""?"REQUESTED":q.Status;if(!new[]{"REQUESTED","CANCELLED","DENIED","PENDING","PENDED","APPROVED","MANUAL_REFUND","ALL"}.Contains(status))throw new IntegrationException("invalid_status","Geçersiz iade durumu.",400);
        XNamespace soap="http://schemas.xmlsoap.org/soap/envelope/",schema="http://www.n11.com/ws/schemas";
        var search=new XElement("searchData",new XElement("status",status),new XElement("sender",q.Sender));if(q.Search is {Length:>0})search.Add(new XElement("searchInfoType","ORDERID"),new XElement("searchQuery",q.Search));
        if(q.StartDate.HasValue)search.Add(new XElement("period",new XElement("startDate",DateTimeOffset.FromUnixTimeMilliseconds(q.StartDate.Value).ToOffset(TimeSpan.FromHours(3)).ToString("dd/MM/yyyy")),new XElement("endDate",DateTimeOffset.FromUnixTimeMilliseconds(q.EndDate!.Value).ToOffset(TimeSpan.FromHours(3)).ToString("dd/MM/yyyy"))));
        var xml=new XDocument(new XElement(soap+"Envelope",new XAttribute(XNamespace.Xmlns+"soapenv",soap),new XAttribute(XNamespace.Xmlns+"sch",schema),new XElement(soap+"Body",new XElement(schema+"ClaimReturnListRequest",new XElement("auth",new XElement("appKey",configuration["Marketplaces:N11:production:AppKey"]),new XElement("appSecret",configuration["Marketplaces:N11:production:AppSecret"])),search,new XElement("pagingData",new XElement("currentPage",q.Page))))));
        var bytes=await Send(s,"/ws/returnService/",null,new StringContent(xml.ToString(),Encoding.UTF8,"text/xml"),ct);
        using var input=new MemoryStream(bytes);using var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=16*1024*1024});var doc=XDocument.Load(reader);var response=doc.Descendants().FirstOrDefault(x=>x.Name.LocalName=="ClaimReturnListResponse")??throw new IntegrationException("invalid_response","n11 iade yanıtı okunamadı.");
        string V(XElement? node,string key)=>node?.Elements().FirstOrDefault(x=>x.Name.LocalName==key)?.Value??"";
        var result=response.Elements().FirstOrDefault(x=>x.Name.LocalName=="result");if(V(result,"status")!="success")throw new IntegrationException("claim_read_failed","n11 iade listesi alınamadı. Mağaza yetkisini kontrol edin.");
        var paging=response.Elements().FirstOrDefault(x=>x.Name.LocalName=="pagingData");var rows=response.Descendants().Where(x=>x.Name.LocalName=="claimReturn").Select(x=>new{id=V(x,"claimReturnId"),orderNumber=V(x,"orderNumber"),customerName=V(x,"buyerName"),productName=V(x,"productName"),quantity=int.TryParse(V(x,"quantity"),out var n)?n:(int?)null,reason=V(x,"returnReasonDescription"),status=V(x,"status"),sender=V(x,"sender"),claimDate=V(x,"requestDate")}).ToArray();
        return new N11Page<object>(rows,q.Page,20,long.TryParse(V(paging,"totalCount"),out var total)?total:null,int.TryParse(V(paging,"pageCount"),out var pages)?pages:null);
    }
    public async Task<object> Read(ConnectionSettings s,string kind,N11Query q,CancellationToken ct)
    {
        Credentials(s);if(q.Error() is {} error)throw new IntegrationException("invalid_query",error,400);
        if(kind is "products" or "inventory" or "depot")return await Products(s,kind=="depot"?q with{Sender="N11"}:q,ct);
        if(kind is "orders" or "shipping" or "invoices" or "finance")return await Orders(s,q,ct);
        if(kind=="claims")return await Claims(s,q,ct);
        if(kind=="history")
        {
            if(q.TaskId is null or <=0)throw new IntegrationException("task_required","Sonucu kontrol etmek için işlem numarası girin.",400);
            var body=new StringContent(JsonSerializer.Serialize(new{taskId=q.TaskId,pageable=new{page=q.Page,size=q.Size}}),Encoding.UTF8,"application/json");using var doc=JsonDocument.Parse(await Send(s,"/ms/product/task-details/page-query",null,body,ct));var skus=Nj.F(doc.RootElement,"skus");Nj.Require(skus,"content");var rows=Nj.A(skus,"content").Select(e=>new{sku=Nj.S(e,"itemCode"),status=Nj.S(e,"status"),reasons=Nj.A(e,"reasons").Select(v=>v.ToString()).ToArray()}).ToArray();return new N11Page<object>(rows,q.Page,q.Size,(long?)Nj.N(skus,"totalElements"),(int?)Nj.N(skus,"totalPages"),Nj.S(doc.RootElement,"status"));
        }
        if(kind=="catalog")
        {
            await categoryGate.WaitAsync(ct);try{if(categoriesUntil<DateTimeOffset.UtcNow){using var doc=await Get(s,"/cdn/categories",new(),ct);Nj.Require(doc.RootElement,"categories");categories=doc.RootElement.Clone();categoriesUntil=DateTimeOffset.UtcNow.AddMinutes(15);}}finally{categoryGate.Release();}
            var rows=new List<object>();void Walk(JsonElement e,string parent,int depth){if(depth>20)throw new IntegrationException("invalid_categories","Kategori ağacı okunamadı.");var name=Nj.S(e,"name");var full=parent.Length>0?parent+" / "+name:name;var children=Nj.A(e,"subCategories");if(children.Length==0&&(string.IsNullOrWhiteSpace(q.Search)||full.Contains(q.Search,StringComparison.OrdinalIgnoreCase)))rows.Add(new{id=Nj.S(e,"id"),name,path=full});foreach(var child in children)Walk(child,full,depth+1);}foreach(var e in Nj.A(categories,"categories"))Walk(e,"",0);return new N11Page<object>(rows.Skip(q.Page*q.Size).Take(q.Size).ToArray(),q.Page,q.Size,rows.Count,(int)Math.Ceiling(rows.Count/(double)q.Size));
        }
        throw new IntegrationException("not_available","Bu bölümün gerçek n11 bağlantısı henüz tamamlanmadı.",501);
    }
    public async Task<MarketplacePage<MarketplaceProduct>> ProductsAsync(ConnectionSettings s,ProductQuery q,CancellationToken ct)
    {
        if(q.Barcode is {Length:>0})throw new IntegrationException("unsupported_filter","n11 ürünlerini tam satıcı SKU'suyla arayın; barkod sorgusu desteklenmiyor.",400);var page=await Products(s,new(Search:q.StockCode,Page:q.Page,Size:q.Size),ct);return new(page.Items.Select(x=>x.Product).ToArray(),q.Page,q.Size,page.TotalElements,page.TotalPages,null);
    }
    public async Task<MarketplacePage<MarketplaceOrder>> OrdersAsync(ConnectionSettings s,OrderQuery q,CancellationToken ct)
    {
        var query=new N11Query(Search:q.OrderNumber,Status:q.Status,StartDate:q.StartDate,EndDate:q.EndDate,Page:q.Page,Size:q.Size);if(query.Error() is {} error)throw new IntegrationException("invalid_query",error,400);var page=await Orders(s,query,ct);return new(page.Items.Select(x=>x.Order with{FulfilledBy=x.Sender}).ToArray(),q.Page,q.Size,page.TotalElements,page.TotalPages,null);
    }
}
internal static class Nj
{
    public static JsonElement F(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var v)?v:default;
    public static string S(JsonElement e,string key)=>F(e,key).ValueKind is JsonValueKind.String or JsonValueKind.Number?F(e,key).ToString():"";
    public static decimal? N(JsonElement e,string key)=>decimal.TryParse(F(e,key).ToString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var n)?n:null;
    public static JsonElement[] A(JsonElement e,string key)=>F(e,key).ValueKind==JsonValueKind.Array?F(e,key).EnumerateArray().ToArray():[];
    public static void Require(JsonElement e,string key){if(F(e,key).ValueKind!=JsonValueKind.Array)throw new IntegrationException("invalid_response","n11 liste yanıtı beklenen biçimde değil.");}
    public static DateTimeOffset? D(JsonElement e,string key){var n=N(e,key);if(n is >=0 and <=253402300799999)return DateTimeOffset.FromUnixTimeMilliseconds((long)n.Value);return DateTimeOffset.TryParse(S(e,key),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var d)?d:null;}
}
