using MarketPilot.Web.Integrations;
using MarketPilot.Web.Local;
namespace MarketPilot.Web.Workspace;
public static class OperationalEndpoints
{
    public static void AddOperationalPreparation(this IServiceCollection services){services.AddSingleton<OperationalService>();services.AddHostedService<OperationalWorker>();}
    public static void MapOperationalPreparation(this WebApplication app)
    {
        var group=app.MapGroup("/api/operations/preparation");
        group.AddEndpointFilter(async(context,next)=>{
            var c=context.HttpContext;
            if(c.Request.Method!="GET"&&c.RequestServices.GetRequiredService<LocalAccountStore>().Configured&&!c.User.IsInRole("admin"))return Results.StatusCode(403);
            try{return await next(context);}catch(IntegrationException e){return Results.Json(new{error=e.Message},statusCode:e.Status);}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}catch(IOException){return Results.Json(new{error="İşlem kalıcı kaydedilemedi; önceki kayıtlar korundu."},statusCode:503);}
        });
        group.MapGet("",(OperationalService service)=>Results.Ok(service.Status()));
        group.MapPut("/policy",(OperationalPolicy input,OperationalService service)=>{service.Configure(input);return Results.Ok(service.Status());});
        group.MapPost("/scan",(OperationalService service)=>Results.Ok(service.Scan()));
        group.MapPost("/settlements",(SettlementDraft input,OperationalService service)=>Results.Ok(service.Settle(input)));
        group.MapPost("/holds",(HoldDraft input,OperationalService service)=>Results.Ok(service.Reserve(input)));
        group.MapPost("/holds/{id}/release",(string id,OperationalService service)=>{service.Release(id);return Results.Ok(new{ok=true});});
        group.MapPost("/requests",(OperationDraft input,OperationalService service)=>Results.Ok(service.Prepare(input)));
        group.MapPost("/requests/{id}/approve",(string id,OperationalService service)=>Results.Ok(service.Approve(id)));
        group.MapPost("/requests/{id}/cancel",(string id,OperationalService service)=>{service.Cancel(id);return Results.Ok(new{ok=true});});
        group.MapPost("/requests/{id}/receive",(string id,ReturnReceipt input,OperationalService service)=>Results.Ok(service.ReceiveReturn(id,input.ReceiptId)));
        group.MapPost("/deliveries/{id}/retry",(string id,OperationalService service)=>{service.Retry(id);return Results.Ok(new{ok=true});});
    }
}
public sealed record ReturnReceipt(string ReceiptId);
