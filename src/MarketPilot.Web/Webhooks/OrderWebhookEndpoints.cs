using MarketPilot.Web.Local;
using MarketPilot.Web.Integrations.Demo;
using MarketPilot.Web.Integrations.CentralStock;
namespace MarketPilot.Web.Webhooks;

public static class OrderWebhookEndpoints
{
    public static void AddOrderWebhooks(this IServiceCollection services){services.AddSingleton<OrderWebhookInbox>();services.AddSingleton<OrderWebhookReceiver>();services.AddHostedService<OrderWebhookWorker>();}
    public static void MapOrderWebhooks(this WebApplication app)
    {
        // This single non-/api path has its own credential validation; local API/auth rules stay intact.
        app.MapPost("/hooks/orders/{endpointId}",(string endpointId,HttpContext context,OrderWebhookReceiver receiver,CancellationToken ct)=>receiver.Receive(endpointId,context,ct));
        app.MapGet("/api/operations/revision",(DemoStore db,DemoWorkspace demo,CentralStockService stock)=>Results.Ok(new{revision=db.Revision + ":" + demo.Revision + ":" + stock.Revision}));
        app.MapGet("/api/system/webhooks",(HttpContext c,LocalAccountStore accounts,OrderWebhookReceiver receiver)=>accounts.Configured&&!c.User.IsInRole("admin")?Results.StatusCode(403):Results.Ok(receiver.Status()));
        app.MapPost("/api/system/webhooks/{id}/retry",(string id,HttpContext c,LocalAccountStore accounts,OrderWebhookInbox inbox)=>{
            if(accounts.Configured&&!c.User.IsInRole("admin"))return Results.StatusCode(403);try{inbox.Retry(id);return Results.Ok(new{ok=true});}catch(MarketPilot.Web.Integrations.IntegrationException e){return Results.Json(new{error=e.Message},statusCode:e.Status);}
        });
    }
}
