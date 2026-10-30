using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MarketPilot.Web.Enterprise;
using MarketPilot.Web.Integrations;

int checks=0;
void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
var order=new MarketplaceOrder("PACKAGE1","ORDER1","Created",DateTimeOffset.UnixEpoch,"MOCK","",180,200,20,"TRY","","",null,
    [new("L1","SKU1","BC1","MOCK item",2,100,200,10,"TRY","Created","")],new("MOCK carrier","TRACK1","","","SHIP1",null,2),
    new("","","",false,"","","",null,[]),1,Promotions:[new("CAMPAIGN1","MOCK campaign",20,"TRY")]);
var product=new MarketplaceProduct("LISTING1","CONTENT1","MODEL1","SKU1","BC1","MOCK item","","","",100,100,null,0,"TRY",true,"",[],"approved",[],VatRate:10,VatIncluded:true);
ErpVariantLink[] links=[new("SKU1","BC1","ERP1","ERP1-S")];
using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
using var cancel=new CancellationTokenSource();
var server=Task.Run(async()=>
{
    while(!cancel.IsCancellationRequested)
    {
        TcpClient client;
        try{client=await listener.AcceptTcpClientAsync(cancel.Token);}catch(OperationCanceledException){break;}catch(SocketException){break;}
        using var connection=client;using var stream=client.GetStream();using var reader=new StreamReader(stream,leaveOpen:true);
        var requestLine=await reader.ReadLineAsync()??throw new Exception("Empty request");
        while(!string.IsNullOrEmpty(await reader.ReadLineAsync())){}
        var query=new Uri("http://localhost"+requestLine.Split(' ')[1]).Query.TrimStart('?').Split('&').Select(x=>x.Split('=',2)).ToDictionary(x=>x[0],x=>x[1]);
        var channel=query["channel"];
        if(query["fail"]=="true") {await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 429 Too Many Requests\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));continue;}
        var native=ChannelContracts.For(channel).Capabilities.OrderStatuses.First().Key;
        var page=new MarketplacePage<MarketplaceOrder>([order with{Status=native}],2,50,200,4,"TOKEN-3",true,"CURSOR-3");
        var bytes=Encoding.UTF8.GetBytes(requestLine.Contains("/products?",StringComparison.Ordinal)
            ? JsonSerializer.Serialize(new MarketplacePage<MarketplaceProduct>([product],1,20,100,5,"PRODUCT-TOKEN",true,"PRODUCT-CURSOR"),json)
            : JsonSerializer.Serialize(page,json));
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));await stream.WriteAsync(bytes);
    }
});
try
{
    using var http=new HttpClient{BaseAddress=new Uri($"http://127.0.0.1:{port}/")};
    foreach(var matrix in ChannelContracts.All)
    {
        var mapper=ChannelContracts.For(matrix.Channel);
        Check(matrix.UnsupportedOperations.Contains("create-product"),"Product creation unexpectedly supported");
        try{mapper.Require("create-product");throw new Exception("Fake product creation success");}catch(IntegrationException e){Check(e.Status==501,"Unsupported code");}
        var p=mapper.Product("account",product,links);
        Check(p.ErpVariantCode=="ERP1-S"&&p.ListingId=="LISTING1"&&p.Stock==0&&p.VatRate==10&&p.VatIncluded==true,"Product identity/zero/tax loss");
        Check(mapper.Product("account",product with{Id=""},links).State=="review-required","Missing existing listing accepted");
        Check(mapper.Product("account",product,[]).ErpVariantCode==null,"Unmapped variant invented");
        Check(mapper.Product("account",product,[new("SKU1","BC1","","")]).State=="review-required","Empty ERP codes accepted");
        Check(mapper.Product("account",product,[..links,..links]).ErpVariantCode==null,"Ambiguous identity silently chosen");
        Check(mapper.Product("account",product with{SalePrice=-1},links).State=="review-required","Negative price accepted");
        Check(mapper.Product("account",product with{VatRate=101},links).State=="review-required","Invalid VAT accepted");
        Check(mapper.Product("account",product with{Currency="",VatRate=null,VatIncluded=null,SalePrice=null},links).Currency==null,"Unknown fields invented");
        var nativeStatus=matrix.OrderStatuses.First().Key;
        var o=mapper.Order("tenant","account",order with{Status=nativeStatus},links);
        Check(o.SourceOrderId=="PACKAGE1"&&o.PackageId=="PACKAGE1"&&o.Lines[0].ErpProductCode=="ERP1"&&o.AccountId=="account","Order identity loss");
        Check(o.Discount==20&&o.Original.Promotions![0].Id=="CAMPAIGN1"&&o.TrackingNumber=="TRACK1","Discount/campaign/tracking loss");
        Check(mapper.Order("tenant","account",order with{Status="FUTURE_STATUS"},links).CanonicalStatus==null,"Unknown status inferred");
        Check(mapper.Order("tenant","account",order with{Status=null!},links).CanonicalStatus==null,"Missing status inferred");
        Check(mapper.Order("tenant","account",order with{Lines=[order.Lines[0] with{Quantity=-1}]},links).ReviewReasons.Length>0,"Negative order quantity accepted");
        Check(mapper.Order("tenant","account",order with{Lines=[order.Lines[0],order.Lines[0]]},links).ReviewReasons.Length>0,"Duplicate line identity accepted");
        Check(mapper.Order("tenant","account",order with{PackageId=""},links).SourceOrderId=="ORDER1","Fabricated package ID");
        var packages=mapper.Order("tenant","account",order with{Packages=[new("P1","MOCK","T1","FUTURE_STATUS"),new("P2","MOCK","T2",null)]},links).Packages!;
        Check(packages.Length==2&&packages[0].CanonicalStatus==null&&packages[1].NativeStatus==null,"Packages dropped or status invented");
        Check(!mapper.Receipt("accepted","REF",null).Processed&&mapper.Receipt("accepted","REF",null).Accepted,"Acceptance treated as processing");
        Check(mapper.Receipt("completed","REF",true).Processed&&!mapper.Receipt("completed",null,true).Processed,"Missing processing proof accepted");
        foreach(var (native,canonical) in matrix.OrderStatuses)
            Check(mapper.Order("tenant","account",order with{Status=native},links).CanonicalStatus==canonical,"Status mapping lost");
        Check(mapper.Error(new("native_error","do not copy secret body",429)).Retryable,"429 not retryable");
        Check(mapper.Error(new("native_auth","secret",401)).RequiresReview,"Auth error not reviewable");
        Check(mapper.Error(new("native_unsupported","secret",501)).NativeCode=="native_unsupported","Native error code lost");
        Check(!mapper.Error(new("native_unsupported","secret",501)).Retryable,"Unsupported operation was retried");
        Check(!JsonSerializer.Serialize(mapper.Error(new("native_error","secret-body",500))).Contains("secret-body"),"Raw error body leaked");
        Check(mapper.Error(new("native_unavailable","secret",503)).Retryable,"Temporary service error was not retryable");
        var adapter=new LoopbackFixtureAdapter(http,matrix.Channel);
        var settings=new ConnectionSettings("Fixture",matrix.Channel,"demo","stage");
        var query=new OrderQuery(Page:2,StartDate:1,EndDate:2,NextCursor:"CURSOR-2");
        var productQuery=new ProductQuery(Page:1,Size:20,NextPageToken:"PRODUCT-PREVIOUS");
        var productPage=await mapper.ReadProductsAsync(adapter,settings,productQuery,"tenant","actual-account-id",links,default);
        Check(productPage.NextPageToken=="PRODUCT-TOKEN"&&productPage.NextCursor=="PRODUCT-CURSOR"&&productPage.TotalPages==5&&productPage.HasMore==true,"Product pagination discarded");
        Check(productPage.Items[0].TenantId=="tenant"&&productPage.Items[0].AccountId=="actual-account-id"&&productPage.Items[0].ProductMainId=="MODEL1"&&adapter.ProductQuery==productQuery,"Product identity/query lost");
        var page=await mapper.ReadOrdersAsync(adapter,settings,query,"tenant","actual-account-id",links,default);
        Check(page.NextCursor=="CURSOR-3"&&page.NextPageToken=="TOKEN-3"&&page.HasMore==true&&page.TotalPages==4,"Pagination discarded");
        Check(page.Items[0].AccountId=="actual-account-id"&&adapter.Query==query,"Account identity/date filter changed");
        adapter.Fail=true;
        try{await mapper.ReadProductsAsync(adapter,settings,productQuery,"tenant","account",links,default);throw new Exception("Fake product success on HTTP 429");}
        catch(IntegrationException e){Check(e.Status==429&&mapper.Error(e).Retryable,"Product HTTP error lost");}
        try{await mapper.ReadOrdersAsync(adapter,settings,query,"tenant","account",links,default);throw new Exception("Fake success on HTTP 429");}
        catch(IntegrationException e){Check(e.Status==429&&mapper.Error(e).Retryable,"HTTP error lost");}
    }
    Check(ChannelContracts.For("amazon").Order("tenant","account",order with{FulfilledBy="AMAZON"},links).ChannelManagedStock,"FBA stock not recognized");
    Check(ChannelContracts.For("n11").Order("tenant","account",order with{FulfilledBy="N11"},links).ChannelManagedStock,"N11 depot not recognized");
    Check(ChannelContracts.For("amazon").Product("account",product with{FulfilledBy="AMAZON"},links).ChannelManagedStock,"FBA product stock not recognized");
    Check(ChannelContracts.For("amazon").Product("account",product with{FulfilledBy=" fba "},links).ChannelManagedStock,"FBA normalized mode not protected");
    Check(ChannelContracts.For("n11").Product("account",product with{FulfilledBy="n11depom"},links).ChannelManagedStock,"n11depom product not protected");
    foreach(var channel in new[]{"amazon","n11"})
    {
        var mapper=ChannelContracts.For(channel);
        try{mapper.RequireMerchantStock(mapper.Product("account",product with{FulfilledBy=channel=="amazon"?"FBA":"n11depom"},links));throw new Exception("Managed stock writable");}
        catch(IntegrationException error){Check(error.Code=="channel_managed_stock","Managed stock guard lost");}
    }
    Console.WriteLine($"PASS {checks} six-channel contract checks: ERP listing links, SKU/barcode/variant, VAT/currency, raw status/discount/campaign/tracking preservation, managed stock, unsupported operations, loopback fake HTTP server, paging/cursor/date/account and native error codes. No live API.");
}
finally{cancel.Cancel();listener.Stop();await server;}
sealed class LoopbackFixtureAdapter(HttpClient http,string channel):IMarketplaceAdapter
{
    public string Marketplace=>channel;
    public bool Fail{get;set;}
    public OrderQuery? Query{get;private set;}
    public ProductQuery? ProductQuery{get;private set;}
    public Task<MarketplaceAccount> TestAsync(ConnectionSettings s,CancellationToken ct)=>throw new NotSupportedException();
    public Task<MarketplaceAccount> AccountAsync(ConnectionSettings s,CancellationToken ct)=>throw new NotSupportedException();
    public async Task<MarketplacePage<MarketplaceProduct>> ProductsAsync(ConnectionSettings s,ProductQuery q,CancellationToken ct)
    {
        ProductQuery=q;using var response=await http.GetAsync("products?channel="+channel+"&fail="+(Fail?"true":"false"),ct);
        if(!response.IsSuccessStatusCode)throw new IntegrationException("fixture_http_error","Sanitized fixture failure",(int)response.StatusCode);
        return await response.Content.ReadFromJsonAsync<MarketplacePage<MarketplaceProduct>>(cancellationToken:ct)??throw new Exception("Missing product fixture");
    }
    public async Task<MarketplacePage<MarketplaceOrder>> OrdersAsync(ConnectionSettings s,OrderQuery q,CancellationToken ct)
    {
        Query=q;using var response=await http.GetAsync("orders?channel="+channel+"&fail="+(Fail?"true":"false"),ct);
        if(!response.IsSuccessStatusCode)throw new IntegrationException("fixture_http_error","Sanitized fixture failure",(int)response.StatusCode);
        return await response.Content.ReadFromJsonAsync<MarketplacePage<MarketplaceOrder>>(cancellationToken:ct)??throw new Exception("Missing fixture");
    }
}
