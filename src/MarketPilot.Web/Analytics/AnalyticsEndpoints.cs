using System.Text.RegularExpressions;
using MarketPilot.Web.Catalog;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Local;
using MarketPilot.Web.Synchronization;

namespace MarketPilot.Web.Analytics;

public sealed record ProductCost(decimal UnitCost, decimal CommissionRate, decimal ShippingCost, decimal OtherCost, string Currency);
public sealed record ReportQuery(long? StartDate = null, long? EndDate = null, string? Channel = null, string? Mode = null, string? Currency = null)
{
    public string? Error() => StartDate < 0 || EndDate < 0 || StartDate > EndDate ||
        StartDate > 253402300799999 || EndDate > 253402300799999 || Channel?.Length > 120 ||
        Mode is not (null or "" or "local" or "demo" or "real") ||
        Currency is not null && !Regex.IsMatch(Currency, "^[A-Z]{3}$")
        ? "Tarih, kanal, veri kaynağı veya para birimi filtresi geçersiz." : null;
}
public sealed record WorkspaceAlert(string Id, string Level, string Kind, string Title, string Detail, string Target);

public static class AnalyticsEndpoints
{
    public static void MapAnalyticsWorkspace(this WebApplication app)
    {
        app.MapGet("/api/analytics/reports", ([AsParameters] ReportQuery query, OperationsHub hub, DemoStore db, IntegrationRegistry registry) =>
        {
            if (query.Error() is { } error) return Results.BadRequest(new { error });
            var all = hub.AllOrders();
            var currency = query.Currency ?? db.CatalogRead(s => s.Settings.Currency);
            var rows = all.Where(o => o.Currency == currency && ListingFilters.Exact(o.Channel, query.Channel) &&
                ListingFilters.Exact(o.SourceMode, query.Mode) &&
                (!query.StartDate.HasValue || o.Date.ToUnixTimeMilliseconds() >= query.StartDate) &&
                (!query.EndDate.HasValue || o.Date.ToUnixTimeMilliseconds() <= query.EndDate)).ToArray();
            var included = rows.Where(o => o.Status != "İptal" && o.TotalKnown).ToArray();
            var revenue = included.Sum(o => o.Total);
            return Results.Ok(new {
                generatedAt = DateTimeOffset.UtcNow, currency,
                orderCount = rows.Length, revenueOrderCount = included.Length, revenue,
                averageBasket = included.Length > 0 ? (decimal?)decimal.Round(revenue / included.Length, 2) : null,
                cancelledCount = rows.Count(o => o.Status == "İptal"), returnedCount = rows.Count(o => o.Status == "İade"),
                unknownTotalCount = rows.Count(o => !o.TotalKnown), undatedCount = rows.Count(o => o.Date == DateTimeOffset.MinValue),
                demoConnections = registry.Connections.Values.Count(c => c.Settings.Mode == "demo"),
                realConnections = registry.Connections.Values.Count(c => c.Settings.Mode == "real" && c.Status == "connected"),
                channels = rows.GroupBy(o => o.Channel).Select(g => new { name = g.Key, orders = g.Count(), revenue = g.Where(o => o.Status != "İptal" && o.TotalKnown).Sum(o => o.Total) }).OrderByDescending(c => c.revenue),
                daily = rows.Where(o => o.Date != DateTimeOffset.MinValue).GroupBy(o => o.Date.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd"))
                    .OrderBy(g => g.Key).Select(g => new { date = g.Key, orders = g.Count(), revenue = g.Where(o => o.Status != "İptal" && o.TotalKnown).Sum(o => o.Total) }),
                statuses = rows.GroupBy(o => o.Status).Select(g => new { name = g.Key, count = g.Count() }),
                availableChannels = all.Select(o => o.Channel).Distinct().Order(),
                availableCurrencies = all.Select(o => o.Currency).Append(currency).Where(c => Regex.IsMatch(c, "^[A-Z]{3}$")).Distinct().Order()
            });
        });

        app.MapGet("/api/analytics/profit", (DemoStore db) => Results.Ok(db.CatalogRead(s => new {
            generatedAt = DateTimeOffset.UtcNow,
            products = s.Products.OrderBy(p => p.Name).Select(p => {
                var cost = s.ProductCosts.GetValueOrDefault(p.Id);
                var ready = p.PriceKnown && cost is not null && cost.Currency == p.Currency;
                decimal? expenses = ready ? cost!.UnitCost + p.Price * cost.CommissionRate / 100 + cost.ShippingCost + cost.OtherCost : null;
                var margin = expenses.HasValue ? p.Price - expenses : null;
                return new { p.Id, p.Name, p.Sku, p.Currency, p.PriceKnown, price = p.PriceKnown ? (decimal?)p.Price : null,
                    stock = p.StockKnown ? (decimal?)p.Stock : null, p.Status, cost, expenses, margin,
                    marginRate = margin.HasValue && p.Price > 0 ? margin / p.Price * 100 : null,
                    state = !p.PriceKnown ? "missing_price" : cost is null ? "missing_cost" : cost.Currency != p.Currency ? "currency_mismatch" : "ready" };
            }).ToArray()
        })));

        app.MapPut("/api/analytics/costs/{id}", (string id, ProductCost cost, HttpContext context, LocalAccountStore accounts, DemoStore db) =>
        {
            if (accounts.Configured && !context.User.IsInRole("admin")) return Results.StatusCode(403);
            if (cost.UnitCost is < 0 or > 10000000 || cost.CommissionRate is < 0 or > 100 ||
                cost.ShippingCost is < 0 or > 10000000 || cost.OtherCost is < 0 or > 10000000 ||
                cost.Currency is null || !Regex.IsMatch(cost.Currency, "^[A-Z]{3}$"))
                return Results.BadRequest(new { error = "Maliyetler negatif olamaz; komisyon %0–100 ve para birimi geçerli olmalı." });
            return db.Modify<IResult>(s => {
                var product = s.Products.FirstOrDefault(p => p.Id == id);
                if (product is null) return Results.NotFound(new { error = "Ürün bulunamadı." });
                if (product.Currency != cost.Currency) return Results.Conflict(new { error = "Maliyet para birimi ürünün para birimiyle aynı olmalı. Ürünü yenileyip tekrar deneyin." });
                s.ProductCosts[id] = cost;
                db.Event(s, "Ürün maliyeti güncellendi", product.Sku);
                return Results.Ok(new { ok = true });
            });
        });

        app.MapGet("/api/analytics/workspace", (DemoStore db, IntegrationRegistry registry, ReliabilityState reliability,
            LocalBackups backups, LocalAudit audit, HttpContext context, LocalAccountStore accounts,
            IConfiguration configuration, OrderSynchronization automation, StockOutbox outbox) =>
        {
            var snapshot = db.CatalogRead(s => new {
                threshold = s.Settings.LowStockThreshold,
                settings = new { s.Settings.BusinessName, s.Settings.Currency, s.Settings.LowStockThreshold, s.Settings.SyncNotifications },
                products = s.Products.Count,
                alerts = s.Products.Where(p => p.Status == "Aktif" && p.StockKnown && p.Stock <= s.Settings.LowStockThreshold)
                    .Select(p => new WorkspaceAlert("stock:" + p.Id, p.Stock <= 0 ? "critical" : "warning", "stock", p.Stock <= 0 ? "Stok tükendi" : "Kritik stok", $"{p.Name} · {p.Sku} · {p.Stock} adet / eşik {s.Settings.LowStockThreshold}", "inventory")).ToArray(),
                activity = s.Activity.Select(a => new { date = a.At, level = "info", category = "operation", title = a.Title, detail = a.Detail }).ToArray(),
                catalogFailed = s.Catalog.Jobs.Count(j => j.State == "failed")
            });
            var stores = registry.Connections.Values.ToArray();
            var alerts = snapshot.alerts.ToList();
            if (reliability.LastQueueError is { } queueError) alerts.Add(new("queue", "critical", "system", "Arka plan kuyruğu hatası", queueError, "users"));
            if (reliability.LastBackupError is { } backupError) alerts.Add(new("backup", "critical", "system", "Otomatik yedekleme hatası", backupError, "users"));
            if (snapshot.catalogFailed > 0) alerts.Add(new("catalog", "warning", "sync", "Katalog gönderimleri tamamlanamadı", $"{snapshot.catalogFailed} işlem hata durumunda.", "bulk"));
            foreach (var store in stores.Where(c => c.Settings.Mode == "real" && c.Status != "connected"))
                alerts.Add(new("connection:" + store.Id, "warning", "connection", "Gerçek mağaza bağlantısı hazır değil", store.Settings.Name, "connections"));
            var canReadAudit = context.User.IsInRole("admin");
            return Results.Ok(new {
                generatedAt = DateTimeOffset.UtcNow, snapshot.settings, snapshot.products,
                alerts, activity = snapshot.activity,
                events = canReadAudit ? audit.Recent() : Array.Empty<LocalAuditEvent>(), canReadAudit,
                connections = new { total = stores.Length, demo = stores.Count(c => c.Settings.Mode == "demo"), real = stores.Count(c => c.Settings.Mode == "real"), connected = stores.Count(c => c.Settings.Mode == "real" && c.Status == "connected") },
                system = new { mode = LocalAccess.IsPublicDeployment(configuration) ? "public" : "local", accountsConfigured = accounts.Configured,
                    reliability.LastQueueTick, reliability.LastQueueError, reliability.LastBackupSuccess, reliability.LastBackupError, backupCount = backups.List().Count,
                    realReadsEnabled = configuration.GetValue<bool>("Synchronization:IncludeRealStores"), realWritesEnabled = configuration.GetValue<bool>("Synchronization:AllowRealWrites") },
                automation = automation.Status(), outbox = outbox.Status()
            });
        });
    }
}
