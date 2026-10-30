using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MarketPilot.Web.Integrations.Ticimax;

// Ticimax stores use their own domains and WSDL contracts, rather than one marketplace host.
public sealed partial class TicimaxSoap(IHttpClientFactory factory,IConfiguration configuration)
{
    internal static readonly XNamespace Wsdl="http://schemas.xmlsoap.org/wsdl/",Xs="http://www.w3.org/2001/XMLSchema",SoapBinding="http://schemas.xmlsoap.org/wsdl/soap/",Envelope="http://schemas.xmlsoap.org/soap/envelope/";
    private readonly ConcurrentDictionary<string,(TicimaxContract Contract,DateTimeOffset Until)> contracts=new();
    private readonly ConcurrentDictionary<string,SemaphoreSlim> gates=new();
    public static bool PublicIp(IPAddress ip)
    {
        if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();if(IPAddress.IsLoopback(ip))return false;
        var b=ip.GetAddressBytes();if(ip.AddressFamily==AddressFamily.InterNetwork)return b[0] is not(0 or 10 or 127) && !(b[0]==169&&b[1]==254) && !(b[0]==172&&b[1] is >=16 and <=31) && !(b[0]==192&&b[1]==168) && !(b[0]==100&&b[1] is >=64 and <=127)&&!(b[0]==198&&b[1] is 18 or 19)&&!(b[0]==192&&b[1]==0)&&b[0]<224;
        return ip.AddressFamily==AddressFamily.InterNetworkV6&&(b[0]&0xe0)==0x20;
    }
    public static Uri? ValidBase(string? value)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Port!=443||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0||uri.AbsolutePath!="/"||uri.HostNameType!=UriHostNameType.Dns||!uri.Host.Contains('.')||uri.Host.EndsWith(".local",StringComparison.OrdinalIgnoreCase)||uri.Host.EndsWith(".localhost",StringComparison.OrdinalIgnoreCase))return null;
        return uri;
    }
    public static bool IsConfigured(IConfiguration c,string environment)=>environment=="production"&&ValidBase(c["Marketplaces:Ticimax:production:BaseUrl"])!=null&&!string.IsNullOrWhiteSpace(c["Marketplaces:Ticimax:production:UyeKodu"]);
    public Uri Base(ConnectionSettings s){if(!IsConfigured(configuration,s.Environment))throw new IntegrationException("configuration_missing","Ticimax mağaza adresi ve servis yetkisi henüz tanımlanmadı. Hesapsız denemek için demo mağazayı seçin.",409);return ValidBase(configuration["Marketplaces:Ticimax:production:BaseUrl"])!;}
    public static SocketsHttpHandler Handler()=>new(){AllowAutoRedirect=false,UseProxy=false,PooledConnectionLifetime=TimeSpan.FromMinutes(2),ConnectCallback=async(context,ct)=>
    {
        var addresses=await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host,ct);if(addresses.Length==0||addresses.Any(ip=>!PublicIp(ip)))throw new HttpRequestException("Store address cannot use a private network.");
        Exception? last=null;foreach(var address in addresses){var socket=new Socket(address.AddressFamily,SocketType.Stream,ProtocolType.Tcp);try{await socket.ConnectAsync(new IPEndPoint(address,context.DnsEndPoint.Port),ct);return new NetworkStream(socket,true);}catch(Exception ex){socket.Dispose();last=ex;}}throw new HttpRequestException("Store connection failed.",last);
    }};
    public static XDocument Parse(byte[] bytes){using var stream=new MemoryStream(bytes);using var reader=XmlReader.Create(stream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=16*1024*1024});return XDocument.Load(reader);}
    private async Task<byte[]> Send(HttpRequestMessage request,CancellationToken ct)
    {
        using var response=await factory.CreateClient("Ticimax").SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!response.IsSuccessStatusCode)throw new IntegrationException((int)response.StatusCode==429?"rate_limited":"ticimax_error",(int)response.StatusCode is 401 or 403?"Ticimax servis yetkisi doğrulanamadı.":"Ticimax servisine erişilemedi. Mağaza adresini ve servis yetkilerini kontrol edin.",(int)response.StatusCode is 401 or 403?409:(int)response.StatusCode==429?429:502,(int)response.StatusCode==429?IntegrationException.ReadRetryAfter(response):null);
        const int limit=16*1024*1024;if(response.Content.Headers.ContentLength>limit)throw new IntegrationException("response_large","Ticimax yanıtı boyut sınırını aştı.");await using var input=await response.Content.ReadAsStreamAsync(ct);using var output=new MemoryStream();var block=new byte[8192];int n;while((n=await input.ReadAsync(block,ct))>0){if(output.Length+n>limit)throw new IntegrationException("response_large","Ticimax yanıtı boyut sınırını aştı.");output.Write(block,0,n);}return output.ToArray();
    }
    public async Task<TicimaxContract> Contract(ConnectionSettings s,string service,CancellationToken ct)
    {
        if(service is not("UrunServis" or "SiparisServis"))throw new IntegrationException("invalid_service","Geçersiz Ticimax servisi.",400);
        var root=Base(s);var url=new Uri(root,"Servis/"+service+".svc");var key=url.AbsoluteUri;var gate=gates.GetOrAdd(key,_=>new(1,1));await gate.WaitAsync(ct);
        try
        {
            if(contracts.TryGetValue(key,out var cached)&&cached.Until>DateTimeOffset.UtcNow)return cached.Contract;
            using var request=new HttpRequestMessage(HttpMethod.Get,url+"?singleWsdl");var document=Parse(await Send(request,ct));
            var documents=new List<XDocument>{document};var seen=new HashSet<string>{request.RequestUri!.AbsoluteUri};
            for(var index=0;index<documents.Count;index++)foreach(var import in documents[index].Descendants().Where(e=>e.Name==Xs+"import"||e.Name==Xs+"include"))
            {
                var location=(string?)import.Attribute("schemaLocation");if(string.IsNullOrWhiteSpace(location))continue;var target=new Uri(url,location);
                if(target.Scheme!="https"||target.Host!=url.Host||target.Port!=443||target.AbsolutePath!=url.AbsolutePath||!target.Query.StartsWith("?xsd=",StringComparison.OrdinalIgnoreCase))throw new IntegrationException("unsupported_contract","Ticimax sözleşmesi mağaza dışı şema içeriyor; otomatik indirilmedi.",409);
                if(!seen.Add(target.AbsoluteUri))continue;if(seen.Count>12)throw new IntegrationException("unsupported_contract","Ticimax sözleşme boyutu sınırı aşıldı.",409);using var schemaRequest=new HttpRequestMessage(HttpMethod.Get,target);documents.Add(Parse(await Send(schemaRequest,ct)));
            }
            var contract=new TicimaxContract(documents,url);contracts[key]=(contract,DateTimeOffset.UtcNow.AddMinutes(30));return contract;
        }
        finally{gate.Release();}
    }
    public Task<XElement> Call(ConnectionSettings s,string service,string operation,Dictionary<string,object?> arguments,CancellationToken ct) => CallCore(s,service,operation,arguments,false,ct);
    private async Task<XElement> CallCore(ConnectionSettings s,string service,string operation,Dictionary<string,object?> arguments,bool write,CancellationToken ct)
    {
        var allowed=new[]{"SelectUrun","SelectUrunCount","SelectKategori","SelectMarka","SelectParaBirimi","SelectTedarikci","SelectSiparis","SelectSiparisOdeme","SelectSiparisUrun","SelectSiparisKargoPaket"};if(!(write && operation=="VaryasyonGuncelle" && CredentialProfiles.Writes(configuration,s)) && !allowed.Contains(operation))throw new IntegrationException("write_disabled","Ticimax mağaza değiştirme işlemleri açık değil.",403);
        var contract=await Contract(s,service,ct);var values=new Dictionary<string,object?>(arguments,StringComparer.OrdinalIgnoreCase){["UyeKodu"]=configuration["Marketplaces:Ticimax:production:UyeKodu"]};var (body,action)=contract.Request(operation,values);
        using var request=new HttpRequestMessage(HttpMethod.Post,contract.Address){Content=new StringContent(new XDocument(new XElement(Envelope+"Envelope",new XElement(Envelope+"Body",body))).ToString(),Encoding.UTF8,"text/xml")};request.Headers.TryAddWithoutValidation("SOAPAction","\""+action+"\"");var doc=Parse(await Send(request,ct));
        if(doc.Descendants(Envelope+"Fault").Any())throw new IntegrationException("soap_fault","Ticimax işlemi tamamlanamadı. Servis yetkisini ve mağaza sözleşmesini kontrol edin.",409);
        var response=doc.Descendants().FirstOrDefault(e=>e.Name.LocalName==operation+"Response");var result=response?.Elements().FirstOrDefault(e=>e.Name.LocalName==operation+"Result");if(result==null||(string?)result.Attribute(XName.Get("nil","http://www.w3.org/2001/XMLSchema-instance"))=="true")throw new IntegrationException("invalid_response","Ticimax yanıtı beklenen biçimde değil.");return result;
    }
}
public sealed class TicimaxContract
{
    private readonly Dictionary<XName,XElement> elements=new(),types=new();private readonly XDocument wsdl;public Uri Address{get;}
    public TicimaxContract(IEnumerable<XDocument> documents,Uri address)
    {
        Address=address;wsdl=documents.First();foreach(var schema in documents.SelectMany(d=>d.DescendantsAndSelf()).Where(e=>e.Name==TicimaxSoap.Xs+"schema"))
        {var ns=(string?)schema.Attribute("targetNamespace")??"";foreach(var e in schema.Elements(TicimaxSoap.Xs+"element"))elements[XName.Get((string)e.Attribute("name")!,ns)]=e;foreach(var e in schema.Elements(TicimaxSoap.Xs+"complexType"))types[XName.Get((string)e.Attribute("name")!,ns)]=e;}
    }
    private static XName QName(XElement e,string value){var parts=value.Split(':',2);return parts.Length==2?XName.Get(parts[1],e.GetNamespaceOfPrefix(parts[0])?.NamespaceName??""):XName.Get(value,e.GetDefaultNamespace().NamespaceName);}
    private XElement Type(XElement element){var inline=element.Element(TicimaxSoap.Xs+"complexType");if(inline!=null)return inline;var type=(string?)element.Attribute("type");return type!=null&&types.TryGetValue(QName(element,type),out var found)?found:new XElement("primitive");}
    private IEnumerable<XElement> Fields(XElement type,int depth=0)
    {
        if(depth>10)throw new IntegrationException("unsupported_contract","Ticimax sözleşme tipi çok derin.",409);var extension=type.Element(TicimaxSoap.Xs+"complexContent")?.Element(TicimaxSoap.Xs+"extension");if(extension?.Attribute("base") is {} b&&types.TryGetValue(QName(extension,b.Value),out var parent))foreach(var field in Fields(parent,depth+1))yield return field;
        var sequence=(extension??type).Element(TicimaxSoap.Xs+"sequence");if(sequence!=null)foreach(var f in sequence.Elements(TicimaxSoap.Xs+"element"))yield return f;
    }
    private XElement Wrapper(string operation)
    {
        var node=wsdl.Descendants(TicimaxSoap.Wsdl+"portType").Elements(TicimaxSoap.Wsdl+"operation").FirstOrDefault(e=>(string?)e.Attribute("name")==operation)??throw new IntegrationException("operation_unavailable","Bu okuma işlemi mağazanın Ticimax servisinde bulunamadı.",409);
        var input=node.Element(TicimaxSoap.Wsdl+"input")!;var message=QName(input,(string)input.Attribute("message")!);var part=wsdl.Root!.Elements(TicimaxSoap.Wsdl+"message").First(e=>(string?)e.Attribute("name")==message.LocalName).Element(TicimaxSoap.Wsdl+"part")!;
        return elements[QName(part,(string)part.Attribute("element")!)];
    }
    public Dictionary<string,string> ParameterFields(string operation,string parameter)
    {
        var argument=Fields(Type(Wrapper(operation))).SingleOrDefault(f=>string.Equals((string?)f.Attribute("name"),parameter,StringComparison.OrdinalIgnoreCase))
            ??throw new IntegrationException("unsupported_contract","Ticimax yazma parametresi sözleşmede bulunamadı.",409);
        return Fields(Type(argument)).ToDictionary(f=>(string)f.Attribute("name")!,f=>((string?)f.Attribute("type")??"").Split(':').Last(),StringComparer.OrdinalIgnoreCase);
    }
    public void RequirePaging(string operation){var paging=Fields(Type(Wrapper(operation))).FirstOrDefault(f=>((string?)f.Attribute("type")??"").Contains("Sayfalama",StringComparison.OrdinalIgnoreCase));var names=paging==null?[]:Fields(Type(paging)).Select(f=>(string?)f.Attribute("name")).ToArray();if(!names.Contains("BaslangicIndex")||!names.Contains("KayitSayisi"))throw new IntegrationException("unsupported_contract","Mağazanın sayfalama sözleşmesi desteklenmiyor.",409);}
    public HashSet<string> FilterFields(string operation)
    {
        var fields=Fields(Type(Wrapper(operation)));var filter=fields.FirstOrDefault(f=>((string?)f.Attribute("type")??"").Contains("Filtre",StringComparison.OrdinalIgnoreCase));return filter==null?new(StringComparer.OrdinalIgnoreCase):Fields(Type(filter)).Select(e=>(string)e.Attribute("name")!).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    public (XElement Body,string Action) Request(string operation,Dictionary<string,object?> values)
    {
        var wrapper=Wrapper(operation);var schema=wrapper.Ancestors(TicimaxSoap.Xs+"schema").First();var name=XName.Get((string)wrapper.Attribute("name")!, (string?)schema.Attribute("targetNamespace")??"");var body=Serialize(wrapper,name,values,0);
        var binding=wsdl.Descendants(TicimaxSoap.Wsdl+"binding").FirstOrDefault(e=>e.Element(TicimaxSoap.SoapBinding+"binding")!=null);var action=(string?)binding?.Elements(TicimaxSoap.Wsdl+"operation").FirstOrDefault(e=>(string?)e.Attribute("name")==operation)?.Element(TicimaxSoap.SoapBinding+"operation")?.Attribute("soapAction");if(string.IsNullOrWhiteSpace(action)||action.Contains('"')||action.Any(char.IsControl)||!(action.EndsWith("/"+operation,StringComparison.Ordinal)||action==operation))throw new IntegrationException("unsupported_contract","Ticimax SOAP işlem tanımı doğrulanamadı.",409);return(body,action);
    }
    private XElement Serialize(XElement definition,XName name,object? value,int depth)
    {
        if(depth>12)throw new IntegrationException("unsupported_contract","Ticimax sözleşme sınırı aşıldı.",409);var node=new XElement(name);if(value is not Dictionary<string,object?> data){node.Value=value switch{bool b=>b?"true":"false",IFormattable f=>f.ToString(null,System.Globalization.CultureInfo.InvariantCulture),_=>value?.ToString()??""};return node;}
        var fields=Fields(Type(definition)).ToArray();if(fields.Length==0&&data.Count>0)throw new IntegrationException("unsupported_contract","Ticimax alan tipi desteklenmiyor.",409);if(depth==0)foreach(var key in data.Keys){if(!fields.Any(f=>string.Equals((string?)f.Attribute("name"),key,StringComparison.OrdinalIgnoreCase)||(key=="$filter"&&((string?)f.Attribute("type")??"").Contains("Filtre",StringComparison.OrdinalIgnoreCase))||(key=="$paging"&&((string?)f.Attribute("type")??"").Contains("Sayfalama",StringComparison.OrdinalIgnoreCase))))throw new IntegrationException("unsupported_contract","Ticimax işlem parametreleri sözleşmeyle uyuşmuyor.",409);}foreach(var field in fields)
        {
            var fieldName=(string?)field.Attribute("name");if(fieldName==null)throw new IntegrationException("unsupported_contract","Ticimax şema alanı desteklenmiyor.",409);
            var match=data.FirstOrDefault(p=>p.Key.Equals(fieldName,StringComparison.OrdinalIgnoreCase));
            if(match.Key==null)
            {
                var type=(string?)field.Attribute("type")??"";var alias=type.Contains("Filtre",StringComparison.OrdinalIgnoreCase)?"$filter":type.Contains("Sayfalama",StringComparison.OrdinalIgnoreCase)?"$paging":null;if(alias!=null&&data.TryGetValue(alias,out var aliasValue))match=new(fieldName,aliasValue);
            }
            if(match.Key==null){if((string?)field.Attribute("minOccurs")=="0")continue;throw new IntegrationException("unsupported_contract","Ticimax servisinde gerekli bir alan tanınmadı; mağaza sözleşmesi incelenmeli.",409);}
            var schema=field.Ancestors(TicimaxSoap.Xs+"schema").First();var qualified=((string?)field.Attribute("form")??(string?)schema.Attribute("elementFormDefault"))=="qualified";node.Add(Serialize(field,XName.Get(fieldName,qualified?(string?)schema.Attribute("targetNamespace")??"":""),match.Value,depth+1));
        }
        return node;
    }
}
internal static class XmlExtensions{public static IEnumerable<XElement> DescendantsAndSelf(this XDocument doc)=>doc.Root?.DescendantsAndSelf()??[];}
