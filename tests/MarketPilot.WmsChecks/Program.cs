using MarketPilot.Web.Wms;
using MarketPilot.Web.Contracts;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Enterprise.Nebim;
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
// A rejected request must never appear reserved, picked, packed or shipped.
var rejectingProvider = new MockWmsProvider(profile, new WmsMockBehavior(RejectWarehouseId: "mock-main"));
Check((await rejectingProvider.SubmitFulfillmentOrderAsync(order)).Data.Status == "rejected", "Explicit warehouse rejection.");
Check((await rejectingProvider.GetAcceptanceStatusAsync(order.Id)).Data.Status == "rejected", "Rejected acceptance remains queryable.");
await Reject(async () => await rejectingProvider.GetReservationStatusAsync(order.Id), "FulfillmentRejected");
await Reject(async () => await rejectingProvider.GetPickingStatusAsync(order.Id), "FulfillmentRejected");
await Reject(async () => await rejectingProvider.GetPackingStatusAsync(order.Id), "FulfillmentRejected");
await Reject(async () => await rejectingProvider.GetShipmentStatusAsync(order.Id), "FulfillmentRejected");
await Reject(async () => await rejectingProvider.CheckCancellationOrReturnEligibilityAsync(order.Id, WmsRequestType.Return), "FulfillmentRejected");

var callerLines = new List<WmsFulfillmentLine> { new("TSH-EKR-S", 2, "EA") };
var isolatedOrder = order with { Id = "isolated-request", Lines = callerLines };
await provider.SubmitFulfillmentOrderAsync(isolatedOrder);
callerLines[0] = callerLines[0] with { Quantity = 99 };
Check((await provider.GetReservationStatusAsync(isolatedOrder.Id)).Data.Lines.Single().Quantity == 2,
    "Caller mutation cannot change an accepted request.");

var otherTenant = new MockWmsProvider(profile with { TenantId = "tenant-b" });
await Reject(async () => await otherTenant.GetAcceptanceStatusAsync(order.Id), "NotFound");
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
using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
{
    var slow = new MockWmsProvider(profile, new WmsMockBehavior(DelayMilliseconds: 500));
    try
    {
        await slow.SubmitFulfillmentOrderAsync(order, timeout.Token);
        throw new Exception("In-flight cancellation was ignored.");
    }
    catch (OperationCanceledException) { }
    await Reject(async () => await slow.GetAcceptanceStatusAsync(order.Id), "NotFound");
}
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
    Check(overview.RuleApprovedAt is null && overview.Stock.All(row => row.SellableQuantity is null),
        "A new business must explicitly approve the sellable calculation.");
    await inventory.UpdatePolicyAsync(new(new(), new(), new()));
    overview = await inventory.ReadAsync();
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
    Check(inventory.ApplySnapshot(baseline with { SourceVersion = null, CapturedAt = baseline.CapturedAt.AddDays(1), OnHandQuantity = 999 }) == "conflict",
        "Unversioned events cannot erase a versioned stock snapshot.");
    var managedBaseline = overview.Stock.First(row => row.Snapshot.ProviderManaged).Snapshot;
    Check(inventory.ApplySnapshot(managedBaseline with { ProviderManaged = false, SourceVersion = 100 }) == "conflict",
        "Provider-managed stock ownership cannot be overwritten.");
    Check(overview.Stock.Single(row => row.Snapshot.WarehouseId == "mock-outlet" && row.Snapshot.Sku == "TSH-EKR-S").Snapshot.OnHandQuantity == 8,
        "Warehouse balances remain independent.");
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

    var fulfillmentRoot = Path.Combine(testFolder, "fulfillment");
    Directory.CreateDirectory(fulfillmentRoot);
    var fulfillmentEnvironment = new WmsTestEnvironment(fulfillmentRoot);
    var fulfillmentDb = new DemoStore(fulfillmentEnvironment);
    var fulfillmentConfiguration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Workspace:BusinessId"] = "default"
        })
        .Build();
    var fulfillmentCanonical = new CanonicalStore(fulfillmentDb, fulfillmentConfiguration);
    var fulfillmentRegistry = new IntegrationRegistry(fulfillmentEnvironment);
    var writableProfile = profile with { TenantId = "default", AllowMockWrites = true };
    var fulfillmentWms = new MockWmsProvider(writableProfile, new WmsMockBehavior(PackageCount: 2));
    var fulfillmentErp = new MockNebimProvider(fulfillmentDb,
        new ErpConnectionProfile("mock-nebim", "default", AllowMockWrites: true));
    var canonicalSync = new NebimCanonicalSync(fulfillmentErp, fulfillmentCanonical, fulfillmentDb);
    var canonicalSeed = await canonicalSync.Run(CancellationToken.None);
    Check((bool)canonicalSeed.GetType().GetProperty("success")!.GetValue(canonicalSeed)!,
        "Fulfillment ERP fixture canonical identities.");
    var fulfillmentInventory = new WmsInventoryService(
        fulfillmentWms, fulfillmentCanonical, fulfillmentRegistry, fulfillmentEnvironment);
    await fulfillmentInventory.UpdatePolicyAsync(new(new(), new(), new()));
    var statusPublisher = new MockFulfillmentStatusPublisher(
        writableProfile, new WmsMockBehavior(PackageCount: 2));
    var fulfillment = new FulfillmentOrchestrationService(
        fulfillmentWms, fulfillmentInventory, fulfillmentErp, fulfillmentCanonical, statusPublisher, fulfillmentEnvironment);

    var fulfillmentOrder = new FulfillmentWorkflowOrder(
        "trendyol", "seller-01", "package-fulfillment-1001", "request-fulfillment-1001", "Created",
        DateTimeOffset.UtcNow, "TRY", [new("line-01", "TSH-EKR-S", 35, "EA", 449, "TRY")]);
    var blockedBeforeApproval = await fulfillment.ReceiveAsync(fulfillmentOrder);
    Check(blockedBeforeApproval.State == "blocked-policy", "No unapproved workflow can cause external writes.");
    var approvedPolicy = new FulfillmentWorkflowPolicy(
        true,
        [FulfillmentWriteSteps.ErpOrderRegistration, FulfillmentWriteSteps.WmsFulfillmentSubmission],
        ["mock-main", "mock-outlet"],
        false,
        [FulfillmentUpdateTargets.Marketplace, FulfillmentUpdateTargets.Erp]);
    fulfillment.UpdatePolicy(approvedPolicy);
    var prepared = await fulfillment.AdvanceAsync(blockedBeforeApproval.Id);
    Check(prepared.State == "validated" && prepared.Warehouses.Length == 2 &&
        prepared.Warehouses[0].Lines[0].Quantity == 30 && prepared.Warehouses[1].Lines[0].Quantity == 5,
        "Approved policy splits a validated order across prioritized depots without over-allocation.");
    Check(prepared.Warehouses.Select(task => task.ExternalRequestId).Distinct().Count() == 2 &&
        prepared.Warehouses.All(task => task.IdempotencyKey.Length > 0),
        "Each WMS depot request receives a stable external request ID and idempotency key.");
    Check((await fulfillment.ReceiveAsync(fulfillmentOrder)).Id == prepared.Id,
        "Repeated marketplace delivery with the same idempotency key does not create a second workflow.");
    await Reject(async () => await fulfillment.ReceiveAsync(fulfillmentOrder with
    {
        IdempotencyKey = "second-request",
        Lines = [fulfillmentOrder.Lines[0] with { Quantity = 34 }]
    }), "IdempotencyConflict");

    var erpRegistered = await fulfillment.AdvanceAsync(prepared.Id);
    Check(erpRegistered.State == "erp-registered", "ERP registration occurs first when the approved policy says ERP then WMS.");
    await fulfillment.AdvanceAsync(prepared.Id);
    await fulfillment.AdvanceAsync(prepared.Id);
    var accepted = await fulfillment.AdvanceAsync(prepared.Id);
    Check(accepted.State == "wms-accepted" &&
        accepted.History.Any(entry => entry.State == "wms-accepted"),
        "WMS acceptance is explicitly separate from completed fulfillment processing.");
    await fulfillment.AdvanceAsync(prepared.Id);
    await fulfillment.AdvanceAsync(prepared.Id);
    await fulfillment.AdvanceAsync(prepared.Id);
    var processed = await fulfillment.AdvanceAsync(prepared.Id);
    Check(processed.State == "processed" && processed.Packages.Count == 4 &&
        processed.Publications.Count == 8 && processed.Publications.All(item => item.State == "acknowledged"),
        "Two warehouses, multiple packages, tracking and mock ERP/marketplace status updates complete end to end.");
    Check(processed.History.FindIndex(entry => entry.State == "erp-registered") <
        processed.History.FindIndex(entry => entry.State == "wms-accepted"),
        "Persisted status history follows the business-approved write sequence.");
    var restartedFulfillment = new FulfillmentOrchestrationService(
        fulfillmentWms, fulfillmentInventory, fulfillmentErp, fulfillmentCanonical, statusPublisher, fulfillmentEnvironment);
    Check(restartedFulfillment.Get(processed.Id).Packages.Count == 4,
        "Workflow history and package tracking survive service reconstruction.");

    var wmsFirstOrder = fulfillmentOrder with
    {
        ExternalOrderId = "package-fulfillment-1002",
        IdempotencyKey = "request-fulfillment-1002",
        Lines = [fulfillmentOrder.Lines[0] with { Quantity = 2 }]
    };
    fulfillment.UpdatePolicy(approvedPolicy with
    {
        WriteSequence = [FulfillmentWriteSteps.WmsFulfillmentSubmission, FulfillmentWriteSteps.ErpOrderRegistration]
    });
    var wmsFirst = await fulfillment.ReceiveAsync(wmsFirstOrder);
    await fulfillment.AdvanceAsync(wmsFirst.Id);
    await fulfillment.AdvanceAsync(wmsFirst.Id);
    var wmsFirstAccepted = fulfillment.Get(wmsFirst.Id);
    Check(wmsFirstAccepted.State == "wms-accepted" &&
        wmsFirstAccepted.History.All(entry => entry.State != "erp-registered"),
        "The alternate approved WMS-before-ERP sequence is honored.");
    var erpSecond = await fulfillment.AdvanceAsync(wmsFirst.Id);
    Check(erpSecond.State == "erp-registered" &&
        erpSecond.History.FindIndex(entry => entry.State == "wms-accepted") <
        erpSecond.History.FindIndex(entry => entry.State == "erp-registered"),
        "ERP registration waits for WMS acceptance when policy orders WMS first.");
    var insufficient = await fulfillment.ReceiveAsync(fulfillmentOrder with
    {
        ExternalOrderId = "package-fulfillment-1003",
        IdempotencyKey = "request-fulfillment-1003",
        Lines = [fulfillmentOrder.Lines[0] with { Quantity = 100 }]
    });
    Check(insufficient.State == "blocked-stock" && insufficient.Warehouses.Length == 0,
        "Insufficient stock is blocked before any external write.");
    var repeatedSku = await fulfillment.ReceiveAsync(fulfillmentOrder with
    {
        ExternalOrderId = "same-sku-multiple-lines",
        IdempotencyKey = "same-sku-multiple-lines",
        Lines = [new("first", "TSH-EKR-S", 25, "EA", 449, "TRY"), new("second", "TSH-EKR-S", 25, "EA", 449, "TRY")]
    });
    Check(repeatedSku.State == "blocked-stock" && repeatedSku.Warehouses.Length == 0,
        "Two lines of the same SKU cannot allocate the same physical stock twice.");

    var partialRoot = Path.Combine(testFolder, "partial");
    Directory.CreateDirectory(partialRoot);
    var partialEnvironment = new WmsTestEnvironment(partialRoot);
    var partialWms = new MockWmsProvider(writableProfile, new WmsMockBehavior(PackageCount: 2, ShipmentQuantityLimit: 1));
    var partialInventory = new WmsInventoryService(partialWms, fulfillmentCanonical, fulfillmentRegistry, partialEnvironment);
    await partialInventory.UpdatePolicyAsync(new(new(), new(), new()));
    var partialWorkflow = new FulfillmentOrchestrationService(partialWms, partialInventory, fulfillmentErp,
        fulfillmentCanonical, statusPublisher, partialEnvironment);
    partialWorkflow.UpdatePolicy(approvedPolicy with { WriteSequence = [FulfillmentWriteSteps.WmsFulfillmentSubmission], AllowPartialShipments = true });
    var partial = await partialWorkflow.ReceiveAsync(wmsFirstOrder with { ExternalOrderId = "partial-shipment", IdempotencyKey = "partial-shipment" });
    for (var step = 0; step < 7; step++) partial = await partialWorkflow.AdvanceAsync(partial.Id);
    Check(partial.State == "partially-shipped" && partial.Packages.Sum(package => package.Lines.Sum(line => line.Quantity)) == 1,
        "Partial shipment retains unshipped quantity and never marks the complete order processed.");
    var replayedPartial = await partialWorkflow.AdvanceAsync(partial.Id);
    Check(replayedPartial.Packages.Count == partial.Packages.Count && replayedPartial.Publications.Count == partial.Publications.Count,
        "Repeated partial shipment polling creates neither duplicate packages nor publications.");

    var mismatchRoot = Path.Combine(testFolder, "wrong-reservation");
    Directory.CreateDirectory(mismatchRoot);
    var mismatchEnvironment = new WmsTestEnvironment(mismatchRoot);
    var mismatchedProvider = new MismatchedReservationProvider(new MockWmsProvider(writableProfile));
    var mismatchInventory = new WmsInventoryService(mismatchedProvider, fulfillmentCanonical, fulfillmentRegistry, mismatchEnvironment);
    await mismatchInventory.UpdatePolicyAsync(new(new(), new(), new()));
    var mismatchWorkflow = new FulfillmentOrchestrationService(mismatchedProvider, mismatchInventory, fulfillmentErp,
        fulfillmentCanonical, statusPublisher, mismatchEnvironment);
    mismatchWorkflow.UpdatePolicy(approvedPolicy with { WriteSequence = [FulfillmentWriteSteps.WmsFulfillmentSubmission] });
    var mismatch = await mismatchWorkflow.ReceiveAsync(wmsFirstOrder with { ExternalOrderId = "wrong-reservation", IdempotencyKey = "wrong-reservation" });
    for (var step = 0; step < 3; step++) mismatch = await mismatchWorkflow.AdvanceAsync(mismatch.Id);
    Check(mismatch.State == "blocked-reservation" && mismatch.Warehouses.All(task => task.PickingState == "pending"),
        "A successful reservation response from the wrong warehouse blocks picking.");

    var rejectionRoot = Path.Combine(testFolder, "rejection");
    Directory.CreateDirectory(rejectionRoot);
    var rejectionEnvironment = new WmsTestEnvironment(rejectionRoot);
    var rejectionDb = new DemoStore(rejectionEnvironment);
    var rejectionCanonical = new CanonicalStore(rejectionDb, fulfillmentConfiguration);
    var rejectionErp = new MockNebimProvider(rejectionDb,
        new ErpConnectionProfile("mock-nebim", "default", AllowMockWrites: true));
    await new NebimCanonicalSync(rejectionErp, rejectionCanonical, rejectionDb).Run(CancellationToken.None);
    var rejectingWms = new MockWmsProvider(writableProfile,
        new WmsMockBehavior(RejectWarehouseId: "mock-outlet"));
    var rejectingInventory = new WmsInventoryService(rejectingWms, rejectionCanonical,
        new IntegrationRegistry(rejectionEnvironment), rejectionEnvironment);
    await rejectingInventory.UpdatePolicyAsync(new(new(), new(), new()));
    var rejectingWorkflow = new FulfillmentOrchestrationService(rejectingWms, rejectingInventory, rejectionErp,
        rejectionCanonical, new MockFulfillmentStatusPublisher(writableProfile, new()), rejectionEnvironment);
    rejectingWorkflow.UpdatePolicy(approvedPolicy);
    var rejected = await rejectingWorkflow.ReceiveAsync(fulfillmentOrder with
    {
        ExternalOrderId = "package-rejected-1004",
        IdempotencyKey = "request-rejected-1004"
    });
    await rejectingWorkflow.AdvanceAsync(rejected.Id);
    await rejectingWorkflow.AdvanceAsync(rejected.Id);
    await rejectingWorkflow.AdvanceAsync(rejected.Id);
    await rejectingWorkflow.AdvanceAsync(rejected.Id);
    var warehouseRejected = await rejectingWorkflow.AdvanceAsync(rejected.Id);
    Check(warehouseRejected.State == "wms-rejected" &&
        warehouseRejected.History.Any(entry => entry.Message.Contains("reddetti", StringComparison.Ordinal)),
        "Warehouse rejection is visible and stops later fulfillment stages.");

    var ambiguousRoot = Path.Combine(testFolder, "ambiguous");
    Directory.CreateDirectory(ambiguousRoot);
    var ambiguousEnvironment = new WmsTestEnvironment(ambiguousRoot);
    var ambiguousDb = new DemoStore(ambiguousEnvironment);
    var ambiguousCanonical = new CanonicalStore(ambiguousDb, fulfillmentConfiguration);
    var ambiguousErp = new MockNebimProvider(ambiguousDb,
        new ErpConnectionProfile("mock-nebim", "default", AllowMockWrites: true));
    await new NebimCanonicalSync(ambiguousErp, ambiguousCanonical, ambiguousDb).Run(CancellationToken.None);
    var ambiguousWms = new MockWmsProvider(writableProfile, new WmsMockBehavior(AmbiguousSubmission: true));
    var ambiguousInventory = new WmsInventoryService(ambiguousWms, ambiguousCanonical,
        new IntegrationRegistry(ambiguousEnvironment), ambiguousEnvironment);
    await ambiguousInventory.UpdatePolicyAsync(new(new(), new(), new()));
    var ambiguousWorkflow = new FulfillmentOrchestrationService(ambiguousWms, ambiguousInventory, ambiguousErp,
        ambiguousCanonical, new MockFulfillmentStatusPublisher(writableProfile, new()), ambiguousEnvironment);
    ambiguousWorkflow.UpdatePolicy(approvedPolicy with { WriteSequence = [FulfillmentWriteSteps.WmsFulfillmentSubmission] });
    var uncertain = await ambiguousWorkflow.ReceiveAsync(wmsFirstOrder with
    {
        ExternalOrderId = "package-unknown-1005",
        IdempotencyKey = "request-unknown-1005"
    });
    var outcomeUnknown = await ambiguousWorkflow.AdvanceAsync(uncertain.Id);
    Check(outcomeUnknown.State == "wms-outcome-unknown",
        "A timeout after the mock WMS accepted a request is retained as an unknown outcome.");
    var reconciled = await ambiguousWorkflow.AdvanceAsync(uncertain.Id);
    Check(reconciled.State == "wms-accepted" &&
        reconciled.History.Any(entry => entry.State == "wms-outcome-unknown"),
        "Unknown WMS outcomes are reconciled by external request ID, not resubmitted.");
}
finally
{
    var absolute = Path.GetFullPath(testFolder);
    if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(absolute).StartsWith("marketpilot-wms-stock-", StringComparison.Ordinal))
        throw new Exception("Unsafe WMS test cleanup.");
    Directory.Delete(absolute, true);
}

Console.WriteLine("PASS WMS contracts, multi-warehouse inventory and fulfillment orchestration: approved configurable ERP/WMS sequence, deterministic external IDs/idempotency, duplicate delivery, ERP registration, accepted-vs-processed, warehouse allocation, multiple packages, shortage/rejection, timeout reconciliation, shipment tracking, status publications, history and restart persistence. Mock-only; no real ERP/WMS/marketplace writes.");

sealed class WmsTestEnvironment(string contentRoot) : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "Production";
    public string ApplicationName { get; set; } = "WmsChecks";
    public string WebRootPath { get; set; } = "";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
