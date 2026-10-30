using MarketPilot.Web.Contracts;
using MarketPilot.Web.Local;
namespace MarketPilot.Web.Enterprise.Nebim;

// MarketPilot application routes. These are NOT Nebim URLs or Nebim request formats.
public static class NebimEndpoints
{
    public static void AddNebimProviders(this IServiceCollection services)
    {
        services.AddSingleton(s=>{
            var config=s.GetRequiredService<IConfiguration>();return new ErpConnectionProfile("mock-nebim",config["Workspace:BusinessId"]??"default",config["Enterprise:Nebim:Mode"]??"mock",config.GetValue("Enterprise:Nebim:Enabled",true),config.GetValue("Enterprise:Nebim:AllowMockWrites",false));
        });
        services.AddSingleton<INebimErpProvider,MockNebimProvider>();services.AddSingleton<NebimCanonicalSync>();
    }
    public static void MapNebimProviders(this WebApplication app)
    {
        var group=app.MapGroup("/api/erp/nebim");
        group.AddEndpointFilter(async(context,next)=>{
            var http=context.HttpContext;if(http.Request.Method!="GET"&&http.RequestServices.GetRequiredService<LocalAccountStore>().Configured&&!http.User.IsInRole("admin"))return Results.StatusCode(403);
            try{return await next(context);}catch(ErpProviderException e){return Results.Json(new{error=e.Error},statusCode:e.Error.Code switch{"NotSupported"=>501,"Disabled" or "WriteDisabled" or "OrderConflict" or "IdempotencyConflict" or "VersionConflict"=>409,"TenantMismatch"=>403,"NotFound"=>404,_=>400});}
            catch(MarketPilot.Web.Integrations.IntegrationException e){return Results.Json(new{error=e.Message},statusCode:e.Status);}
            catch(IOException){return Results.Json(new{error=new ErpError("StorageUnavailable","Mock ERP kaydı saklanamadı; işlem tekrar denenebilir.",true)},statusCode:503);}
        });
        group.MapGet("",(INebimErpProvider provider)=>Results.Ok(new{provider.Profile,provider.Capabilities,nativeAdapter="technical-contract-pending"}));
        group.MapGet("/products",async(INebimErpProvider p,CancellationToken ct)=>Results.Ok(await p.ReadProductsAsync(ct)));
        group.MapGet("/variants",async(INebimErpProvider p,CancellationToken ct)=>Results.Ok(await p.ReadVariantsAsync(ct)));
        group.MapGet("/identifiers",async(INebimErpProvider p,CancellationToken ct)=>Results.Ok(await p.ReadIdentifiersAsync(ct)));
        group.MapGet("/prices",async(INebimErpProvider p,CancellationToken ct)=>Results.Ok(await p.ReadPriceListsAsync(ct)));
        group.MapGet("/warehouses",async(INebimErpProvider p,CancellationToken ct)=>Results.Ok(await p.ReadWarehousesAsync(ct)));
        group.MapGet("/stock",async(INebimErpProvider p,CancellationToken ct)=>Results.Ok(await p.ReadStockAsync(ct)));
        group.MapPost("/orders",async(MarketPilot.Web.Contracts.MarketplaceOrder order,INebimErpProvider p,CancellationToken ct)=>Results.Ok(await p.RegisterOrderAsync(order,ct)));
        group.MapGet("/orders/{id}",async(string id,INebimErpProvider p,CancellationToken ct)=>Results.Ok(await p.GetOrderStatusAsync(id,ct)));
        group.MapGet("/documents",async(INebimErpProvider p,CancellationToken ct)=>Results.Ok(await p.ReadDocumentReferencesAsync(ct)));
        group.MapGet("/events",async(INebimErpProvider p,CancellationToken ct)=>Results.Ok(await p.ReadEventsAsync(ct)));
        group.MapPost("/events",async(IntegrationEvent change,INebimErpProvider p,CancellationToken ct)=>Results.Ok(await p.HandleEventAsync(change,ct)));
        group.MapGet("/sync-status",async(INebimErpProvider p,CancellationToken ct)=>Results.Ok(await p.GetSyncStatusAsync(ct)));
        group.MapPost("/sync",async(NebimCanonicalSync sync,CancellationToken ct)=>Results.Ok(await sync.Run(ct)));
    }
}
public sealed class NebimCanonicalSync(INebimErpProvider provider,CanonicalStore store,DemoStore db)
{
    private readonly SemaphoreSlim gate=new(1,1);
    public async Task<object> Run(CancellationToken ct)
    {
        await gate.WaitAsync(ct);try
        {
            // Read before applying: an unsupported read cannot claim a successful sync.
            var products=await provider.ReadProductsAsync(ct);var variants=await provider.ReadVariantsAsync(ct);var identifiers=await provider.ReadIdentifiersAsync(ct);
            var warehouses=await provider.ReadWarehousesAsync(ct);var stock=await provider.ReadStockAsync(ct);var prices=await provider.ReadPriceListsAsync(ct);
            var results=new List<ContractResult>();
            foreach(var row in products.Data.Cast<CanonicalRecord>().Concat(variants.Data).Concat(identifiers.Data.Barcodes).Concat(warehouses.Data).Concat(stock.Data).Concat(prices.Data)){ct.ThrowIfCancellationRequested();results.Add(store.Apply(row));}
            var success=results.All(r=>r.State is "applied" or "duplicate");
            if(success)
            {
                var scope=System.Text.Json.JsonSerializer.Serialize(new[]{provider.Profile.TenantId,provider.Profile.Id,"sync"});
                var version=db.Modify(s=>{var next=s.NebimMock.SyncVersions.GetValueOrDefault(scope)+1;s.NebimMock.SyncVersions[scope]=next;return next;});
                var now=DateTimeOffset.UtcNow;var id=provider.Profile.Id+"-catalog-checkpoint";
                var context=new RecordContext(provider.Profile.TenantId,"mock-nebim-v3",id,null,Guid.NewGuid().ToString("N"),id+"-"+version,version,now,now);
                var checkpoint=store.Apply(new MarketPilot.Web.Contracts.SyncCheckpoint(id,context,"catalog",null,products.Data.Cast<CanonicalRecord>().Concat(variants.Data).Concat(identifiers.Data.Barcodes).Concat(warehouses.Data).Concat(stock.Data).Concat(prices.Data).Max(r=>r.Context.SourceVersion),now));results.Add(checkpoint);success=checkpoint.State is "applied" or "duplicate";
                if(success)db.Modify(s=>{var old=s.NebimMock.SyncStatuses.GetValueOrDefault(scope)??new(provider.Profile.Id,null,null);s.NebimMock.SyncStatuses[scope]=old with{LastCanonicalSyncAt=now};return true;});
            }
            return new{isMock=true,success,results};
        }finally{gate.Release();}
    }
}
