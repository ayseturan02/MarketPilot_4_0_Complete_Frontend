namespace MarketPilot.Web.Integrations.Amazon;

public static class AmazonEndpoints
{
    public static void AddAmazonIntegration(this IServiceCollection services)
    {
        services.AddHttpClient("AmazonSpApi",c=>c.Timeout=TimeSpan.FromSeconds(45)).ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false});
        services.AddSingleton<AmazonApiClient>();services.AddTransient<AmazonSpApiAdapter>();services.AddTransient<IMarketplaceAdapter>(p=>p.GetRequiredService<AmazonSpApiAdapter>());
    }
    public static void MapAmazon(this RouteGroupBuilder group)
    {
        group.MapGet("/amazon/connections/{id}/search/{kind}",async(string id,string kind,[AsParameters] AmazonQuery query,IntegrationRegistry registry,MarketPilot.Web.Integrations.Demo.DemoWorkspace demo,AmazonSpApiAdapter adapter,CancellationToken ct)=>
        {
            if(!registry.Connections.TryGetValue(id,out var store)||store.Settings.Marketplace!="amazon")return Results.NotFound();
            
            if(query.Error() is {} error)return Results.BadRequest(new{error});
            if(kind is "orders" or "shipping" or "invoices" or "finance" && !string.IsNullOrWhiteSpace(query.Token) && !query.StartDate.HasValue)return Results.BadRequest(new{error="Devam sayfasında önceki sorgunun tarihlerini koruyun."});
            if(kind=="reports" && query.ReportType is {Length:>0} && !new[]{"GET_MERCHANT_LISTINGS_ALL_DATA","GET_FLAT_FILE_ALL_ORDERS_DATA_BY_ORDER_DATE_GENERAL","GET_FBA_FULFILLMENT_CUSTOMER_RETURNS_DATA"}.Contains(query.ReportType))return Results.BadRequest(new{error="Desteklenmeyen rapor türü."});
            if(kind=="reports" && query.Status is {Length:>0} && !new[]{"IN_QUEUE","IN_PROGRESS","DONE","CANCELLED","FATAL"}.Contains(query.Status))return Results.BadRequest(new{error="Geçersiz rapor durumu."});
            var gate=registry.Gates.GetOrAdd(id,_=>new(1,1));if(!await gate.WaitAsync(0,ct))return Results.Json(new{error="Bu mağazada başka bir işlem sürüyor. Yeniden deneyin."},statusCode:409);
            try
            {
                var result=store.Settings.Mode=="demo"?AmazonDemo.Read(demo,store.Settings,kind,query):await adapter.Read(store.Settings,kind,query,ct);
                registry.Connections[id]=store with{Status=store.Settings.Mode=="demo"?"demo":"connected",LastAction=kind,LastOperation=DateTimeOffset.UtcNow,ErrorCode=null,Message=store.Settings.Mode=="demo"?"Demo kayıtları okundu.":"Amazon verileri okundu."};registry.Save();return Results.Ok(result);
            }
            catch(Exception ex)when(ex is IntegrationException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or OverflowException)
            {
                var e=ex as IntegrationException??new IntegrationException("read_failed","Amazon verileri okunamadı. Mağaza bağlantısını kontrol edip yeniden deneyin.");
                registry.Connections[id]=store with{Status="error",LastOperation=DateTimeOffset.UtcNow,LastAction=kind,ErrorCode=e.Code,Message=e.Message};registry.Save();return Results.Json(new{error=e.Message,code=e.Code},statusCode:e.Status);
            }
            finally{gate.Release();}
        });
    }
}
