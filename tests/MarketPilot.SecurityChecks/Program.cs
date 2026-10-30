using System.Net;
using System.Net.Sockets;
using System.Text;
using MarketPilot.Web.Local;
using MarketPilot.Web.Integrations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

void Check(bool ok,string reason){if(!ok)throw new Exception(reason);}
void Reject(Action action){try{action();}catch(IntegrationException){return;}throw new Exception("Unsafe target/profile accepted");}
var root=Path.Combine(Path.GetTempPath(),"marketpilot-security-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try
{
    foreach(var ip in new[]{"127.0.0.1","10.0.0.1","172.16.0.1","192.168.1.1","169.254.169.254","100.64.0.1","0.0.0.0","224.0.0.1","::1","::ffff:127.0.0.1","fc00::1","fe80::1","2002:7f00:1::"})
        Check(!OutboundTargets.AddressAllowed(IPAddress.Parse(ip),[]),"Unapproved private/reserved IP accepted: "+ip);
    Check(OutboundTargets.AddressAllowed(IPAddress.Parse("127.0.0.1"),["127.0.0.1"]),"Explicit local fixture target denied");
    Check(!OutboundTargets.AddressAllowed(IPAddress.Parse("127.0.0.2"),["127.0.0.1"]),"Private approval widened to another address");
    Check(!OutboundTargets.AddressAllowed(IPAddress.Parse("169.254.169.254"),["169.254.169.254"]),"Metadata approval bypassed deny rule");
    Check(!OutboundTargets.AddressAllowed(IPAddress.Parse("fd00:ec2::254"),["fd00:ec2::254"]),"IPv6 metadata approval bypassed deny rule");
    Check(OutboundTargets.PublicAddress(IPAddress.Parse("8.8.8.8")),"Public address classified incorrectly");
    var values=new Dictionary<string,string?>{["Bridge:BaseUrl"]="http://127.0.0.1:3456",["Bridge:AllowHttp"]="true"};
    IConfiguration Config()=>new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    using(var request=new HttpRequestMessage(HttpMethod.Get,"http://127.0.0.1:3456/read"))Reject(()=>OutboundTargets.Authorize(request,Config(),"Bridge"));
    values["Bridge:AllowedPrivateAddresses:0"]="127.0.0.1";
    foreach(var target in new[]{"file:///tmp/secret","http://user:password@127.0.0.1:3456/read","http://127.0.0.2:3456/read","http://127.0.0.1:3457/read"})
    {using var request=new HttpRequestMessage(HttpMethod.Get,target);Reject(()=>OutboundTargets.Authorize(request,Config(),"Bridge"));}
    values["Deployment:Mode"]="public";
    values["MarketplaceAccounts:fixture:Marketplace"]="trendyol";
    values["MarketplaceAccounts:fixture:Environment"]="production";
    values["MarketplaceAccounts:fixture:Credentials:ApiKey"]="mock-profile-key";
    var publicProfiles=System.Text.Json.JsonSerializer.Serialize(CredentialProfiles.List(Config()));
    Check(publicProfiles.Contains("\"configured\":false")&&!publicProfiles.Contains("mock-profile-key"),"Disabled production profile leaked or failed metadata response");
    using(var request=new HttpRequestMessage(HttpMethod.Get,"http://127.0.0.1:3456/read"))Reject(()=>OutboundTargets.Authorize(request,Config(),"Bridge"));
    Reject(()=>CredentialProfiles.For(Config(),new("fixture","trendyol","real","production")));
    values["IntegrationSecurity:AllowRealConnections"]="true";
    using(var request=new HttpRequestMessage(HttpMethod.Get,"http://127.0.0.1:3456/read"))Reject(()=>OutboundTargets.Authorize(request,Config(),"Bridge"));
    values["Deployment:Mode"]="local";

    using(var listener=new TcpListener(IPAddress.Loopback,0))
    {
        listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
        values["Bridge:BaseUrl"]="http://127.0.0.1:"+port;
        var server=Task.Run(async()=>{using var socket=await listener.AcceptTcpClientAsync();await using var stream=socket.GetStream();using var reader=new StreamReader(stream,leaveOpen:true);while(!string.IsNullOrEmpty(await reader.ReadLineAsync())){}await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/latest/meta-data/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));});
        using var http=new HttpClient(OutboundTargets.Handler()){Timeout=TimeSpan.FromSeconds(5)};
        using var request=new HttpRequestMessage(HttpMethod.Get,values["Bridge:BaseUrl"]+"/read");
        OutboundTargets.Authorize(request,Config(),"Bridge");
        using var response=await http.SendAsync(request);
        Check(response.StatusCode==HttpStatusCode.Found,"Redirect was followed");await server;
    }
    const string secret="fixture-secret-not-for-storage";
    var masking=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Enterprise:Connectors:erp:ApiKey"]=secret}).Build();
    var message=SecurityHardening.Mask(secret+" Bearer fixture-token email=buyer@example.invalid token=other-token phone=05551234567\r\nforged-line",masking);
    Check(!message.Contains(secret)&&!message.Contains("fixture-token")&&!message.Contains("buyer@example.invalid")&&!message.Contains("other-token")&&!message.Contains("05551234567")&&!message.Contains('\n'),"Secret/PII/log injection not masked");
    var audit=new LocalAudit(new TestEnvironment(root),masking);audit.Add("info","fixture",secret+" buyer@example.invalid");
    Check(!File.ReadAllText(Path.Combine(root,"Data","system-events.json")).Contains(secret),"Audit persisted a secret");
    File.WriteAllText(Path.Combine(root,"appsettings.json"),"{\"Enterprise\":{\"Connectors\":{\"erp\":{\"ApiKey\":\"fixture-secret-in-json\"}}}}");
    var builder=WebApplication.CreateBuilder(new WebApplicationOptions{ContentRootPath=root,EnvironmentName="Production"});
    try{builder.ConfigureSecurity();throw new Exception("JSON secret accepted");}catch(InvalidOperationException error){Check(!error.Message.Contains("fixture-secret-in-json"),"Startup error leaked JSON secret");}
    Console.WriteLine("PASS security: SSRF/private/mapped/metadata/URL checks, exact private target approvals, production real/TLS guards, redirect refusal, DNS-pinned handler, secret/PII/log masking, audit storage and JSON credential rejection. Loopback fixtures only.");
}
finally
{
    if(Path.GetDirectoryName(Path.GetFullPath(root))!=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)||!Path.GetFileName(root).StartsWith("marketpilot-security-"))throw new Exception("Unsafe cleanup");
    Directory.Delete(root,true);
}
sealed class TestEnvironment(string root):IWebHostEnvironment
{
    public string EnvironmentName{get;set;}="Production";public string ApplicationName{get;set;}="SecurityChecks";
    public string ContentRootPath{get;set;}=root;public string WebRootPath{get;set;}=root;
    public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
}
