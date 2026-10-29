using MarketPilot.Web.Catalog;
using MarketPilot.Web.Integrations;

namespace MarketPilot.Web.Workspace;
public static class WorkspaceEndpoints
{
    public static void MapSharedWorkspace(this WebApplication app)
    {
        var group = app.MapGroup("/api/workspace");
        group.AddEndpointFilter(async (context, next) => {
            try { return await next(context); }
            catch (IntegrationException e) { return Results.Json(new { error = e.Message }, statusCode: e.Status); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
            catch (IOException) { return Results.Json(new { error = "Kayıt kalıcı olarak saklanamadı; tekrar deneyin." }, statusCode: 503); }
        });
        group.MapGet("/views/{module}", (string module, [AsParameters] ListingQuery query, WorkspaceHub hub) => query.Error() is { } error ? Results.BadRequest(new { error }) : Results.Ok(hub.Search(module, query)));
        group.MapGet("/orders/{id}", (string id, WorkspaceHub hub) => Results.Ok(hub.OrderDetail(id)));
        group.MapPost("/orders", (LocalOrderDraft input, WorkspaceHub hub) => Results.Ok(hub.CreateOrder(input)));
        group.MapPost("/records", (WorkspaceRecordDraft input, WorkspaceHub hub) => Results.Ok(hub.CreateRecord(input)));
        group.MapPost("/views/{module}/{id}/actions", (string module, string id, WorkspaceAction input, WorkspaceHub hub) => Results.Ok(hub.Act(module, id, input)));
        group.MapGet("/options/{kind}", (string kind, string? search, bool? localOnly, OperationsHub operations, DemoStore db) => {
            if (search?.Length > 120) return Results.BadRequest(new { error = "Arama 120 karakteri geçemez." });
            if (kind == "products") return Results.Ok(db.SearchProducts(new(Search: search, Status: "Aktif", Size: 25)));
            if (kind == "orders") {
                var query = new ListingQuery(Search: search, Size: localOnly == true ? int.MaxValue : 25);
                var page = operations.Orders(query);
                if (localOnly == true) return Results.Ok(ListingFilters.Page(page.Items.Where(o => o.StoreId.Length == 0), new ListingQuery(Size: 25)));
                return Results.Ok(page);
            }
            return Results.NotFound(new { error = "Seçim kaynağı bulunamadı." });
        });
    }
}
