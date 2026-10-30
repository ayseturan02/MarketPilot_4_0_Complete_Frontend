using MarketPilot.Web.Enterprise.Nebim;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Local;
namespace MarketPilot.Web.Pricing;
public static class PricingEndpoints
{
    public static void AddErpPricing(this IServiceCollection services)=>services.AddSingleton<ErpPricingService>();
    public static void MapErpPricing(this WebApplication app)
    {
        var group=app.MapGroup("/api/pricing");
        group.AddEndpointFilter(async(context,next)=>{
            var http=context.HttpContext;if(http.Request.Method!="GET"&&http.RequestServices.GetRequiredService<LocalAccountStore>().Configured&&!http.User.IsInRole("admin"))return Results.StatusCode(403);
            try{return await next(context);}catch(IntegrationException e){return Results.Json(new{error=e.Message,code=e.Code},statusCode:e.Status);}catch(ErpProviderException e){return Results.Json(new{error=e.Error.Message},statusCode:e.Error.Code=="NotSupported"?501:409);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){return Results.Json(new{error="Fiyat kaydı saklanamadı; tekrar isteği güvenle kontrol edilebilir."},statusCode:503);}
        });
        group.MapGet("",(ErpPricingService pricing)=>Results.Ok(pricing.Status()));
        group.MapPost("/source/refresh",async(ErpPricingService pricing,CancellationToken ct)=>Results.Ok(await pricing.Refresh(ct)));
        group.MapPut("/policy",(PricingPolicy policy,ErpPricingService pricing)=>{pricing.Configure(policy);return Results.Ok(pricing.Status());});
        group.MapPut("/rules",(ChannelPriceRule rule,ErpPricingService pricing)=>{pricing.Configure(rule);return Results.Ok(pricing.Status());});
        group.MapPost("/preview",(PricingPreviewRequest input,ErpPricingService pricing)=>Results.Ok(pricing.Preview(input.ProductId,input.StoreId,input.ListingKey)));
        group.MapPost("/proposals/{id}/approve",(string id,HttpContext http,ErpPricingService pricing)=>Results.Ok(pricing.Approve(id,http.User.Identity?.Name??"local-admin")));
        group.MapPost("/proposals/{id}/publish",(string id,ErpPricingService pricing)=>Results.Ok(pricing.Publish(id)));
    }
    public sealed record PricingPreviewRequest(string ProductId,string StoreId,string ListingKey);
}
