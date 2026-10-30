using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketPilot.Web.Integrations;

namespace MarketPilot.Web.Enterprise;

// This is the app's versioned bridge contract, not an invented Nebim/carrier native API.
public sealed class EnterpriseBridge(IHttpClientFactory factory, IConfiguration config)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Section(string connector) => "Enterprise:Connectors:" + connector;
    public bool Configured(string connector) => Base(connector) != null && !string.IsNullOrWhiteSpace(config[Section(connector)+":ApiKey"]) && !config[Section(connector)+":ApiKey"]!.Any(char.IsControl);
    public bool Writes(string connector) => Configured(connector) && config.GetValue<bool>(Section(connector)+":AllowWrites");
    public string Identity(string connector) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((Base(connector)?.AbsoluteUri??"")+"|"+config[Section(connector)+":ApiKey"])));
    private Uri? Base(string connector)
    {
        if(!Regex.IsMatch(connector,@"^[a-zA-Z0-9_-]{1,100}$"))return null;
        if(!Uri.TryCreate(config[Section(connector)+":BaseUrl"],UriKind.Absolute,out var uri)||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0)return null;
        return uri.Scheme=="https"||uri.Scheme=="http"&&config.GetValue<bool>(Section(connector)+":AllowHttp")?uri:null;
    }
    public static bool SafeLink(string? value) => Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.UserInfo.Length==0&&uri.Fragment.Length==0;
    private Uri Url(string connector,string path)
    {
        var root=Base(connector)??throw new IntegrationException("connector_missing","Bağlantı adresi ve API anahtarı yapılandırılmadı.",409);
        if(!path.StartsWith('/')||path.StartsWith("//")||path.Contains('\\')||path.Contains("..")||path.Any(char.IsControl))throw new IntegrationException("invalid_path","Bağlantı servis yolu geçersiz.",409);
        return new Uri(root.AbsoluteUri.TrimEnd('/')+path);
    }
    private async Task<JsonElement> Send(string connector,HttpMethod method,string path,object? body,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.GetValue<int?>(Section(connector)+":TimeoutSeconds")??30,1,120)));
        try {return await SendCore(connector,method,path,body,timeout.Token);}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new IntegrationException("bridge_timeout","Harici servis zaman aşımına uğradı; önceki kayıtlar korundu. Bağlantıyı kontrol edip tekrar deneyin.",504);}
        catch(HttpRequestException){throw new IntegrationException("bridge_connection","Harici servis bağlantısı kesildi veya kurulamadı; önceki kayıtlar korundu.",502);}
        catch(JsonException){throw new IntegrationException("bridge_response","Harici servis geçerli JSON yanıtı göndermedi; önceki kayıtlar korundu.",502);}
    }
    private async Task<JsonElement> SendCore(string connector,HttpMethod method,string path,object? body,CancellationToken ct)
    {
        if(!Configured(connector))throw new IntegrationException("connector_missing","Bağlantı yapılandırması eksik.",409);
        if(method!=HttpMethod.Get&&!Writes(connector))throw new IntegrationException("write_disabled","Bu bağlantının yazma izni sunucuda kapalı.",409);
        using var request=new HttpRequestMessage(method,Url(connector,path));
        MarketPilot.Web.Local.OutboundTargets.Authorize(request,config,Section(connector));
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",config[Section(connector)+":ApiKey"]);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if(body!=null)request.Content=new StringContent(JsonSerializer.Serialize(body,Json),Encoding.UTF8,"application/json");
        using var response=await factory.CreateClient("EnterpriseBridge").SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!response.IsSuccessStatusCode)throw new IntegrationException("bridge_http_"+(int)response.StatusCode,"Harici servis isteği tamamlanamadı (HTTP "+(int)response.StatusCode+").",(int)response.StatusCode,
            (int)response.StatusCode==429?IntegrationException.ReadRetryAfter(response):null);
        const int limit=10*1024*1024;
        if(response.Content.Headers.ContentLength>limit)throw new IntegrationException("bridge_response","Harici servis yanıtı boyut sınırını aşıyor.",502);
        await using var stream=await response.Content.ReadAsStreamAsync(ct);using var bytes=new MemoryStream();var chunk=new byte[81920];int count;
        while((count=await stream.ReadAsync(chunk,ct))>0){if(bytes.Length+count>limit)throw new IntegrationException("bridge_response","Harici servis yanıtı boyut sınırını aşıyor.",502);bytes.Write(chunk,0,count);}
        using var document=JsonDocument.Parse(bytes.ToArray());return document.RootElement.Clone();
    }
    public async Task<T[]> ReadAll<T>(string connector,string resource,string warehouse,string priceList,CancellationToken ct)
    {
        var all=new List<T>();var seen=new HashSet<string>();string? cursor=null;
        for(var page=0;page<200;page++)
        {
            var query="?warehouseCode="+Uri.EscapeDataString(warehouse)+"&priceListCode="+Uri.EscapeDataString(priceList)+(cursor==null?"":"&cursor="+Uri.EscapeDataString(cursor));
            var result=await Send(connector,HttpMethod.Get,"/v1/"+resource+query,null,ct);
            if(result.ValueKind!=JsonValueKind.Object||!result.TryGetProperty("items",out var rows)||rows.ValueKind!=JsonValueKind.Array)throw new IntegrationException("bridge_contract","Harici servis beklenen liste sözleşmesini karşılamıyor.",502);
            foreach(var row in rows.EnumerateArray()){all.Add(row.Deserialize<T>(Json)??throw new JsonException());if(all.Count>200000)throw new IntegrationException("bridge_limit","Kaynak kayıt sınırı aşıldı.",502);}
            cursor=result.TryGetProperty("nextCursor",out var token)&&token.ValueKind==JsonValueKind.String?token.GetString():null;
            if(string.IsNullOrEmpty(cursor))return all.ToArray();
            if(cursor.Length>4000||!seen.Add(cursor))throw new IntegrationException("bridge_cursor","Kaynak sayfalaması ilerlemiyor; önceki kayıtlar korunuyor.",502);
        }
        throw new IntegrationException("bridge_limit","Kaynak sayfa sınırı aşıldı.",502);
    }
    public async Task<BridgeReceipt> Submit(EnterpriseJob job,CancellationToken ct)
    {
        using var document=JsonDocument.Parse(job.Payload);
        var resource=job.Kind switch {"erp-order" or "wms-order"=>"orders","cargo-order"=>"shipments","images"=>"product-images","listings"=>"products","marketplace-shipment"=>"shipment-updates","order-adjustment"=>"order-adjustments","fulfillment-split"=>"fulfillment-splits","document-submit"=>"documents","refund-submit"=>"refunds","notification"=>"notifications",_=>throw new ArgumentException("Gönderim türü geçersiz.")};
        var result=await Send(job.Connector,HttpMethod.Post,"/v1/"+resource,new {idempotencyKey=job.Id,payload=document.RootElement},ct);
        return Receipt(result,job.Id);
    }
    public async Task<BridgeReceipt> Receipt(EnterpriseJob job,CancellationToken ct)=>Receipt(await Send(job.Connector,HttpMethod.Get,"/v1/receipts/"+Uri.EscapeDataString(job.Id),null,ct),job.Id);
    private static BridgeReceipt Receipt(JsonElement result,string id)
    {
        var receipt=result.Deserialize<BridgeReceipt>(Json);
        if(receipt==null||receipt.IdempotencyKey!=id||!new[]{"completed","pending","not-found","failed"}.Contains(receipt.State)||receipt.State=="completed"&&string.IsNullOrWhiteSpace(receipt.Reference))throw new IntegrationException("receipt_invalid","Harici servisin işlem onayı doğrulanamadı.",502);
        return receipt;
    }
}
