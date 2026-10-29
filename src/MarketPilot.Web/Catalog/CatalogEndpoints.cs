using MarketPilot.Web.Integrations;
using MarketPilot.Web.Local;
namespace MarketPilot.Web.Catalog;
public static class CatalogEndpoints
{
    public static void AddCatalog(this IServiceCollection s)
    {
        s.AddSingleton<CatalogHub>();
        s.AddSingleton<ErpCatalogReadModel>();
        s.AddSingleton<OperationsHub>();
        s.AddSingleton<CatalogSource>();
        s.AddHostedService<CatalogWorker>();
    }
    public static void MapCatalog(this WebApplication app)
    {
        var group = app.MapGroup("/api/catalog");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (!LocalAccess.IsLoopback(http) &&
                !(LocalAccess.IsPublicDeployment(http.RequestServices.GetRequiredService<IConfiguration>()) &&
                  http.User.Identity?.IsAuthenticated == true))
                return Results.Json(new {
                    error = "Katalog çalışma alanı yalnızca yerel erişime açıktır."
                }, statusCode: 403);
            if(http.Request.Path.StartsWithSegments("/api/catalog/erp")&&http.Request.Method!="GET"&&http.RequestServices.GetRequiredService<LocalAccountStore>().Configured&&!http.User.IsInRole("admin"))return Results.StatusCode(403);
            try
            {
                return await next(context);
            }
            catch(MarketPilot.Web.Enterprise.Nebim.ErpProviderException e){return Results.Json(new{error=e.Error.Message,code=e.Error.Code},statusCode:e.Error.Code=="NotSupported"?501:409);}
            catch(IOException){return Results.Json(new{error="ERP catalog persistence failed; previous records preserved."},statusCode:503);}
            catch (Exception ex) when (ex is IntegrationException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or System.Xml.XmlException or ArgumentException or OverflowException or InvalidOperationException) { var e = ex as IntegrationException ?? new IntegrationException("catalog_failed", "Katalog işlemi tamamlanamadı. Seçimi ve mağaza bağlantısını kontrol et.", ex is ArgumentException ? 400 : 502); return Results.Json(new { error = e.Message, code = e.Code }, statusCode: e.Status); }
        });
        group.MapGet("/erp/status",(ErpCatalogReadModel erp)=>Results.Ok(erp.Status()));
        group.MapPost("/erp/preview",async(ErpCatalogReadModel erp,CancellationToken ct)=>Results.Ok(await erp.Preview(ct)));
        group.MapPost("/erp/apply",(ErpPreviewApply input,ErpCatalogReadModel erp)=>Results.Ok(erp.Apply(input.Token)));
        group.MapDelete("/products/{id}",()=>Results.Json(new{error="ERP product master is read only."},statusCode:405));
        group.MapPatch("/products/{id}",()=>Results.Json(new{error="ERP product master is read only."},statusCode:405));
        group.MapGet("/lookup", (string storeId, string? externalId, string? sku, string? barcode, CatalogHub hub) => Results.Ok(hub.Lookup(storeId, externalId, sku, barcode)));
        group.MapGet("/products", ([AsParameters] CatalogQuery query, CatalogHub hub) => query.Error() is { } error ? Results.BadRequest(new { error }) : Results.Ok(hub.Search(query)));
        group.MapGet("/products/{id}", (string id, [AsParameters] CatalogQuery query, CatalogHub hub) => query.Error() is { } error ? Results.BadRequest(new { error }) : Results.Ok(hub.Detail(id, query)));
        group.MapPost("/products", () => Results.Json(new { error = "Yeni ürün oluşturma kapalı. Ürünler Nebim V3 kaynağından alınır." }, statusCode: 405));
        group.MapPut("/products/{id}", (string id, CatalogEdit input, CatalogHub hub) => Results.Ok(hub.Edit(id, input)));
        group.MapGet("/listings", ([AsParameters] CatalogQuery query, CatalogHub hub) => query.Error() is { } error ? Results.BadRequest(new { error }) : Results.Ok(hub.Listings(query)));
        group.MapGet("/stores/{id}/candidates", async (string id, [AsParameters] SourceQuery query, CatalogSource source, CancellationToken ct) => Results.Ok(await source.Read(id, query, ct)));
        group.MapGet("/candidates/{token}", (string token, int? page, CatalogSource source) => Results.Ok(source.Page(token, page ?? 0)));
        group.MapPost("/import", (CatalogImport input, CatalogSource source, CatalogHub hub, DemoStore db, IntegrationRegistry registry) => {
            if(input.Keys==null)return Results.BadRequest(new{error="Ürün seçimi gerekli."});
            var batch=source.Get(input.Token);
            var mode=registry.Connections[batch.StoreId].Settings.Mode;
            var known=db.CatalogRead(s=>batch.Rows.Where(r=>input.Keys.Contains(r.Key)).All(r=>s.Catalog.Listings.ContainsKey(r.Key)||s.Products.Any(p=>p.SourceSystem==ProductOwnershipPolicy.ErpSource&&p.SourceMode==mode&&(r.Barcode.Length>0?p.Barcode==r.Barcode:p.Sku==r.Sku))));
            return known?Results.Ok(hub.Import(batch,input.Keys)):Results.Conflict(new{error="Yeni ürünler önce Nebim V3 kaynağından alınmalıdır. Mağaza işlemi yalnızca mevcut ürünleri eşleştirir."});
        });
        group.MapPut("/mapping", (CatalogRemap input, CatalogHub hub) => Results.Ok(hub.Remap(input)));
        group.MapGet("/categories", ([AsParameters] CatalogQuery query, CatalogHub hub) => query.Error() is { } error ? Results.BadRequest(new { error }) : Results.Ok(hub.Categories(query)));
        group.MapPut("/categories", (CategoryBinding input, CatalogHub hub) => Results.Ok(hub.Bind(input)));
        group.MapPost("/preview", (CatalogSelection input, CatalogHub hub) => Results.Ok(hub.Preview(input.Keys)));
        group.MapPost("/publish", (CatalogPublish input, CatalogHub hub) => Results.Ok(hub.Publish(input)));
        group.MapGet("/jobs", ([AsParameters] CatalogQuery query, CatalogHub hub) => query.Error() is { } error ? Results.BadRequest(new { error }) : Results.Ok(hub.Jobs(query)));
    }
}
public record CatalogSelection(string[] Keys);

public sealed record ErpPreviewApply(string Token);
