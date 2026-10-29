using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Local;
using Microsoft.AspNetCore.RateLimiting;

namespace MarketPilot.Web.Enterprise;
public static class EnterpriseEndpoints
{
    public static void AddEnterprise(this IServiceCollection services)
    {
        services.AddHttpClient("EnterpriseBridge",c=>c.Timeout=Timeout.InfiniteTimeSpan).ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false});
        services.AddSingleton<EnterpriseBridge>();services.AddSingleton<IErpCatalogProvider,NebimBridgeProvider>();services.AddSingleton<IWmsProvider,WmsBridgeProvider>();services.AddSingleton<ICargoProvider,CargoBridgeProvider>();services.AddSingleton<ErpCatalog>();services.AddSingleton<EnterpriseSync>();services.AddHostedService<EnterpriseSyncWorker>();
        services.Configure<RateLimiterOptions>(o=>o.AddPolicy("erp-inbound",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=120,Window=TimeSpan.FromMinutes(1),QueueLimit=0})));
    }
    public static void MapEnterprise(this WebApplication app)
    {
        var group=app.MapGroup("/api/sync/enterprise");
        group.AddEndpointFilter(async(context,next)=>{
            if(context.HttpContext.Request.Method!="GET"&&context.HttpContext.RequestServices.GetRequiredService<LocalAccountStore>().Configured&&!context.HttpContext.User.IsInRole("admin"))return Results.StatusCode(403);
            try{return await next(context);}catch(IntegrationException e){return Results.Json(new{error=e.Message},statusCode:e.Status is >=400 and <=599?e.Status:502);}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}catch(IOException){return Results.Json(new{error="Entegrasyon ayarı kaydedilemedi; önceki kayıtlar korundu."},statusCode:503);}
        });
        group.MapGet("",(EnterpriseSync sync)=>Results.Ok(sync.Status()));
        group.MapGet("/readiness",(HttpContext context,LocalAccountStore accounts,EnterpriseSync sync,EnterpriseBridge bridge,IntegrationRegistry registry,IConfiguration config)=>
            accounts.Configured&&!context.User.IsInRole("admin")?Results.StatusCode(403):Results.Ok(EnterpriseReadiness.Inspect(sync,bridge,registry,config)));
        group.MapPut("/plan",(EnterprisePlan plan,EnterpriseSync sync)=>{sync.Configure(plan);return Results.Ok(sync.Status());});
        group.MapPost("/run",(EnterpriseRun request,EnterpriseSync sync)=>{sync.Request(request.Task);return Results.Accepted(value:new{message="Verileri eşitleme isteği kalıcı görev planına alındı. Sonuçlar görev ve gönderim listesinde izlenir."});});
        group.MapPost("/jobs/{id}/retry",(string id,EnterpriseSync sync)=>{sync.Retry(id);return Results.Ok(new{ok=true});});
        app.MapPost("/hooks/erp/catalog",ReceiveCatalog).RequireRateLimiting("erp-inbound");
        app.MapPost("/hooks/businesses/{businessId}/erp/catalog",ReceiveCatalog).RequireRateLimiting("erp-inbound");
    }
    private static async Task<IResult> ReceiveCatalog(HttpContext context,IConfiguration config,ErpCatalog catalog,EnterpriseSync sync)
    {
        var secret=config["Enterprise:Erp:InboundKey"];
        if(string.IsNullOrWhiteSpace(secret))return Results.Json(new{error="ERP kaynak bildirimi yapılandırılmadı."},statusCode:409);
        if(!context.Request.IsHttps&&!LocalAccess.IsLoopback(context))return Results.StatusCode(403);
        if(!context.Request.HasJsonContentType())return Results.StatusCode(415);
        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if(!long.TryParse(context.Request.Headers["X-ERP-Timestamp"],out var stamp)||stamp<now-300||stamp>now+300)return Results.StatusCode(401);
        const int limit=10*1024*1024;
        if(context.Request.ContentLength>limit)return Results.StatusCode(413);
        using var buffer=new MemoryStream();var bytes=new byte[81920];int count;
        while((count=await context.Request.Body.ReadAsync(bytes,context.RequestAborted))>0){if(buffer.Length+count>limit)return Results.StatusCode(413);buffer.Write(bytes,0,count);}
        var body=buffer.ToArray();var signed=Encoding.UTF8.GetBytes(stamp+".").Concat(body).ToArray();var expected=HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),signed);
        byte[] supplied;try{supplied=Convert.FromHexString(context.Request.Headers["X-ERP-Signature"].ToString());}catch(FormatException){return Results.StatusCode(401);}
        if(!CryptographicOperations.FixedTimeEquals(expected,supplied))return Results.StatusCode(401);
        try{var batch=JsonSerializer.Deserialize<ErpCatalogBatch>(body,new JsonSerializerOptions(JsonSerializerDefaults.Web));if(batch==null)return Results.BadRequest(new{error="Kaynak bildirimi boş."});var result=catalog.Import(batch,sync.Plan.StockAuthority=="wms");sync.CatalogReceived();return Results.Ok(result);}
        catch(JsonException){return Results.BadRequest(new{error="Kaynak bildirimi biçimi geçersiz."});}
        catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
        catch(IntegrationException e){return Results.Json(new{error=e.Message},statusCode:e.Status);}
        catch(IOException){return Results.Json(new{error="Kaynak kaydı kalıcı saklanamadı; aynı işlem kimliğiyle tekrar gönderilebilir."},statusCode:503);}
    }
}
public sealed record EnterpriseRun(string Task);
