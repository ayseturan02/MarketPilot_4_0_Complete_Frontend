using MarketPilot.Web.Wms;
using MarketPilot.Web.Contracts;
using MarketPilot.Web.Integrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

static async Task Reject(Func<Task> action, string expectedCode)
{
    try
    {
        await action();
    }
    catch (WmsProviderException error)
    {
        Check(error.Error.Code == expectedCode, $"Expected {expectedCode}, got {error.Error.Code}.");
        return;
    }
    throw new Exception($"Expected WMS error {expectedCode}.");
}

var profile = new WmsConnectionProfile("mock-wms", "tenant-a", AllowMockWrites: true);
var provider = new MockWmsProvider(profile);
var services = new ServiceCollection();
var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspace:BusinessId"] = "tenant-b" })
    .Build();
services.AddWmsProviders(configuration);
using (var serviceProvider = services.BuildServiceProvider())
    Check(serviceProvider.GetRequiredService<IWmsProvider>().Profile.TenantId == "tenant-b", "Provider DI and tenant profile.");

var warehouses = await provider.ReadWarehousesAsync();
Check(warehouses.IsMock && warehouses.Data.Count == 2, "Deterministic warehouse fixtures.");
Check(warehouses.Data.SequenceEqual((await provider.ReadWarehousesAsync()).Data), "Stable fixture data.");

var stock = await provider.ReadStockAsync("mock-main");
Check(stock.Data.Count == 4 && stock.Data.Single(row => row.Sku == "TSH-EKR-S").OnHandQuantity == 36,
    "Warehouse stock snapshot.");
var movements = await provider.ReadStockMovementsAsync("mock-main", new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero));
Check(movements.Data.Count == 1 && movements.Data.Single().QuantityDelta == -4, "Stock movement cursor.");
await Reject(async () => await provider.ReadStockAsync("unknown"), "NotFound");

var order = new WmsFulfillmentOrder(
    "fulfillment-1001",
    "marketplace-order-1001",
    "mock-main",
    [new("TSH-EKR-S", 2, "EA")],
    new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero));
var submitted = await provider.SubmitFulfillmentOrderAsync(order);
Check(submitted.Data.Status == "accepted", "Fulfillment submission.");
Check((await provider.SubmitFulfillmentOrderAsync(order)).Data == submitted.Data, "Idempotent fulfillment submission.");
await Reject(async () => await provider.SubmitFulfillmentOrderAsync(order with { ExternalOrderId = "different" }), "IdempotencyConflict");
Check((await provider.GetAcceptanceStatusAsync(order.Id)).Data.Status == "accepted", "Acceptance status.");
Check((await provider.GetReservationStatusAsync(order.Id)).Data.Lines.Single().Quantity == 2, "Reservation status.");
Check((await provider.GetPickingStatusAsync(order.Id)).Data.Status == "picked", "Picking status.");
Check((await provider.GetPackingStatusAsync(order.Id)).Data.Status == "packed", "Packing status.");
Check((await provider.GetShipmentStatusAsync(order.Id)).Data.TrackingNumber == "MOCK-" + order.Id, "Shipment tracking.");
Check((await provider.CheckCancellationOrReturnEligibilityAsync(order.Id, WmsRequestType.Cancellation)).Data.IsEligible,
    "Cancellation eligibility.");
Check(!(await provider.CheckCancellationOrReturnEligibilityAsync(order.Id, WmsRequestType.Return)).Data.IsEligible,
    "Return eligibility.");

var readOnly = new MockWmsProvider(profile with { AllowMockWrites = false });
Check(!readOnly.Capabilities.Supports(WmsCapability.FulfillmentSubmission), "Write capability is disabled.");
await Reject(async () => await readOnly.SubmitFulfillmentOrderAsync(order), "NotSupported");
var unsupported = new MockWmsProvider(profile,
    capabilities: new WmsCapabilities(WmsCapability.Warehouses, true));
await Reject(async () => await unsupported.ReadStockAsync("mock-main"), "NotSupported");
var realMode = new MockWmsProvider(profile with { Mode = "real" });
Check(realMode.Capabilities.Supported == WmsCapability.None && !realMode.Capabilities.IsMock,
    "No assumed native provider capabilities.");
await Reject(async () => await realMode.ReadWarehousesAsync(), "NotSupported");
await Reject(async () => await new MockWmsProvider(profile with { Enabled = false }).ReadWarehousesAsync(), "Disabled");

var failure = new MockWmsProvider(profile, new WmsMockBehavior(FailureOperation: WmsMockOperations.Stock));
await Reject(async () => await failure.ReadStockAsync("mock-main"), "SimulatedFailure");
var delayed = new MockWmsProvider(profile, new WmsMockBehavior(DelayMilliseconds: 20));
var started = System.Diagnostics.Stopwatch.StartNew();
await delayed.ReadWarehousesAsync();
Check(started.ElapsedMilliseconds >= 15, "Configured mock latency.");
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try
    {
        await delayed.ReadWarehousesAsync(cancellation.Token);
        throw new Exception("Cancellation was ignored.");
    }
    catch (OperationCanceledException)
    {
    }
}

var testFolder = Path.Combine(Path.GetTempPath(), "marketpilot-wms-stock-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testFolder);
try
{
    var environment = new WmsTestEnvironment(testFolder);
    var db = new DemoStore(environment);
    var registry = new IntegrationRegistry(environment);
    var canonical = new CanonicalStore(db, new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspace:BusinessId"] = "default" })
        .Build());
    var inventory = new WmsInventoryService(provider, canonical, registry, environment);
    var now = DateTimeOffset.UtcNow;
    RecordContext Context(string id, long version = 1) =>
        new("default", "nebim-v3", id, null, "wms-check", id + "-" + version, version, now, now);
    canonical.Apply(new CanonicalProduct("erp-product", Context("erp-product"), "Mock ürün", "Giyim", "Mock"));
    canonical.Apply(new ProductVariant("erp-variant", Context("erp-variant"), "erp-product", "TSH-EKR-S",
        "Ekru", "S", "EA", true, []));
    canonical.Apply(new Warehouse("erp-main", Context("erp-main"), "MAIN", "ERP Ana Depo"));
    canonical.Apply(new StockPosition("erp-stock", Context("erp-stock"), "erp-variant", "erp-main", 25, "EA"));

    var overview = await inventory.ReadAsync();
    var mainStock = overview.Stock.Single(row => row.Snapshot.WarehouseId == "mock-main" && row.Snapshot.Sku == "TSH-EKR-S");
    Check(mainStock.SellableQuantity == 30 && mainStock.Snapshot.ReservedQuantity == 4,
        "Approved sellable rule deducts reservations and separately tracked unusable stock.");
    Check(overview.Stock.Any(row => row.Snapshot.ProviderManaged && row.SellableQuantity is null),
        "Provider-managed FBA/n11depom stock is protected from merchant sellable totals.");
    Check(overview.Discrepancies.Any(row => row.WarehouseCode == "MAIN" && row.Sku == "TSH-EKR-S" &&
        row.WmsQuantity == 36 && row.ErpQuantity == 25 && row.State == "conflict"),
        "ERP/WMS conflict displays both values without choosing a winner.");

    var updatedPolicy = await inventory.UpdatePolicyAsync(new(
        new Dictionary<string, decimal> { ["mock-main"] = 5, ["mock-outlet"] = 0 },
        new Dictionary<string, string[]> { ["trendyol"] = ["mock-main", "mock-outlet"] },
        new Dictionary<string, decimal> { ["trendyol"] = 20 }));
    Check(updatedPolicy.SafetyStockByWarehouse["mock-main"] == 5, "Warehouse safety-stock configuration.");
    overview = await inventory.ReadAsync();
    Check(overview.ChannelStock.Where(row => row.AccountId == "trendyol" && row.Sku == "TSH-EKR-S")
        .All(row => row.SellableQuantity == 20), "Multi-depot assignment and channel stock cap.");

    var baseline = mainStock.Snapshot;
    var stale = inventory.ApplySnapshot(baseline with
    {
        OnHandQuantity = 4,
        SourceVersion = 1,
        CapturedAt = baseline.CapturedAt.AddDays(-1)
    });
    Check(stale == "stale", "Old WMS source versions cannot replace current stock.");
    var conflict = inventory.ApplySnapshot(baseline with { OnHandQuantity = 18 });
    Check(conflict == "conflict", "Different stock content at the same source version requires review.");
    var negative = inventory.ApplySnapshot(baseline with
    {
        OnHandQuantity = -2,
        SourceVersion = 3,
        CapturedAt = baseline.CapturedAt.AddMinutes(1)
    });
    Check(negative == "applied", "A newer negative snapshot is retained for review.");
    overview = await inventory.ReadAsync();
    mainStock = overview.Stock.Single(row => row.Snapshot.WarehouseId == "mock-main" && row.Snapshot.Sku == "TSH-EKR-S");
    Check(mainStock.SellableQuantity == 0 && mainStock.SellableState == "negative-stock-blocked",
        "Negative WMS stock is not exposed as sellable.");

    var unknown = inventory.ApplySnapshot(mainStock.Snapshot with
    {
        DamagedQuantity = null,
        SourceVersion = 4,
        CapturedAt = mainStock.Snapshot.CapturedAt.AddMinutes(1)
    });
    Check(unknown == "applied", "Unknown stock components are retained for review.");
    overview = await inventory.ReadAsync();
    mainStock = overview.Stock.Single(row => row.Snapshot.WarehouseId == "mock-main" && row.Snapshot.Sku == "TSH-EKR-S");
    Check(mainStock.SellableQuantity is null && mainStock.SellableState == "unknown",
        "Unknown WMS quantities never become a sellable zero or positive quantity.");
    Check(overview.History.Any(entry => entry.Disposition == "stale") &&
        overview.History.Any(entry => entry.Disposition == "conflict"), "Stock history preserves stale and conflict review events.");

    var reopened = new WmsInventoryService(provider, canonical, registry, environment);
    var persisted = await reopened.ReadAsync();
    Check(persisted.Stock.Single(row => row.Snapshot.WarehouseId == "mock-main" &&
        row.Snapshot.Sku == "TSH-EKR-S").SellableQuantity is null, "WMS snapshots and policy survive process restart.");
    Check(reopened.ReadMovements("mock-main", null).Count > 0, "WMS movement history is persisted.");
}
finally
{
    var absolute = Path.GetFullPath(testFolder);
    if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(absolute).StartsWith("marketpilot-wms-stock-", StringComparison.Ordinal))
        throw new Exception("Unsafe WMS test cleanup.");
    Directory.Delete(absolute, true);
}

Console.WriteLine("PASS WMS contract and multi-warehouse inventory: stock categories, approved sellable rule, reservations, per-warehouse safety stock, channel assignments/caps, provider-managed stock protection, ERP/WMS discrepancy review, stale-version rejection, unknown/negative stock blocking, movement/history persistence, provider workflows and mock failures/latency. No real stock writes.");

sealed class WmsTestEnvironment(string contentRoot) : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "Production";
    public string ApplicationName { get; set; } = "WmsChecks";
    public string WebRootPath { get; set; } = "";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
