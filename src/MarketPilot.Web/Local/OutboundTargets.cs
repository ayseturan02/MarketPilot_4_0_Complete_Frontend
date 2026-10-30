using System.Net;
using System.Net.Sockets;
using MarketPilot.Web.Integrations;

namespace MarketPilot.Web.Local;

public static class OutboundTargets
{
    private static readonly HttpRequestOptionsKey<string[]> PrivateAddresses = new("MarketPilot.ApprovedPrivateAddresses");
    public static bool PublicAddress(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();
        var bytes=address.GetAddressBytes();
        if(address.AddressFamily==AddressFamily.InterNetwork)
            return bytes[0] is not (0 or 10 or 127) && bytes[0]<224 &&
                !(bytes[0]==169&&bytes[1]==254) && !(bytes[0]==172&&bytes[1] is >=16 and <=31) &&
                !(bytes[0]==192&&bytes[1] is 0 or 168) && !(bytes[0]==100&&bytes[1] is >=64 and <=127) &&
                !(bytes[0]==198&&bytes[1] is 18 or 19) && !(bytes[0]==198&&bytes[1]==51&&bytes[2]==100) &&
                !(bytes[0]==203&&bytes[1]==0&&bytes[2]==113);
        return address.AddressFamily==AddressFamily.InterNetworkV6 && (bytes[0]&0xe0)==0x20 &&
            !(bytes[0]==0x20&&bytes[1]==0x02) && // 6to4 can tunnel to an otherwise blocked IPv4 target.
            !(bytes[0]==0x20&&bytes[1]==0x01&&bytes[2]==0&&bytes[3]==0) && // Teredo.
            !(bytes[0]==0x20&&bytes[1]==0x01&&bytes[2]==0x0d&&bytes[3]==0xb8);
    }
    public static bool AddressAllowed(IPAddress address,IEnumerable<string> approved)
    {
        if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();
        // Cloud metadata and unspecified/multicast destinations are never an integration target.
        var bytes=address.GetAddressBytes();
        if(address.Equals(IPAddress.Any)||address.Equals(IPAddress.IPv6Any)||address.Equals(IPAddress.Parse("fd00:ec2::254"))||address.IsIPv6Multicast||
            address.AddressFamily==AddressFamily.InterNetwork&&(bytes[0]>=224||bytes[0]==169&&bytes[1]==254))return false;
        return PublicAddress(address)||approved.Any(value=>IPAddress.TryParse(value,out var allowed)&&
            (allowed.IsIPv4MappedToIPv6?allowed.MapToIPv4():allowed).Equals(address));
    }
    public static void Authorize(HttpRequestMessage request,IConfiguration config,string section)
    {
        if(LocalAccess.IsPublicDeployment(config)&&!config.GetValue<bool>("IntegrationSecurity:AllowRealConnections"))
            throw new IntegrationException("real_connections_disabled","Gerçek bağlantılar sunucuda kapalı.",409);
        var uri=request.RequestUri;
        if(uri is null||!uri.IsAbsoluteUri||uri.UserInfo.Length>0||uri.Fragment.Length>0||
            uri.Scheme!="https"&&!(uri.Scheme=="http"&&!LocalAccess.IsPublicDeployment(config)&&config.GetValue<bool>(section+":AllowHttp")))
            throw new IntegrationException("outbound_target_denied","Dış servis hedefi doğrulanamadı.",409);
        if(!Uri.TryCreate(config[section+":BaseUrl"],UriKind.Absolute,out var root)||
            root.Scheme!=uri.Scheme||root.IdnHost!=uri.IdnHost||root.Port!=uri.Port)
            throw new IntegrationException("outbound_target_denied","Dış servis hedefi onaylı bağlantıyla uyuşmuyor.",409);
        var approved=config.GetSection(section+":AllowedPrivateAddresses").GetChildren().Select(child=>child.Value??"").ToArray();
        if(IPAddress.TryParse(uri.IdnHost.Trim('[',']'),out var address)&&!AddressAllowed(address,approved))
            throw new IntegrationException("outbound_target_denied","Özel ağ hedefi açıkça onaylanmadı.",409);
        request.Options.Set(PrivateAddresses,approved);
    }
    public static SocketsHttpHandler Handler()=>new()
    {
        AllowAutoRedirect=false,UseProxy=false,ConnectTimeout=TimeSpan.FromSeconds(10),MaxResponseHeadersLength=32,MaxConnectionsPerServer=8,
        // Revalidate DNS on every bridge request, including after a profile/policy change.
        PooledConnectionLifetime=TimeSpan.Zero,
        ConnectCallback=async(context,ct)=>
        {
            if(!context.InitialRequestMessage.Options.TryGetValue(PrivateAddresses,out var approved))
                throw new HttpRequestException("Outbound request has no target policy.");
            var addresses=await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host,ct);
            if(addresses.Length==0||addresses.Any(address=>!AddressAllowed(address,approved)))
                throw new HttpRequestException("Outbound address is not approved.");
            foreach(var address in addresses)
            {
                var socket=new Socket(address.AddressFamily,SocketType.Stream,ProtocolType.Tcp);
                try{await socket.ConnectAsync(new IPEndPoint(address,context.DnsEndPoint.Port),ct);return new NetworkStream(socket,true);}
                catch{socket.Dispose();ct.ThrowIfCancellationRequested();}
            }
            throw new HttpRequestException("Approved service connection failed.");
        }
    };
}
