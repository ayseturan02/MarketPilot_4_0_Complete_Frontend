using MarketPilot.Web.Integrations;
using MarketPilot.Web.Local;
using System.Text.Json;
namespace MarketPilot.Web.Contracts;
public static class CanonicalEndpoints
{
    public static void AddCanonicalContracts(this IServiceCollection services)=>services.AddSingleton<CanonicalStore>();
    public static void MapCanonicalContracts(this WebApplication app)
    {
        var group=app.MapGroup("/api/contracts");
        group.AddEndpointFilter(async(context,next)=>{
            var http=context.HttpContext;
            if(http.Request.Method!="GET"&&http.RequestServices.GetRequiredService<LocalAccountStore>().Configured&&!http.User.IsInRole("admin"))return Results.StatusCode(403);
            try{return await next(context);}
            catch(IntegrationException e){return Results.Json(new{error=e.Message,code=e.Code},statusCode:e.Status);}
            catch(IOException){return Results.Json(new{error="Ortak veri kalıcı kaydedilemedi; önceki kayıtlar korundu."},statusCode:503);}
        });
        group.MapGet("/schema",()=>Results.Ok(new{schemaVersion=1,resources=CanonicalStore.Types.Keys}));
        group.MapGet("/manual-mappings",(CanonicalStore store)=>Results.Ok(store.Reviews()));
        group.MapPost("/manual-mappings/{id}/resolve",(string id,ResolveMapping input,CanonicalStore store)=>Results.Ok(store.Resolve(id,input.VariantId)));
        group.MapGet("/available-to-sell",(string variantId,string warehouseId,CanonicalStore store)=>Results.Ok(store.Available(variantId,warehouseId)));
        group.MapGet("/{kind}",(string kind,int? page,int? size,CanonicalStore store)=>Results.Ok(store.Read(kind,page??0,size??25)));
        group.MapPost("/{kind}",(string kind,JsonElement payload,long? expectedVersion,CanonicalStore store)=>Results.Ok(store.Apply(kind,payload,expectedVersion)));
    }
    public sealed record ResolveMapping(string VariantId);
}
