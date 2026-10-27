namespace MarketPilot.Web.Integrations.Hepsiburada;

public static class HepsiburadaEndpoints
{
    public static void AddHepsiburadaIntegration(this IServiceCollection services)
    {
        services.AddHttpClient<HepsiburadaAdapter>(c => c.Timeout = TimeSpan.FromSeconds(45))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<IMarketplaceAdapter>(p => p.GetRequiredService<HepsiburadaAdapter>());
    }
    public static void MapHepsiburada(this RouteGroupBuilder group)
    {
        group.MapGet("/hepsiburada/connections/{id}/search/{kind}", async (string id, string kind, string? orderNumber, [AsParameters] ListingQuery query,
            IntegrationRegistry registry, HepsiburadaAdapter adapter, CancellationToken ct) =>
        {
            if (!registry.Connections.TryGetValue(id, out var store) || store.Settings.Marketplace != "hepsiburada") return Results.NotFound();
            if (store.Settings.Mode != "real") return Results.BadRequest(new { error = "Demo mağazasını demo çalışma alanından açın." });
            if (query.Error() is { } error) return Results.BadRequest(new { error });
            if (orderNumber?.Length > 100) return Results.BadRequest(new { error = "Sipariş numarası çok uzun." });
            if (!string.IsNullOrWhiteSpace(query.Approval) || query.Sort != "newest") return Results.BadRequest(new { error = "Bu mağazada seçilen onay/sıralama filtresi desteklenmiyor." });
            if (kind is "products" or "inventory" && (query.StartDate.HasValue || query.EndDate.HasValue)) return Results.BadRequest(new { error = "Ürünler için tarih filtresi desteklenmiyor." });
            if (kind is "orders" or "shipping" or "invoices" or "labels" && (!string.IsNullOrWhiteSpace(query.Sku) || !string.IsNullOrWhiteSpace(query.Barcode))) return Results.BadRequest(new { error = "Sipariş numarasıyla arayın." });
            // Never filter a downloaded page and present it as a complete remotely filtered result.
            if (!string.IsNullOrWhiteSpace(query.Search) || !string.IsNullOrWhiteSpace(query.Category) || !string.IsNullOrWhiteSpace(query.Carrier) ||
                query.MinStock.HasValue || query.MaxStock.HasValue || query.MinPrice.HasValue || query.MaxPrice.HasValue)
                return Results.BadRequest(new { error = "Bu mağazada ürünleri tam stok kodu, siparişleri sipariş numarası veya durumuyla filtreleyin." });
            if (kind == "claims" && (!string.IsNullOrWhiteSpace(query.Barcode) || !string.IsNullOrWhiteSpace(query.Sku) || query.StartDate.HasValue || query.EndDate.HasValue))
                return Results.BadRequest(new { error = "İadeleri durumuyla filtreleyin." });
            var gate = registry.Gates.GetOrAdd(id, _ => new(1, 1));
            if (!await gate.WaitAsync(0, ct)) return Results.Json(new { error = "Mağazada başka bir işlem sürüyor; yeniden deneyin." }, statusCode: 409);
            try
            {
                object result;
                if (kind is "products" or "inventory") result = await adapter.ProductsAsync(store.Settings, new(query.Page, query.Size, query.Barcode, query.Sku, query.Status), ct);
                else if (kind is "orders" or "shipping" or "invoices" or "labels")
                {
                    // Native exact order-number query; never search only a fetched page.
                    long? start = query.StartDate;
                    long? end = query.EndDate;
                    if (start.HasValue != end.HasValue || end - start > TimeSpan.FromDays(31).TotalMilliseconds) return Results.BadRequest(new { error = "Tarihleri birlikte seçin; aralık en fazla 31 gün olabilir." });
                    result = await adapter.OrdersAsync(store.Settings, new(query.Page, query.Size, kind == "invoices" ? "MissingInvoice" : query.Status, orderNumber?.Trim(), StartDate: start, EndDate: end), ct);
                }
                else if (kind == "claims") result = await adapter.ClaimsAsync(store.Settings, query, ct);
                else return Results.Json(new { error = "Bu bölümdeki gerçek veri bağlantısı henüz tamamlanmadı. Demo mağazada örneklerini inceleyebilirsiniz." }, statusCode: 501);
                registry.Connections[id] = store with { Status = "connected", LastOperation = DateTimeOffset.UtcNow, LastAction = kind, ErrorCode = null, Message = "Hepsiburada verileri okundu." }; registry.Save();
                return Results.Ok(result);
            }
            catch (Exception e) when (e is IntegrationException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or OverflowException)
            {
                var ex = e as IntegrationException ?? new IntegrationException("read_failed", "Hepsiburada verileri okunamadı. Mağaza bağlantısını kontrol edip yeniden deneyin.");
                registry.Connections[id] = store with { Status = "error", LastOperation = DateTimeOffset.UtcNow, LastAction = kind, ErrorCode = ex.Code, Message = ex.Message }; registry.Save();
                return Results.Json(new { error = ex.Message, code = ex.Code }, statusCode: ex.Status);
            }
            finally { gate.Release(); }
        });
    }
}
