using System.Net;
using System.Text;
using System.Text.Json;
using MarketPilot.Web.Catalog;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Demo;
using MarketPilot.Web.Integrations.Trendyol;
using MarketPilot.Web.Integrations.CentralStock;
using MarketPilot.Web.Synchronization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
var folder = Path.Combine(Path.GetTempPath(), "marketpilot-automation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var env = new FixtureEnvironment { ContentRootPath = folder };
    var clock = new ManualClock();
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["Synchronization:IncludeRealStores"] = "true", ["Synchronization:AllowRealWrites"] = "false",
        ["Marketplaces:Trendyol:stage:SellerId"] = "123", ["Marketplaces:Trendyol:stage:ApiKey"] = "fixture-key",
        ["Marketplaces:Trendyol:stage:ApiSecret"] = "fixture-secret", ["Marketplaces:Trendyol:stage:AllowWrites"] = "true"
    }).Build();
    var registry = new IntegrationRegistry(env);
    var demo = new DemoWorkspace(env);
    var orders = new FixtureAdapter("hepsiburada");
    var trendyol = new FixtureAdapter("trendyol");
    var transport = new FixtureTransport();
    var client = new TrendyolApiClient(new HttpClient(transport), config);
    var real = new ConnectionState("real-orders", new("Fixture account", "hepsiburada", "real", "production"), "untested", null, null, null, null, null);
    registry.Connections[real.Id] = real;
    using var services = new ServiceCollection().AddSingleton<IWebHostEnvironment>(env).AddSingleton<IConfiguration>(config)
        .AddSingleton<TimeProvider>(clock).AddSingleton(registry).AddSingleton(demo).AddSingleton<DemoStore>()
        .AddSingleton<OperationsHub>().AddSingleton<StockOutbox>().AddSingleton<OrderSynchronization>()
        .AddSingleton<IMarketplaceAdapter>(orders).AddSingleton<IMarketplaceAdapter>(trendyol)
        .AddSingleton<IHttpClientFactory>(new FixtureHttpFactory(transport)).AddSingleton<MarketplaceClients>().AddSingleton<MarketplaceStockWriter>()
        .AddSingleton(client).AddSingleton<TrendyolOperationRunner>().BuildServiceProvider();
    var sync = services.GetRequiredService<OrderSynchronization>();
    var db = services.GetRequiredService<DemoStore>();
    orders.Rows = [FixtureAdapter.Order("one"), FixtureAdapter.Order("two")];
    sync.Configure(new(true, 30, false, 2));
    await sync.Tick(default);
    Check(orders.OrderCalls == 2 && db.CatalogRead(s => s.Catalog.Orders.Count) == 2, "Scheduled polling must persist every page into shared orders");
    await sync.Tick(default);
    Check(orders.OrderCalls == 2, "Timer interval must prevent repeat polling");
    var restartedSync = new OrderSynchronization(env, services, config, clock);
    await restartedSync.Tick(default);
    Check(orders.OrderCalls == 2, "Restart must preserve the next scheduled poll");
    clock.Advance(31); orders.Rows.Add(FixtureAdapter.Order("three"));
    await restartedSync.Tick(default);
    Check(db.CatalogRead(s => s.Catalog.Orders.Count) == 3, "A later scheduled poll must import new orders without a UI request");
    orders.Fail = true; clock.Advance(31); await restartedSync.Tick(default);
    Check(db.CatalogRead(s => s.Catalog.Orders.Count) == 3 && Json(restartedSync.Status()).GetProperty("stores").EnumerateArray().Any(s => s.GetProperty("state").GetString() == "retry"), "Failed pulls must preserve orders and persist retry state");
    var calls = orders.OrderCalls; await restartedSync.Tick(default); Check(orders.OrderCalls == calls, "Failure backoff must prevent immediate retry");
    orders.Fail = false; clock.Advance(31); await restartedSync.Tick(default);
    restartedSync.Configure(new(false, 30, false, 2));
    calls = orders.OrderCalls; clock.Advance(60); await restartedSync.Tick(default); Check(orders.OrderCalls == calls, "Disabled scheduling must stop new polls");

    var outbox = services.GetRequiredService<StockOutbox>();
    var demoStore = registry.Connections["trendyol"];
    var product = demo.Snapshot(demoStore.Settings).Products[0];
    var job = outbox.Enqueue(demoStore, product.Barcode, 42, product.SalePrice, "fixture");
    Check(outbox.Enqueue(demoStore, product.Barcode, 42, product.SalePrice, "fixture").Id == job.Id, "Repeated enqueue must reuse the active job");
    await outbox.ProcessDue(default);
    Check(Json(outbox.Status()).GetProperty("waiting").GetInt32() == 1, "Sending a demo action must wait for completion");
    var providerJob = demo.Enqueue(demoStore.Settings, new("stock", "outbox-" + job.Id, product.Barcode, 42, product.SalePrice));
    providerJob.DueAt = DateTimeOffset.UtcNow.AddSeconds(-1); demo.ProcessPendingJobs();
    var recovered = new StockOutbox(env, registry, demo, services, config, clock);
    clock.Advance(2); await recovered.ProcessDue(default);
    Check(demo.Snapshot(demoStore.Settings).Products[0].Stock == 42 && Json(recovered.Status()).GetProperty("completed").GetInt32() == 1, "A restarted outbox must finish a persisted provider job exactly once");
    Check(demo.Snapshot(demoStore.Settings).Jobs.Count(j => j.RequestId == "outbox-" + job.Id) == 1, "Recovery must not duplicate the provider job");

    var live = real with { Id = "real-stock", Settings = new("Fixture stock", "trendyol", "real", "stage") };
    registry.Connections[live.Id] = live;
    var guarded = recovered.Enqueue(live, "FIXTURE-BARCODE", 42, null, "fixture",requestId:"fixture-replay");
    await recovered.ProcessDue(default);
    Check(Json(recovered.Status()).GetProperty("blocked").GetInt32() == 1 && transport.Calls == 0, "Real write guard must block outbound calls");
    config["Synchronization:AllowRealWrites"] = "true";
    recovered.Retry(guarded.Id); transport.Code = HttpStatusCode.TooManyRequests;
    await recovered.ProcessDue(default); Check(transport.Calls == 1, "Mock 429 must be reached once");
    await recovered.ProcessDue(default); Check(transport.Calls == 1, "Rate-limit backoff must survive extra worker ticks");
    clock.Advance(21); await recovered.ProcessDue(default); Check(transport.Calls == 2, "Due retry must reuse the queued target");
    transport.Code = HttpStatusCode.OK; clock.Advance(41); await recovered.ProcessDue(default);
    Check(Json(recovered.Status()).GetProperty("waiting").GetInt32() == 1, "HTTP acceptance must not count as completion");
    trendyol.Stock = 42; clock.Advance(16); await recovered.ProcessDue(default);
    Check(recovered.ConfirmedStock(live.Id, "FIXTURE-BARCODE") == 42, "Readback must confirm the exact target");

    Check(recovered.Enqueue(live,"FIXTURE-BARCODE",42,null,"fixture",requestId:"fixture-replay").Id==guarded.Id,"Replay must retain the same job after native SKU resolution and completion");
    try{recovered.Enqueue(live,"FIXTURE-BARCODE",43,null,"fixture",requestId:"fixture-replay");throw new Exception("Different target accepted under same request ID");}
    catch(IntegrationException e){Check(e.Code=="idempotency_conflict","Replay conflict classification");}
    var interrupted = recovered.Enqueue(live, "INTERRUPTED", 55, null, "fixture");
    var outboxFile = Path.Combine(folder, "Data", "marketplace-outbox.json");
    var raw = JsonSerializer.Deserialize<List<StockWriteJob>>(File.ReadAllText(outboxFile), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    raw.Single(j => j.Id == interrupted.Id).State = "dispatching";
    File.WriteAllText(outboxFile, JsonSerializer.Serialize(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    var uncertain = new StockOutbox(env, registry, demo, services, config, clock);
    trendyol.Stock = 55; var beforeSend = transport.Calls;
    await uncertain.ProcessDue(default);
    Check(transport.Calls == beforeSend && uncertain.ConfirmedStock(live.Id, "INTERRUPTED") == 55, "Interrupted real dispatch must verify without resending");

    var oldTarget = uncertain.Enqueue(demoStore, product.Barcode, 77, product.SalePrice, "ordered target");
    await uncertain.ProcessDue(default);
    var newTarget = uncertain.Enqueue(demoStore, product.Barcode, 78, product.SalePrice, "ordered target");
    Check(newTarget.Sequence > oldTarget.Sequence, "Targets must keep durable ordering even when clock timestamps are equal");
    await uncertain.ProcessDue(default);
    Check(!demo.Snapshot(demoStore.Settings).Jobs.Any(j => j.RequestId == "outbox-" + newTarget.Id), "A newer target must wait for the in-flight old target");
    demo.Enqueue(demoStore.Settings, new("stock", "outbox-" + oldTarget.Id, product.Barcode, 77, product.SalePrice)).DueAt = DateTimeOffset.UtcNow.AddSeconds(-1);
    demo.ProcessPendingJobs(); clock.Advance(2); await uncertain.ProcessDue(default); await uncertain.ProcessDue(default);
    demo.Enqueue(demoStore.Settings, new("stock", "outbox-" + newTarget.Id, product.Barcode, 78, product.SalePrice)).DueAt = DateTimeOffset.UtcNow.AddSeconds(-1);
    demo.ProcessPendingJobs(); clock.Advance(2); await uncertain.ProcessDue(default);
    Check(uncertain.ConfirmedStock(demoStore.Id, product.Barcode) == 78, "The newest ordered target must become the confirmed stock");

    var barcodeTarget=uncertain.Enqueue(demoStore,product.Barcode,80,product.SalePrice,"mixed identity",requestId:"barcode-replay");
    await uncertain.ProcessDue(default);
    var skuTarget=uncertain.Enqueue(demoStore,"sku:"+product.Sku,81,product.SalePrice,"mixed identity",nativeSku:product.Sku);
    await uncertain.ProcessDue(default);
    Check(!demo.Snapshot(demoStore.Settings).Jobs.Any(j=>j.RequestId=="outbox-"+skuTarget.Id),"SKU and barcode targets must share the same product ordering");
    demo.Enqueue(demoStore.Settings,new("stock","outbox-"+barcodeTarget.Id,product.Barcode,80,product.SalePrice)).DueAt=DateTimeOffset.UtcNow.AddSeconds(-1);
    demo.ProcessPendingJobs();clock.Advance(2);await uncertain.ProcessDue(default);await uncertain.ProcessDue(default);
    demo.Enqueue(demoStore.Settings,new("stock","outbox-"+skuTarget.Id,product.Barcode,81,product.SalePrice)).DueAt=DateTimeOffset.UtcNow.AddSeconds(-1);
    demo.ProcessPendingJobs();clock.Advance(2);await uncertain.ProcessDue(default);
    Check(uncertain.ConfirmedStock(demoStore.Id,product.Barcode)==81,"A SKU-only completion must be visible under its resolved barcode");
    Check(uncertain.Enqueue(demoStore,product.Barcode,80,product.SalePrice,"mixed identity",requestId:"barcode-replay").Id==barcodeTarget.Id,"Resolved demo identity must not invalidate request idempotency");

    var accountBound = uncertain.Enqueue(live, "ACCOUNT-BOUND", 99, null, "account guard");
    config["Marketplaces:Trendyol:stage:SellerId"] = "456";
    beforeSend = transport.Calls; await uncertain.ProcessDue(default);
    Check(transport.Calls == beforeSend && Json(uncertain.Status()).GetProperty("recent").EnumerateArray().Single(j => j.GetProperty("id").GetString() == accountBound.Id).GetProperty("state").GetString() == "blocked", "A changed seller account must not receive an older queued job");
    Check(uncertain.ConfirmedStock(live.Id, "FIXTURE-BARCODE") is null, "A new account must not inherit previous stock confirmation");
    config["Marketplaces:Trendyol:stage:SellerId"] = "123";

    // Pruning finished history must keep the newest confirmation per product, or unchanged stock is resent on every run.
    var history = JsonSerializer.Deserialize<List<StockWriteJob>>(File.ReadAllText(outboxFile), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    var lastSequence = history.Max(j => j.Sequence);
    for (var i = 0; i < 2100; i++)
        history.Add(new StockWriteJob { StoreId = "filler", Source = "filler", Barcode = "FILLER-" + i, State = "superseded", Sequence = lastSequence + i + 1, CreatedAt = clock.GetUtcNow().AddMinutes(1), FinishedAt = clock.GetUtcNow().AddMinutes(1) });
    File.WriteAllText(outboxFile, JsonSerializer.Serialize(history, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    var pruned = new StockOutbox(env, registry, demo, services, config, clock);
    pruned.Enqueue(demoStore, "PRUNE-TRIGGER", 1, product.SalePrice, "prune");
    Check(pruned.ConfirmedStock(demoStore.Id, product.Barcode) == 81, "Pruning must keep the newest confirmed stock of every product");

    // A failed target must be retried after a cool-down rather than being returned as a dead duplicate forever.
    var failing = pruned.Enqueue(demoStore, product.Barcode, 90, product.SalePrice, "failure retry");
    // The worker takes one job per store per tick, so drain the earlier prune trigger first.
    for (var tick = 0; tick < 5 && pruned.Find(failing.Id)?.State != "waiting"; tick++) { clock.Advance(2); await pruned.ProcessDue(default); }
    Check(pruned.Find(failing.Id)?.State == "waiting", "Fixture target must reach the demo provider");
    var failedProvider = demo.Enqueue(demoStore.Settings, new("stock", "outbox-" + failing.Id, product.Barcode, 90, product.SalePrice));
    failedProvider.State = "failed"; failedProvider.Error = "fixture failure";
    for (var tick = 0; tick < 5 && pruned.Find(failing.Id)?.State != "failed"; tick++) { clock.Advance(2); await pruned.ProcessDue(default); }
    Check(pruned.Find(failing.Id)?.State == "failed", "Fixture provider failure must fail the outbox job");
    Check(pruned.Enqueue(demoStore, product.Barcode, 90, product.SalePrice, "failure retry").Id == failing.Id, "Within the cool-down a failed target must not be resent");
    clock.Advance(16 * 60);
    var requeued = pruned.Enqueue(demoStore, product.Barcode, 90, product.SalePrice, "failure retry");
    Check(requeued.Id != failing.Id && requeued.State == "queued", "After the cool-down a failed target must be queued again");

    // Windows can report an open destination as ACCESS_DENIED rather than an IOException.
    if(OperatingSystem.IsWindows())
    {
        var held=new FileStream(outboxFile,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        var releaser=new Thread(()=>{Thread.Sleep(60);held.Dispose();}){IsBackground=true};
        releaser.Start();
        try
        {
            var retried=uncertain.Enqueue(demoStore,product.Barcode,68,product.SalePrice,"transient Windows destination lock");
            Check(uncertain.Find(retried.Id)?.Quantity==68,"Temporary read locks must not lose an accepted stock target");
        }
        finally{releaser.Join();held.Dispose();}
    }

    var snapshot = Json(uncertain.Status()).GetRawText();
    var blocker = outboxFile + ".tmp"; Directory.CreateDirectory(blocker);
    try { uncertain.Enqueue(demoStore, product.Barcode, 66, product.SalePrice, "disk fixture"); throw new Exception("Disk failure was accepted"); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
    finally { Directory.Delete(blocker); }
    Check(Json(uncertain.Status()).GetRawText() == snapshot, "Failed queue persistence must roll back memory");
    Console.WriteLine("PASS automatic synchronization/outbox: scheduled multi-page import, interval and restart recovery, failure backoff, disabled polling, durable dispatch, idempotency, real-write guard, 429 retry, readback, uncertain-send recovery, transient Windows read locks and disk rollback. All transports mocked.");
}
finally
{
    var absolute = Path.GetFullPath(folder);
    if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("marketpilot-automation-")) throw new Exception("Unsafe cleanup");
    Directory.Delete(absolute, true);
}
sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(int seconds) => now = now.AddSeconds(seconds);
}
sealed class FixtureAdapter(string marketplace) : IMarketplaceAdapter
{
    public string Marketplace => marketplace;
    private string lastBarcode="";
    public List<MarketplaceOrder> Rows = new(); public int OrderCalls; public bool Fail; public int Stock;
    public static MarketplaceOrder Order(string id) => new(id, id, "Created", DateTimeOffset.UtcNow, "Fixture", "", 10, 10, 0, "TRY", "", "", null,
        [new(id + "-line", id, id, "Fixture", 1, 10, 10, 20, "TRY", "Created", "")], new("", "", "", "", "", null, null), new("", "", "", false, "", "", "", null, []), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    public Task<MarketplacePage<MarketplaceOrder>> OrdersAsync(ConnectionSettings s, OrderQuery q, CancellationToken ct)
    {
        OrderCalls++; if (Fail) throw new HttpRequestException("fixture");
        return Task.FromResult(new MarketplacePage<MarketplaceOrder>([Rows[q.Page]], q.Page, 1, Rows.Count, Rows.Count, null));
    }
    public Task<MarketplacePage<MarketplaceProduct>> ProductsAsync(ConnectionSettings s, ProductQuery q, CancellationToken ct) {
        if(q.Barcode is not null)lastBarcode=q.Barcode;
        return Task.FromResult(new MarketplacePage<MarketplaceProduct>([new("id", "", "", "SKU", q.Barcode ?? lastBarcode, "Fixture", "", "", "", 10, 10, 10, Stock, "TRY", true, "", [], "approved", [])], 0, 1, 1, 1, null));
    }
    public Task<MarketplaceAccount> TestAsync(ConnectionSettings s, CancellationToken ct) => throw new Exception("Unexpected account call");
    public Task<MarketplaceAccount> AccountAsync(ConnectionSettings s, CancellationToken ct) => throw new Exception("Unexpected account call");
}
sealed class FixtureTransport : HttpMessageHandler
{
    public int Calls; public HttpStatusCode Code = HttpStatusCode.OK;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    { Calls++; return Task.FromResult(new HttpResponseMessage(Code) { Content = new StringContent("{\"batchRequestId\":\"fixture-batch\"}", Encoding.UTF8, "application/json") }); }
}
sealed class FixtureEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "MarketPilot.Web"; public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = ""; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = ""; public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}

sealed class FixtureHttpFactory(HttpMessageHandler handler):IHttpClientFactory { public HttpClient CreateClient(string name)=>new(handler,false); }
