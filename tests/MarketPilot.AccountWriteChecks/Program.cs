using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Amazon;
using MarketPilot.Web.Integrations.Boyner;
using MarketPilot.Web.Integrations.Hepsiburada;
using MarketPilot.Web.Integrations.N11;
using MarketPilot.Web.Integrations.Ticimax;
using MarketPilot.Web.Catalog;
using MarketPilot.Web.Synchronization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MarketPilot.Web.Integrations.Demo;
using MarketPilot.Web.Integrations.CentralStock;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

static void Check(bool v,string message){if(!v)throw new Exception(message);}
static async Task Reject(Func<Task> action,string code){try{await action();}catch(IntegrationException e){Check(e.Code==code,$"Expected {code}, got {e.Code}");return;}throw new Exception("Accepted "+code);}
var values=new Dictionary<string,string?>{["Synchronization:AllowRealWrites"]="true",
    ["Marketplaces:Trendyol:production:SellerId"]="9999",["Marketplaces:Trendyol:production:ApiKey"]="legacy-key",["Marketplaces:Trendyol:production:ApiSecret"]="legacy-secret"};
foreach(var market in CredentialProfiles.Markets)foreach(var n in new[]{1,2})
{
    var key=$"MarketplaceAccounts:{market}{n}";
    values[key+":Marketplace"]=market;values[key+":Environment"]="production";values[key+":Label"]=$"Account {n}";values[key+":AllowWrites"]="true";
    var fields=market switch
    {
        "trendyol"=>new Dictionary<string,string>{{"SellerId",(100+n).ToString()},{"ApiKey","key-"+n},{"ApiSecret","secret-"+n}},
        "hepsiburada"=>new(){{"MerchantId",$"12345678-1234-1234-1234-123456789ab{n}"},{"Username","user-"+n},{"Password","secret-"+n},{"UserAgent","Fixture"}},
        "n11"=>new(){{"AppKey","key-"+n},{"AppSecret","secret-"+n}},
        "boyner"=>new(){{"SupplierId",(100+n).ToString()},{"ApiKey","key-"+n},{"ApiSecretKey","secret-"+n}},
        "amazon"=>new(){{"SellerId","seller-"+n},{"ClientId","client-"+n},{"ClientSecret","secret-"+n},{"RefreshToken","refresh-"+n}},
        _=>new(){{"BaseUrl",$"https://store{n}.example.com"},{"UyeKodu","secret-"+n}}
    };
    foreach(var f in fields)values[key+":Credentials:"+f.Key]=f.Value;
}
var config=new ConfigurationBuilder().AddInMemoryCollection(values).Build();
var transport=new Fixture();
using var services=new ServiceCollection().AddSingleton<IConfiguration>(config).AddSingleton<IHttpClientFactory>(transport)
    .AddSingleton<MarketplaceClients>().AddSingleton<MarketplaceStockWriter>().BuildServiceProvider();
var clients=services.GetRequiredService<MarketplaceClients>();var writer=services.GetRequiredService<MarketplaceStockWriter>();
var product=new MarketplaceProduct("42","HB-SKU","","SKU","8691000000001","Fixture","","","",200m,150m,null,5,"TRY",true,"",[],"approved",[]);
ConnectionSettings Settings(string market,int n=1)=>new("Account "+n,market,"real","production",market+n);
foreach(var market in CredentialProfiles.Markets)
{
    var one=Settings(market);var two=Settings(market,2);
    Check(CredentialProfiles.Configured(CredentialProfiles.For(config,one),market,"production"),"Missing profile configuration "+market);
    Check(CredentialProfiles.Identity(config,one)!=CredentialProfiles.Identity(config,two),"Credential identity collision "+market);
    Check(CatalogHub.Source(new("1",one,"untested",null,null,null,null,null))!=CatalogHub.Source(new("2",two,"untested",null,null,null,null,null)),"Seller source collision");
    Check(!ReferenceEquals(clients.Adapter(one),clients.Adapter(two)),"Adapters must not share account state");
    var start=transport.Requests.Count;
    await writer.Send(one,product,9,null,default);
    await writer.Send(two,product,9,null,default);
    var writes=transport.Requests.Skip(start).Where(x=>x.Mutation).ToArray();
    Check(writes.Length==2,"Wrong write count "+market);
    Check(writes[0].Account!=writes[1].Account,"Write credentials mixed "+market);
    Check(!writes[0].Body.Contains("legacy-key"),"Explicit profile inherited legacy credentials");
    if(market=="trendyol")Check(writes[0].Path.Contains("/101/")&&writes[1].Path.Contains("/102/"),"Trendyol seller path mixed");
    if(market=="hepsiburada")
    {
        var item=JsonDocument.Parse(writes[0].Body).RootElement[0];
        Check(item.GetProperty("hepsiburadaSku").GetString()=="HB-SKU"&&item.GetProperty("merchantSku").GetString()=="SKU","Hepsiburada listing ID mistaken for HB SKU");
        Check(!item.TryGetProperty("price",out _),"Stock-only upload changed price");
    }
    if(market=="n11")
    {
        var item=JsonDocument.Parse(writes[0].Body).RootElement.GetProperty("payload").GetProperty("skus")[0];
        Check(item.GetProperty("stockCode").GetString()=="SKU"&&!item.TryGetProperty("salePrice",out _),"n11 SKU/omitted price contract failed");
    }
    if(market=="boyner")
    {
        var item=JsonDocument.Parse(writes[0].Body).RootElement.GetProperty("items")[0];
        Check(item.GetProperty("salePrice").GetDecimal()==150m&&item.GetProperty("listPrice").GetDecimal()==200m,"Boyner stock-only update changed price");
    }
    if(market=="amazon")
    {
        var patch=JsonDocument.Parse(writes[0].Body).RootElement.GetProperty("patches");
        Check(patch.GetArrayLength()==1&&patch[0].GetProperty("op").GetString()=="merge","Amazon stock update must preserve other offer/fulfillment fields");
    }
    if(market=="ticimax")
    {
        var doc=XDocument.Parse(writes[0].Body);
        Check(doc.Descendants().Single(x=>x.Name.LocalName=="StokAdediGuncelle").Value=="true"&&doc.Descendants().Single(x=>x.Name.LocalName=="SatisFiyatiGuncelle").Value=="false","Ticimax flags changed unrelated fields");
        Check(doc.Descendants().Single(x=>x.Name.LocalName=="ID").Value=="42","Ticimax must address the variant ID");
    }
}
// A different seller must never reuse an Amazon refresh/access token.
Check(transport.TokenCalls==2,"Amazon token cache crossed profiles");
await clients.Amazon(Settings("amazon")).Get("production","/sellers/v1/marketplaceParticipations",new(),default);
Check(transport.TokenCalls==2,"Same account token cache was not reused");
var before=transport.Requests.Count;
await Reject(()=>Task.FromResult(CredentialProfiles.For(config,Settings("trendyol") with{CredentialProfile="missing"})),"credential_profile_mismatch");
await Reject(()=>Task.FromResult(CredentialProfiles.For(config,Settings("amazon") with{CredentialProfile="trendyol1"})),"credential_profile_mismatch");
await Reject(()=>Task.FromResult(CredentialProfiles.For(config,Settings("trendyol") with{CredentialProfile=CredentialProfiles.Unassigned})),"credential_profile_missing");
Check(transport.Requests.Count==before,"Invalid profile reached a provider");
var publicProfiles=JsonSerializer.Serialize(CredentialProfiles.List(config));
Check(!publicProfiles.Contains("secret-")&&!publicProfiles.Contains("refresh-")&&!publicProfiles.Contains("ApiSecret")&&CredentialProfiles.List(config).Length==12,"Profile metadata leaked secrets");
// Explicit write permission and account binding are enforced before any mutation.
config["MarketplaceAccounts:n111:AllowWrites"]="false";
await Reject(()=>writer.Send(Settings("n11"),product,9,null,default),"write_disabled");
config["MarketplaceAccounts:n111:AllowWrites"]="true";
var oldIdentity=CredentialProfiles.Identity(config,Settings("amazon"));
config["MarketplaceAccounts:amazon1:Credentials:SellerId"]="seller-rotated";
await Reject(()=>writer.Send(Settings("amazon"),product,9,null,default,oldIdentity),"account_changed");
config["MarketplaceAccounts:amazon1:Credentials:SellerId"]="seller-1";
transport.Depot=true;
await Reject(()=>writer.Send(Settings("n11"),product,9,null,default),"managed_inventory");
await Reject(()=>writer.Send(Settings("amazon"),product,9,null,default),"managed_inventory");
transport.Depot=false;
transport.Reject=true;
await Reject(()=>writer.Send(Settings("n11"),product,9,null,default),"write_rejected");
await Reject(()=>writer.Send(Settings("amazon"),product,9,null,default),"write_rejected");
transport.Reject=false;
foreach(var market in new[]{"n11","amazon","ticimax","boyner","hepsiburada","trendyol"})
 await Reject(()=>writer.Send(Settings(market),product,9,160m,default),"pricing_blocked");
transport.Status=HttpStatusCode.TooManyRequests;
await Reject(()=>writer.Send(Settings("hepsiburada"),product,9,null,default),"rate_limited");
var temp=Path.Combine(Path.GetTempPath(),"marketpilot-account-demo-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var env=new FixtureEnvironment{ContentRootPath=temp};var demoConfig=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>()).Build();
    var registry=new IntegrationRegistry(env);var demo=new DemoWorkspace(env);
    using var demoServices=new ServiceCollection().AddSingleton<IWebHostEnvironment>(env).AddSingleton<IConfiguration>(demoConfig).AddSingleton(registry).AddSingleton(demo)
        .AddSingleton<IHttpClientFactory>(transport).AddTransient<CentralStockSource>().AddSingleton<TimeProvider>(TimeProvider.System).AddSingleton<DemoStore>().AddSingleton<StockOutbox>().BuildServiceProvider();
    var stock=new CentralStockService(env,demoServices,demoConfig);
    await stock.RunAsync(default);
    Check(stock.LastRunSucceeded,"Demo baseline stock run failed");
    var barcode="8691000000005";demo.SimulateSale(registry.Connections["trendyol"].Settings,barcode,2);
    await stock.RunAsync(default);
    Check(stock.LastRunSucceeded,"Demo sale distribution failed");
    foreach(var n in new[]{1,2})
    {
        var real=new ConnectionState("account-"+n,Settings("trendyol",n),"untested",null,null,null,null,null);
        registry.Connections[real.Id]=real;
        config[$"Webhooks:Trendyol:Endpoints:receiver0{n}:Enabled"]="true";
        config[$"Webhooks:Trendyol:Endpoints:receiver0{n}:ConnectionId"]=real.Id;
        config[$"Webhooks:Trendyol:Endpoints:receiver0{n}:ApiKey"]="fixture-receiver-key-32-characters-2026";
    }
    var receiver=new MarketPilot.Web.Webhooks.OrderWebhookReceiver(config,registry,null!);
    Check(receiver.Binding("receiver01")?.SellerId=="101"&&receiver.Binding("receiver02")?.SellerId=="102","Webhook seller binding ignored account profiles");

    Check(demoServices.GetRequiredService<StockOutbox>().Find(JsonSerializer.SerializeToElement(demoServices.GetRequiredService<StockOutbox>().Status(),new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("recent")[0].GetProperty("id").GetString()!)!=null,"Demo distribution has no outbox record");
}
finally{if(!Path.GetFullPath(temp).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe cleanup");Directory.Delete(temp,true);}

Console.WriteLine("PASS multi-account/write fixtures: six independent seller profiles, authentication and native identities, separate Amazon OAuth caches, missing/mismatched profiles without fallback, secret-free metadata, rotation guard, stock-only price preservation, managed inventory refusal, async rejection, price payloads and 429. All network transports mocked.");

sealed record Request(string Host,string Path,string Account,string Body,bool Mutation);
sealed class Fixture:HttpMessageHandler,IHttpClientFactory
{
    public List<Request> Requests=new();public int TokenCalls;public bool Depot,Reject;public HttpStatusCode Status=HttpStatusCode.OK;
    public HttpClient CreateClient(string name)=>new(this,false);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
    {
        var uri=r.RequestUri!;var body=r.Content==null?"":await r.Content.ReadAsStringAsync(ct);
        var account=r.Headers.Authorization?.Parameter??(r.Headers.TryGetValues("appKey",out var keys)?keys.Single():r.Headers.TryGetValues("x-amz-access-token",out var token)?token.Single():uri.Host);
        if(uri.Host.StartsWith("store"))account=XDocument.Parse(body.Length>0?body:"<x/>").Descendants().FirstOrDefault(x=>x.Name.LocalName=="UyeKodu")?.Value??uri.Host;
        var mutation=r.Method==HttpMethod.Patch||r.Method==HttpMethod.Post&&uri.Host!="api.amazon.com"&&!uri.AbsolutePath.EndsWith("product-query");
        Requests.Add(new(uri.Host,uri.AbsolutePath,account,body,mutation));
        string response;
        if(uri.Host=="api.amazon.com"){TokenCalls++;response=JsonSerializer.Serialize(new{access_token=body.Contains("refresh-1")?"token-1":"token-2",expires_in=3600,token_type="bearer"});}
        else if(uri.Host.Contains("trendyol"))response="""{"batchRequestId":"batch"}""";
        else if(uri.Host.Contains("hepsiburada"))response="""{"id":"upload"}""";
        else if(uri.Host=="api.n11.com")
            response=r.Method==HttpMethod.Get?JsonSerializer.Serialize(new{content=new[]{new{n11ProductId=42,stockCode="SKU",title="Fixture",sender=Depot?"N11":"SELLER",salePrice=150,listPrice=200,quantity=5,currencyType="TL",imageUrls=Array.Empty<string>()}},totalElements=1,totalPages=1}):
                Reject?"""{"id":1,"status":"REJECT"}""":"""{"id":1,"status":"IN_QUEUE"}""";
        else if(uri.Host.Contains("boyner"))response="""{"success":true,"batchRequestId":"batch"}""";
        else if(uri.Host.StartsWith("store"))
            response=r.Method==HttpMethod.Get?Wsdl:"<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body><VaryasyonGuncelleResponse><VaryasyonGuncelleResult>1</VaryasyonGuncelleResult></VaryasyonGuncelleResponse></s:Body></s:Envelope>";
        else if(r.Method==HttpMethod.Patch)response=Reject?"""{"status":"INVALID","issues":[{"severity":"ERROR"}]}""":"""{"status":"ACCEPTED","submissionId":"submission"}""";
        else response=JsonSerializer.Serialize(new{sku="SKU",fulfillmentAvailability=new[]{new{fulfillmentChannelCode=Depot?"AMAZON_EU":"DEFAULT",quantity=5}},attributes=new{
            fulfillment_availability=new[]{new{fulfillment_channel_code="DEFAULT",quantity=5,lead_time_to_ship_max_days=3}},
            purchasable_offer=new[]{new{marketplace_id=AmazonApiClient.MarketplaceId,currency="TRY",audience="ALL",our_price=new[]{new{schedule=new[]{new{value_with_tax=150}}}}}}}});
        return new(Status){Content=new StringContent(response,Encoding.UTF8,uri.Host.StartsWith("store")?"text/xml":"application/json")};
    }
    // Minimal projection of the public Ticimax WSDL's VaryasyonGuncelle operation.
    public const string Wsdl="""
<wsdl:definitions xmlns:wsdl="http://schemas.xmlsoap.org/wsdl/" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/" xmlns:t="urn:test" xmlns:d="urn:data" targetNamespace="urn:test">
<wsdl:types><xs:schema targetNamespace="urn:data" elementFormDefault="qualified">
<xs:complexType name="Varyasyon"><xs:sequence><xs:element name="ID" type="xs:int"/><xs:element name="StokAdedi" type="xs:double"/><xs:element minOccurs="0" name="SatisFiyati" type="xs:double"/><xs:element minOccurs="0" name="IndirimliFiyati" type="xs:double"/></xs:sequence></xs:complexType>
<xs:complexType name="VaryasyonAyar"><xs:sequence><xs:element name="AktifGuncelle" type="xs:boolean"/><xs:element name="StokAdediGuncelle" type="xs:boolean"/><xs:element name="SatisFiyatiGuncelle" type="xs:boolean"/><xs:element name="IndirimliFiyatiGuncelle" type="xs:boolean"/></xs:sequence></xs:complexType>
</xs:schema><xs:schema targetNamespace="urn:test" elementFormDefault="qualified"><xs:element name="VaryasyonGuncelle"><xs:complexType><xs:sequence><xs:element name="UyeKodu" type="xs:string"/><xs:element name="urun" type="d:Varyasyon"/><xs:element name="ayar" type="d:VaryasyonAyar"/></xs:sequence></xs:complexType></xs:element></xs:schema></wsdl:types>
<wsdl:message name="Input"><wsdl:part name="parameters" element="t:VaryasyonGuncelle"/></wsdl:message><wsdl:portType name="IUrun"><wsdl:operation name="VaryasyonGuncelle"><wsdl:input message="t:Input"/></wsdl:operation></wsdl:portType>
<wsdl:binding name="Binding"><soap:binding transport="http://schemas.xmlsoap.org/soap/http"/><wsdl:operation name="VaryasyonGuncelle"><soap:operation soapAction="urn:test/IUrun/VaryasyonGuncelle"/></wsdl:operation></wsdl:binding></wsdl:definitions>
""";
}

sealed class FixtureEnvironment:IWebHostEnvironment
{
    public string ApplicationName{get;set;}="MarketPilot.Web";public string EnvironmentName{get;set;}="Production";
    public string ContentRootPath{get;set;}="";public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();
    public string WebRootPath{get;set;}="";public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
}
