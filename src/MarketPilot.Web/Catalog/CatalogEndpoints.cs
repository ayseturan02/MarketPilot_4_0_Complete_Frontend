using MarketPilot.Web.Integrations;
using MarketPilot.Web.Local;
namespace MarketPilot.Web.Catalog;
public static class CatalogEndpoints
{
    public static void AddCatalog(this IServiceCollection s)
    {
        s.AddSingleton<CatalogHub>();
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
            try
            {
                return await next(context);
            }
            catch (Exception ex) when (ex is IntegrationException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or System.Xml.XmlException or ArgumentException or OverflowException or InvalidOperationException) { var e = ex as IntegrationException ?? new IntegrationException("catalog_failed", "Katalog işlemi tamamlanamadı. Seçimi ve mağaza bağlantısını kontrol et.", ex is ArgumentException ? 400 : 502); return Results.Json(new { error = e.Message, code = e.Code }, statusCode: e.Status); }
        });
        group.MapGet("/lookup", (string storeId, string? externalId, string? sku, string? barcode, CatalogHub hub) => Results.Ok(hub.Lookup(storeId, externalId, sku, barcode)));
        group.MapGet("/products", ([AsParameters] CatalogQuery query, CatalogHub hub) => query.Error() is { } error ? Results.BadRequest(new { error }) : Results.Ok(hub.Search(query)));
        group.MapGet("/products/{id}", (string id, [AsParameters] CatalogQuery query, CatalogHub hub) => query.Error() is { } error ? Results.BadRequest(new { error }) : Results.Ok(hub.Detail(id, query)));
        group.MapPost("/products", (CatalogCreate input, CatalogHub hub) => Results.Ok(hub.Create(input)));
        group.MapPut("/products/{id}", (string id, CatalogEdit input, CatalogHub hub) => Results.Ok(hub.Edit(id, input)));
        group.MapGet("/listings", ([AsParameters] CatalogQuery query, CatalogHub hub) => query.Error() is { } error ? Results.BadRequest(new { error }) : Results.Ok(hub.Listings(query)));
        group.MapGet("/stores/{id}/candidates", async (string id, [AsParameters] SourceQuery query, CatalogSource source, CancellationToken ct) => Results.Ok(await source.Read(id, query, ct)));
        group.MapGet("/candidates/{token}", (string token, int? page, CatalogSource source) => Results.Ok(source.Page(token, page ?? 0)));
        group.MapPost("/import", (CatalogImport input, CatalogSource source, CatalogHub hub) => Results.Ok(hub.Import(source.Get(input.Token), input.Keys)));
        group.MapPut("/mapping", (CatalogRemap input, CatalogHub hub) => Results.Ok(hub.Remap(input)));
        group.MapGet("/categories", ([AsParameters] CatalogQuery query, CatalogHub hub) => query.Error() is { } error ? Results.BadRequest(new { error }) : Results.Ok(hub.Categories(query)));
        group.MapPut("/categories", (CategoryBinding input, CatalogHub hub) => Results.Ok(hub.Bind(input)));
        group.MapPost("/preview", (CatalogSelection input, CatalogHub hub) => Results.Ok(hub.Preview(input.Keys)));
        group.MapPost("/publish", (CatalogPublish input, CatalogHub hub) => Results.Ok(hub.Publish(input)));
        group.MapGet("/jobs", ([AsParameters] CatalogQuery query, CatalogHub hub) => query.Error() is { } error ? Results.BadRequest(new { error }) : Results.Ok(hub.Jobs(query)));
    }
}
public record CatalogSelection(string[] Keys);
public record CatalogCreate(CatalogEdit Product, string Mode = "local");
