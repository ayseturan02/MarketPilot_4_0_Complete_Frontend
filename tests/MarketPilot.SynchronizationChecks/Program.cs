using System.Net;
using System.Text;
using System.Text.Json;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.CentralStock;
using MarketPilot.Web.Integrations.Demo;
using MarketPilot.Web.Synchronization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static JsonElement View(CentralStockService service) => JsonSerializer.SerializeToElement(service.View(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
static int Quantity(CentralStockService service, string sku) => View(service).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("sku").GetString() == sku).GetProperty("quantity").GetInt32();
var folder = Path.Combine(Path.GetTempPath(), "marketpilot-sync-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var env = new TestEnvironment { ContentRootPath = folder };
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["Marketplaces:StockSource:BaseUrl"] = "https://fixture.invalid",
        ["Marketplaces:StockSource:ApiKey"] = "fixture-only-key",
        ["Marketplaces:StockSource:AllowWrites"] = "true"
    }).Build();
    var registry = new IntegrationRegistry(env);
    var demo = new DemoWorkspace(env);
    var store = new ConnectionState("real-fixture", new("Fixture", "hepsiburada", "real", "production"), "untested", null, null, null, null, null);
    registry.Connections[store.Id] = store;
    registry.Connections["real-alias"] = store with { Id = "real-alias", Settings = store.Settings with { Name = "Same account alias" } };
    var handler = new SourceFixture();
    var adapter = new OrdersFixture();
    using var provider = new ServiceCollection().AddSingleton<IConfiguration>(config).AddSingleton<IWebHostEnvironment>(env).AddSingleton(TimeProvider.System).AddSingleton<StockOutbox>().AddSingleton(registry).AddSingleton(demo)
        .AddSingleton<IHttpClientFactory>(handler).AddTransient<CentralStockSource>()
        .AddSingleton<IMarketplaceAdapter>(adapter).BuildServiceProvider();
    var service = new CentralStockService(env, provider, config);
    adapter.Rows = [OrdersFixture.Order("old-a", "A"), OrdersFixture.Order("old-b", "B")];
    await service.RunAsync(default);
    Check(service.LastRunSucceeded && adapter.Pages.SequenceEqual(new[] { 0, 1 }), "All numbered pages must be read exactly once, including aliases");
    Check(Quantity(service, "A") == 10 && Quantity(service, "B") == 10, "Historical orders must be baselined");
    Check(!File.Exists(Path.Combine(folder, "Data", "central-stock.json")), "Real source must have separate durable state");
    try { await service.DemoSaleAsync(new("trendyol", "8691000000001", 1), default); throw new Exception("Demo sale reached real source"); }
    catch (ArgumentException) { }

    adapter.Rows.Add(OrdersFixture.Order("new-b", "B", 2));
    await service.RunAsync(default);
    Check(Quantity(service, "B") == 8 && handler.Reports.Count == 1 && handler.Reports[0].Quantity == 2, "A sale on a later page must be deducted once");
    await service.RunAsync(default);
    Check(Quantity(service, "B") == 8 && handler.Reports.Count == 1, "Repeated sync must not duplicate a sale");

    adapter.Rows.Add(OrdersFixture.Order("unmatched", "C", 3));
    await service.RunAsync(default);
    Check(!service.LastRunSucceeded && View(service).GetProperty("unmatchedSales").GetInt32() == 1, "Unmatched sales must stop distribution");
    handler.Stock["C"] = 12;
    await service.RunAsync(default);
    Check(service.LastRunSucceeded && Quantity(service, "C") == 9, "An unmatched sale must retry after mapping becomes possible");
    await service.RunAsync(default);
    Check(handler.Reports.Count == 2, "Resolved unmatched sale must be sent only once");

    adapter.Rows[2] = adapter.Rows[2] with { Status = "Cancelled" };
    await service.RunAsync(default);
    Check(Quantity(service, "B") == 10 && handler.Reports.Last().Quantity == -2, "Cancellation must restore a tracked sale exactly once");
    await service.RunAsync(default);
    Check(handler.Reports.Count == 3, "Repeated cancellation must be idempotent");

    handler.FailRead = true;
    var calls = adapter.Pages.Count;
    await service.RunAsync(default);
    Check(!service.LastRunSucceeded && adapter.Pages.Count == calls, "Source failure must stop sales scanning and stale distribution");
    Check(Quantity(service, "B") == 10, "Source failure must retain the last visible stock");
    handler.FailRead = false;
    adapter.RepeatPage = true;
    await service.RunAsync(default);
    Check(!service.LastRunSucceeded && handler.Reports.Count == 3, "Repeated upstream pages must fail without partial deductions");
    adapter.RepeatPage = false;
    await service.RunAsync(default);
    Check(service.LastRunSucceeded, "A blocked sync must recover");
    Check(adapter.Starts.Last() <= DateTimeOffset.UtcNow.AddDays(-2).AddMinutes(1).ToUnixTimeMilliseconds(), "Order-date marketplaces must keep scanning the last two days");
    handler.DuplicateRow = true;
    await service.RunAsync(default);
    Check(!service.LastRunSucceeded && View(service).GetProperty("lastError").GetString()!.Contains("tekrarlanan") && Quantity(service, "B") == 10, "Duplicate source rows must block distribution without throwing");
    handler.DuplicateRow = false;
    await service.RunAsync(default);
    Check(service.LastRunSucceeded, "Sync must recover after duplicate source rows are fixed");
    var before = service.Settings;
    var blocking = Path.Combine(folder, "Data", "central-stock-real.json.tmp");
    Directory.CreateDirectory(blocking);
    try { service.UpdateSettings(new(false, 300, 5)); throw new Exception("Failed persistence accepted"); }
    catch (UnauthorizedAccessException) { }
    catch (IOException) { }
    finally { Directory.Delete(blocking); }
    Check(service.Settings == before, "Failed disk write must roll back memory");

    var demoConfig = new ConfigurationBuilder().Build();
    var demoService = new CentralStockService(env, provider, demoConfig);
    await demoService.RunAsync(default);
    Check(!View(demoService).GetProperty("stores").EnumerateArray().Any(s => s.GetProperty("mode").GetString() == "real"), "Demo sync must exclude real stores");
    var reportsBefore = handler.Reports.Count;
    var demoStore = registry.Connections["trendyol"];
    var claim = demo.Enqueue(demoStore.Settings, new("claim-approve", "sync-return-test", Reference: "DEMO-IADE-01"));
    claim.DueAt = DateTimeOffset.UtcNow.AddSeconds(-1);
    demo.ProcessPendingJobs();
    await demoService.RunAsync(default);
    Check(Quantity(demoService, "SWT-GRI-M") == 31, "Approved demo return must reach central source");
    await demoService.RunAsync(default);
    Check(Quantity(demoService, "SWT-GRI-M") == 31 && handler.Reports.Count == reportsBefore, "Demo return must not be counted twice or reach a real ERP");

    var frozenView = demoService.View();
    var frozenJson = JsonSerializer.Serialize(frozenView);
    await demoService.DemoSaleAsync(new("trendyol", "8691000000005", 2), default);
    await demoService.RunAsync(default);
    Check(Quantity(demoService, "PNT-BEJ-38") == 7, "Demo sale must remain idempotent on the next scan");
    Check(JsonSerializer.Serialize(frozenView) == frozenJson, "A response snapshot must not change during a later background run");

    // Legacy state: before real and demo had separate files, a real source wrote central-stock.json.
    static TestEnvironment Legacy(string root, CentralStockState old)
    {
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        File.WriteAllText(Path.Combine(root, "Data", "central-stock.json"), JsonSerializer.Serialize(old, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return new TestEnvironment { ContentRootPath = root };
    }
    var realLegacy = new CentralStockService(Legacy(Path.Combine(folder, "legacy-real"), new CentralStockState {
        Settings = new(true, 120, 3),
        Items = { ["sku:ERP-ONLY"] = new() { Sku = "ERP-ONLY", Quantity = 4 } },
        WatchedStores = { "real-fixture", "trendyol" },
        SeenOrderLines = { "real-fixture|old-a|line-old-a|A-barcode" },
        Sales = { new() { Id = "legacy-real", StoreId = "real-fixture", Sku = "A", Barcode = "A-barcode", Quantity = 1, ReportError = "AllowWrites kapalı" },
                  new() { Id = "legacy-demo", StoreId = "trendyol", Sku = "A", Barcode = "A-barcode", Quantity = 1 } } }), provider, config);
    var legacySales = View(realLegacy).GetProperty("sales").EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToArray();
    Check(legacySales.SequenceEqual(new[] { "legacy-real" }) && !realLegacy.Settings.AutoSync && realLegacy.Settings.SafetyStock == 3, "Legacy real state must carry only real unreported sales, with auto sync off");
    await realLegacy.RunAsync(default);
    Check(handler.Reports.Any(r => r.Id == "legacy-real") && !handler.Reports.Any(r => r.Id == "legacy-demo"), "A migrated real sale must reach the source; a demo sale never");
    var demoLegacy = new CentralStockService(Legacy(Path.Combine(folder, "legacy-demo"), new CentralStockState {
        Items = { ["sku:SWT-GRI-M"] = new() { Sku = "SWT-GRI-M", Quantity = 30 } },
        Sales = { new() { Id = "interrupted-demo", StoreId = "real-fixture", Sku = "A", Barcode = "A-barcode", Quantity = 1 } } }), provider, config);
    Check(View(demoLegacy).GetProperty("sales").GetArrayLength() == 0, "Demo-source legacy state must not be migrated to a real source");
    // Review regressions, each against its own fresh real source.
    async Task<(CentralStockService Service, OrdersFixture Orders, SourceFixture Source)> Fresh(string name, string marketplace)
    {
        var root = Path.Combine(folder, "review-" + name); Directory.CreateDirectory(root);
        var e = new TestEnvironment { ContentRootPath = root };
        var r = new IntegrationRegistry(e);
        r.Connections["review-" + name] = new("review-" + name, new("Review " + name, marketplace, "real", "production"), "untested", null, null, null, null, null);
        var src = new SourceFixture(); var orders = new OrdersFixture { Market = marketplace };
        var p = new ServiceCollection().AddSingleton<IConfiguration>(config).AddSingleton<IWebHostEnvironment>(e).AddSingleton(TimeProvider.System).AddSingleton<StockOutbox>().AddSingleton(r).AddSingleton(new DemoWorkspace(e))
            .AddSingleton<IHttpClientFactory>(src).AddTransient<CentralStockSource>().AddSingleton<IMarketplaceAdapter>(orders).BuildServiceProvider();
        var s = new CentralStockService(e, p, config);
        orders.Rows = [OrdersFixture.Order("seed-" + name, "A")];
        await s.RunAsync(default);
        Check(s.LastRunSucceeded && Quantity(s, "A") == 10, "Review fixture must baseline " + name);
        return (s, orders, src);
    }

    // Trendyol scans by last update: an order placed before watching that is delivered later is not a new sale.
    var (watch, watchOrders, watchSource) = await Fresh("prewatch", "trendyol");
    watchOrders.Rows.Add(OrdersFixture.Order("placed-before-watch", "A", 3) with { Status = "Delivered", OrderDate = DateTimeOffset.UtcNow.AddDays(-5) });
    await watch.RunAsync(default);
    Check(watch.LastRunSucceeded && Quantity(watch, "A") == 10 && watchSource.Reports.Count == 0, "An order placed before watching must not be deducted when it surfaces later");
    watchOrders.Rows.Add(OrdersFixture.Order("placed-after-watch", "A", 2));
    await watch.RunAsync(default);
    Check(Quantity(watch, "A") == 8 && watchSource.Reports.Count == 1, "An order placed after watching must still be deducted");

    // A split package keeps its old id as UnPacked while its lines move to new packages.
    var (split, splitOrders, splitSource) = await Fresh("split", "trendyol");
    splitOrders.Rows.Add(OrdersFixture.Order("pkg-1", "A", 2));
    await split.RunAsync(default);
    Check(Quantity(split, "A") == 8, "Split fixture sale must be deducted");
    splitOrders.Rows[1] = splitOrders.Rows[1] with { Status = "UnPacked" };
    splitOrders.Rows.Add(OrdersFixture.Order("pkg-2", "A", 1)); splitOrders.Rows.Add(OrdersFixture.Order("pkg-3", "A", 1));
    await split.RunAsync(default);
    Check(split.LastRunSucceeded && Quantity(split, "A") == 8, "A split package must not be deducted twice");

    // Amazon-fulfilled (FBA) units are not in the stock application; merchant-fulfilled ones are.
    var (fba, fbaOrders, fbaSource) = await Fresh("fba", "amazon");
    fbaOrders.Rows.Add(OrdersFixture.Order("fba-order", "A", 4) with { FulfilledBy = "AMAZON" });
    fbaOrders.Rows.Add(OrdersFixture.Order("fbm-order", "A", 1) with { FulfilledBy = "MERCHANT" });
    await fba.RunAsync(default);
    Check(fba.LastRunSucceeded && Quantity(fba, "A") == 9 && fbaSource.Reports.Count == 1, "Amazon-fulfilled lines must not reduce central stock");

    Console.WriteLine("PASS synchronization regressions: all pages, alias deduplication, demo/real isolation, unmatched retry, cancellation/return idempotency, source failure, repeated pages, duplicate source rows, two-day window, legacy migration, recovery, disk rollback, pre-watch orders, split packages and FBA lines. Mock transports only.");
}
finally
{
    var target = Path.GetFullPath(folder);
    if (!target.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("marketpilot-sync-check-")) throw new Exception("Unsafe cleanup path");
    Directory.Delete(target, true);
}

sealed class OrdersFixture : IMarketplaceAdapter
{
    public string Market = "hepsiburada";
    public string Marketplace => Market;
    public List<MarketplaceOrder> Rows = new();
    public List<int> Pages = new();
    public List<long?> Starts = new();
    public bool RepeatPage;
    public static MarketplaceOrder Order(string id, string sku, int quantity = 1) => new(id, id, "Created", DateTimeOffset.UtcNow, "Fixture", "", quantity * 10, quantity * 10, 0, "TRY", "", "", null,
        [new("line-" + id, sku, sku + "-barcode", sku, quantity, 10, quantity * 10, 20, "TRY", "Created", "")], new("", "", "", "", "", null, null), new("", "", "", false, "", "", "", null, []));
    public Task<MarketplacePage<MarketplaceOrder>> OrdersAsync(ConnectionSettings settings, OrderQuery query, CancellationToken ct)
    {
        Pages.Add(query.Page); Starts.Add(query.StartDate);
        var row = Rows[RepeatPage ? 0 : query.Page];
        return Task.FromResult(new MarketplacePage<MarketplaceOrder>([row], query.Page, 1, Rows.Count, Rows.Count, null));
    }
    public Task<MarketplacePage<MarketplaceProduct>> ProductsAsync(ConnectionSettings s, ProductQuery q, CancellationToken ct) => throw new Exception("Unexpected product request");
    public Task<MarketplaceAccount> TestAsync(ConnectionSettings s, CancellationToken ct) => throw new Exception("Unexpected account request");
    public Task<MarketplaceAccount> AccountAsync(ConnectionSettings s, CancellationToken ct) => throw new Exception("Unexpected account request");
}
sealed class SourceFixture : HttpMessageHandler, IHttpClientFactory
{
    public Dictionary<string, int> Stock = new() { ["A"] = 10, ["B"] = 10 };
    public List<(string Id, int Quantity)> Reports = new();
    private readonly HashSet<string> received = new();
    public bool FailRead, DuplicateRow;
    public HttpClient CreateClient(string name) => new(this, false);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method == HttpMethod.Get)
        {
            var rows = Stock.Select(x => new { sku = x.Key, barcode = x.Key + "-barcode", name = x.Key, quantity = x.Value }).ToList();
            if (DuplicateRow) rows.Add(rows[0] with { name = "Duplicate" });
            return new(FailRead ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(rows), Encoding.UTF8, "application/json") };
        }
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var root = body.RootElement; var id = root.GetProperty("requestId").GetString()!;
        var quantity = root.GetProperty("quantity").GetInt32();
        if (received.Add(id)) { Reports.Add((id, quantity)); Stock[root.GetProperty("sku").GetString()!] -= quantity; }
        return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
    }
}
sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "MarketPilot.Web";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
