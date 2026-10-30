namespace MarketPilot.Web.Wms;

public sealed record WmsMockProviderState(Dictionary<string,WmsFulfillmentAcceptance> Fulfillments,
    Dictionary<string,string> Fingerprints,Dictionary<string,WmsFulfillmentOrder> Orders,HashSet<string> AmbiguousSubmissions);

public sealed class MockWmsProvider : IWmsProvider
{
    private static readonly DateTimeOffset FixtureTime = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
    private readonly WmsMockBehavior behavior;
    private readonly object gate = new();
    private readonly Dictionary<string, WmsFulfillmentAcceptance> fulfillments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> fulfillmentFingerprints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WmsFulfillmentOrder> orders = new(StringComparer.Ordinal);
    private readonly HashSet<string> ambiguousSubmissions = new(StringComparer.Ordinal);
    private readonly MarketPilot.Web.Enterprise.EnterpriseSync? persistence;

    private static readonly IReadOnlyList<WmsWarehouse> Warehouses =
    [
        new("mock-main", "MAIN", "Mock Ana Depo", true),
        new("mock-outlet", "OUTLET", "Mock Outlet Depo", true)
    ];

    private static readonly IReadOnlyList<WmsStockSnapshot> Stock =
    [
        new("mock-main", "TSH-EKR-S", 36, 4, "EA", FixtureTime, 2, 1, 0, 0, 1, 0),
        new("mock-main", "TSH-LAC-M", 12, 0, "EA", FixtureTime, 2, 0, 0, 0, 0, 0),
        new("mock-outlet", "TSH-EKR-S", 8, 1, "EA", FixtureTime, 2, 0, 0, 0, 0, 0),
        new("mock-main", "FBA-TSH-EKR-S", 14, 0, "EA", FixtureTime, 2, 0, 0, 0, 0, 0, true, "Amazon FBA"),
        new("mock-main", "N11-TSH-LAC-M", 6, 0, "EA", FixtureTime, 2, 0, 0, 0, 0, 0, true, "n11depom")
    ];

    private static readonly IReadOnlyList<WmsStockMovement> Movements =
    [
        new("mock-movement-001", "mock-main", "TSH-EKR-S", 40, "EA", "opening-balance", FixtureTime.AddDays(-1)),
        new("mock-movement-002", "mock-main", "TSH-EKR-S", -4, "EA", "order-reservation", FixtureTime.AddHours(-2)),
        new("mock-movement-003", "mock-outlet", "TSH-EKR-S", 9, "EA", "opening-balance", FixtureTime.AddDays(-1)),
        new("mock-movement-004", "mock-outlet", "TSH-EKR-S", -1, "EA", "order-reservation", FixtureTime.AddHours(-1))
    ];

    public MockWmsProvider(
        WmsConnectionProfile profile,
        WmsMockBehavior? behavior = null,
        WmsCapabilities? capabilities = null,
        MarketPilot.Web.Enterprise.EnterpriseSync? persistence = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Profile = profile;
        this.persistence=persistence;
        var saved=persistence?.ReadMockWmsState(profile.TenantId,profile.Id);
        if(saved!=null)Restore(saved);
        this.behavior = behavior ?? new WmsMockBehavior();

        if (this.behavior.DelayMilliseconds is < 0 or > 30000)
            throw new ArgumentOutOfRangeException(nameof(behavior), "Mock gecikmesi 0-30000 ms aralığında olmalıdır.");
        if (this.behavior.FailureOperation is { } operation &&
            operation != "*" &&
            !AllOperations.Contains(operation, StringComparer.Ordinal))
            throw new ArgumentException("Mock hata senaryosu tanınmayan bir WMS işlemi seçiyor.", nameof(behavior));
        if (this.behavior.PackageCount is < 1 or > 20 ||
            this.behavior.ShipmentQuantityLimit is <= 0 or > 1000000)
            throw new ArgumentOutOfRangeException(nameof(behavior), "Mock paket sayısı veya sevk miktarı sınırı geçersiz.");

        var supported = profile.Mode == "mock" && profile.Enabled
            ? WmsCapability.All
            : WmsCapability.None;
        if (!profile.AllowMockWrites)
            supported &= ~WmsCapability.FulfillmentSubmission;

        Capabilities = capabilities ?? new WmsCapabilities(supported, profile.Mode == "mock");
    }

    public WmsConnectionProfile Profile { get; }
    public WmsCapabilities Capabilities { get; }

    public async Task<WmsResult<IReadOnlyList<WmsWarehouse>>> ReadWarehousesAsync(CancellationToken ct = default)
    {
        await BeforeAsync(WmsMockOperations.Warehouses, WmsCapability.Warehouses, ct);
        return Result(WmsMockOperations.Warehouses, Warehouses);
    }

    public async Task<WmsResult<IReadOnlyList<WmsStockSnapshot>>> ReadStockAsync(string warehouseId, CancellationToken ct = default)
    {
        await BeforeAsync(WmsMockOperations.Stock, WmsCapability.Stock, ct);
        EnsureWarehouse(warehouseId);
        return Result<IReadOnlyList<WmsStockSnapshot>>(
            WmsMockOperations.Stock, Stock.Where(row => row.WarehouseId == warehouseId).ToArray());
    }

    public async Task<WmsResult<IReadOnlyList<WmsStockMovement>>> ReadStockMovementsAsync(
        string warehouseId,
        DateTimeOffset? since = null,
        CancellationToken ct = default)
    {
        await BeforeAsync(WmsMockOperations.StockMovements, WmsCapability.StockMovements, ct);
        EnsureWarehouse(warehouseId);
        return Result<IReadOnlyList<WmsStockMovement>>(WmsMockOperations.StockMovements,
            Movements.Where(row => row.WarehouseId == warehouseId && (since is null || row.OccurredAt >= since)).ToArray());
    }

    public async Task<WmsResult<WmsFulfillmentAcceptance>> SubmitFulfillmentOrderAsync(
        WmsFulfillmentOrder order,
        CancellationToken ct = default)
    {
        await BeforeAsync(WmsMockOperations.FulfillmentSubmission, WmsCapability.FulfillmentSubmission, ct);
        ValidateOrder(order);
        var fingerprint = System.Text.Json.JsonSerializer.Serialize(new
        {
            order.ExternalOrderId,
            order.WarehouseId,
            order.Lines,
            IdempotencyKey = order.IdempotencyKey ?? order.Id
        });
        lock (gate)
        {
            if (fulfillmentFingerprints.TryGetValue(order.Id, out var existingFingerprint) &&
                existingFingerprint != fingerprint)
                throw Error("IdempotencyConflict", "Fulfillment kimliği farklı içerikle tekrar kullanıldı.");

            var before=Snapshot();
            if (!fulfillments.TryGetValue(order.Id, out var acceptance))
            {
                var rejected = string.Equals(behavior.RejectWarehouseId, order.WarehouseId, StringComparison.Ordinal);
                acceptance = new WmsFulfillmentAcceptance(order.Id, rejected ? "rejected" : "accepted",
                    rejected ? "Mock WMS bu depoyu reddetti." : "Mock WMS talebi kabul edildi.", FixtureTime);
                fulfillmentFingerprints.Add(order.Id, fingerprint);
                fulfillments.Add(order.Id, acceptance);
                orders.Add(order.Id, order);
            }
            var ambiguous=behavior.AmbiguousSubmission && ambiguousSubmissions.Add(order.Id);
            try{persistence?.SaveMockWmsState(Profile.TenantId,Profile.Id,Snapshot());}
            catch{Restore(before);throw;}
            if (ambiguous)
                throw new WmsProviderException(new("OutcomeUnknown", "Mock WMS isteği aldı ancak yanıt zaman aşımına uğradı; kabul durumu sorgulanmalı.", true));
            return Result(WmsMockOperations.FulfillmentSubmission, acceptance);
        }
    }

    private WmsMockProviderState Snapshot()=>new(new(fulfillments),new(fulfillmentFingerprints),new(orders),new(ambiguousSubmissions));
    private void Restore(WmsMockProviderState state)
    {
        fulfillments.Clear();foreach(var pair in state.Fulfillments)fulfillments.Add(pair.Key,pair.Value);
        fulfillmentFingerprints.Clear();foreach(var pair in state.Fingerprints)fulfillmentFingerprints.Add(pair.Key,pair.Value);
        orders.Clear();foreach(var pair in state.Orders)orders.Add(pair.Key,pair.Value);
        ambiguousSubmissions.Clear();ambiguousSubmissions.UnionWith(state.AmbiguousSubmissions);
    }

    public async Task<WmsResult<WmsFulfillmentAcceptance>> GetAcceptanceStatusAsync(
        string fulfillmentOrderId,
        CancellationToken ct = default)
    {
        await BeforeAsync(WmsMockOperations.AcceptanceStatus, WmsCapability.AcceptanceStatus, ct);
        lock (gate)
            return Result(WmsMockOperations.AcceptanceStatus, GetFulfillment(fulfillmentOrderId));
    }

    public async Task<WmsResult<WmsReservation>> GetReservationStatusAsync(
        string fulfillmentOrderId,
        CancellationToken ct = default)
    {
        await BeforeAsync(WmsMockOperations.Reservations, WmsCapability.Reservations, ct);
        WmsFulfillmentAcceptance acceptance;
        lock (gate)
            acceptance = GetFulfillment(fulfillmentOrderId);

        var order = FindOrder(fulfillmentOrderId);
        return Result(WmsMockOperations.Reservations, new WmsReservation(
            "mock-reservation-" + fulfillmentOrderId,
            fulfillmentOrderId,
            order.WarehouseId,
            "reserved",
            order.Lines.Select(line => new WmsReservationLine(line.Sku, line.Quantity, line.Unit)).ToArray(),
            acceptance.UpdatedAt));
    }

    public async Task<WmsResult<WmsFulfillmentProgress>> GetPickingStatusAsync(
        string fulfillmentOrderId,
        CancellationToken ct = default)
    {
        await BeforeAsync(WmsMockOperations.PickingStatus, WmsCapability.PickingStatus, ct);
        var acceptance = GetKnownFulfillment(fulfillmentOrderId);
        return Result(WmsMockOperations.PickingStatus,
            new WmsFulfillmentProgress(acceptance.FulfillmentOrderId, "picked", acceptance.UpdatedAt));
    }

    public async Task<WmsResult<WmsFulfillmentProgress>> GetPackingStatusAsync(
        string fulfillmentOrderId,
        CancellationToken ct = default)
    {
        await BeforeAsync(WmsMockOperations.PackingStatus, WmsCapability.PackingStatus, ct);
        var acceptance = GetKnownFulfillment(fulfillmentOrderId);
        return Result(WmsMockOperations.PackingStatus,
            new WmsFulfillmentProgress(acceptance.FulfillmentOrderId, "packed", acceptance.UpdatedAt));
    }

    public async Task<WmsResult<WmsShipmentStatus>> GetShipmentStatusAsync(
        string fulfillmentOrderId,
        CancellationToken ct = default)
    {
        await BeforeAsync(WmsMockOperations.ShipmentStatus, WmsCapability.ShipmentStatus, ct);
        var acceptance = GetKnownFulfillment(fulfillmentOrderId);
        var order = FindOrder(fulfillmentOrderId);
        var packages = CreatePackages(order);
        var shipped = packages.SelectMany(package => package.Lines)
            .GroupBy(line => line.ExternalLineId ?? line.Sku)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity), StringComparer.Ordinal);
        var fullyShipped = order.Lines.All(line => shipped.GetValueOrDefault(line.ExternalLineId ?? line.Sku) >= line.Quantity);
        return Result(WmsMockOperations.ShipmentStatus, new WmsShipmentStatus(
            fulfillmentOrderId,
            fullyShipped ? "shipped" : "partially-shipped",
            "Mock Kargo",
            "MOCK-" + fulfillmentOrderId,
            null,
            acceptance.UpdatedAt,
            packages));
    }

    public async Task<WmsResult<WmsRequestEligibility>> CheckCancellationOrReturnEligibilityAsync(
        string fulfillmentOrderId,
        WmsRequestType requestType,
        CancellationToken ct = default)
    {
        var (operation, capability) = requestType switch
        {
            WmsRequestType.Cancellation => (WmsMockOperations.CancellationEligibility, WmsCapability.CancellationEligibility),
            WmsRequestType.Return => (WmsMockOperations.ReturnEligibility, WmsCapability.ReturnEligibility),
            _ => throw Error("InvalidRequest", "İptal veya iade talebi türü geçersiz.")
        };
        await BeforeAsync(operation, capability, ct);
        var acceptance = GetKnownFulfillment(fulfillmentOrderId);
        var eligible = requestType == WmsRequestType.Cancellation;
        return Result(operation, new WmsRequestEligibility(
            fulfillmentOrderId,
            requestType,
            eligible,
            eligible ? null : "Mock fulfillment için sevkiyat sonrası iade uygun değil.",
            acceptance.UpdatedAt));
    }

    private static readonly string[] AllOperations =
    [
        WmsMockOperations.Warehouses,
        WmsMockOperations.Stock,
        WmsMockOperations.StockMovements,
        WmsMockOperations.FulfillmentSubmission,
        WmsMockOperations.AcceptanceStatus,
        WmsMockOperations.Reservations,
        WmsMockOperations.PickingStatus,
        WmsMockOperations.PackingStatus,
        WmsMockOperations.ShipmentStatus,
        WmsMockOperations.CancellationEligibility,
        WmsMockOperations.ReturnEligibility,
        WmsMockOperations.MarketplaceUpdate,
        WmsMockOperations.ErpUpdate
    ];

    private IReadOnlyList<WmsShipmentPackage> CreatePackages(WmsFulfillmentOrder order)
    {
        var packages = Enumerable.Range(0, behavior.PackageCount)
            .Select(index => new List<WmsFulfillmentLine>()).ToArray();
        foreach (var line in order.Lines)
        {
            var quantity = Math.Min(line.Quantity, behavior.ShipmentQuantityLimit ?? line.Quantity);
            var packageCount = Math.Min(behavior.PackageCount, (int)decimal.Ceiling(quantity));
            for (var index = 0; index < packageCount; index++)
            {
                var remainingPackages = packageCount - index;
                var part = decimal.Ceiling(quantity / remainingPackages);
                packages[index].Add(line with { Quantity = part });
                quantity -= part;
            }
        }

        return packages.Select((lines, index) => new WmsShipmentPackage(
                $"{order.Id}-package-{index + 1}",
                lines,
                "Mock Kargo",
                $"MOCK-{order.Id}-{index + 1}",
                null,
                FixtureTime))
            .Where(package => package.Lines.Count > 0)
            .ToArray();
    }

    private async Task BeforeAsync(string operation, WmsCapability capability, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Profile.Enabled)
            throw Error("Disabled", "WMS bağlantı profili kapalı.");
        if (Profile.Mode != "mock" || !Capabilities.IsMock)
            throw Error("NotSupported", "Gerçek WMS adaptörü sağlayıcı sözleşmesi doğrulanana kadar etkin değil.");
        if (string.IsNullOrWhiteSpace(Profile.Id) || string.IsNullOrWhiteSpace(Profile.TenantId))
            throw Error("InvalidProfile", "WMS profil ve işletme kimliği gereklidir.");
        if (!Capabilities.Supports(capability))
            throw Error("NotSupported", "İstenen işlem sağlayıcının capabilities kapsamına dahil değil.");
        if (operation == WmsMockOperations.FulfillmentSubmission && !Profile.AllowMockWrites)
            throw Error("WriteDisabled", "Mock WMS yazma izni kapalı.");

        if (behavior.DelayMilliseconds > 0)
            await Task.Delay(behavior.DelayMilliseconds, ct);
        if (behavior.FailureOperation is "*" || behavior.FailureOperation == operation)
            throw new WmsProviderException(new("SimulatedFailure", $"Mock WMS '{operation}' hatası simüle edildi.", true));
    }

    private static void EnsureWarehouse(string warehouseId)
    {
        if (Warehouses.All(warehouse => warehouse.Id != warehouseId))
            throw Error("NotFound", "Mock WMS deposu bulunamadı.");
    }

    private static void ValidateOrder(WmsFulfillmentOrder order)
    {
        if (order is null || string.IsNullOrWhiteSpace(order.Id) || order.Id.Length > 120 ||
            string.IsNullOrWhiteSpace(order.ExternalOrderId) || order.ExternalOrderId.Length > 120 ||
            order.IdempotencyKey is { Length: > 160 } ||
            order.RequestedAt == default || order.Lines is null || order.Lines.Count is < 1 or > 100 ||
            order.Lines.Any(line => line is null || string.IsNullOrWhiteSpace(line.Sku) ||
                line.Quantity <= 0 || line.Quantity > 1000000 || decimal.Truncate(line.Quantity) != line.Quantity ||
                line.Unit != "EA"))
            throw Error("InvalidRequest", "Mock fulfillment kimliği, zamanı veya satırları geçersiz.");
        EnsureWarehouse(order.WarehouseId);
        if (order.Lines.Any(line => Stock.All(row => row.Sku != line.Sku)))
            throw Error("InvalidRequest", "Mock fulfillment satırında desteklenmeyen SKU var.");
    }

    private WmsFulfillmentAcceptance GetKnownFulfillment(string fulfillmentOrderId)
    {
        lock (gate)
            return GetFulfillment(fulfillmentOrderId);
    }

    private WmsFulfillmentAcceptance GetFulfillment(string fulfillmentOrderId) =>
        fulfillments.GetValueOrDefault(fulfillmentOrderId) ?? throw Error("NotFound", "Mock fulfillment talebi bulunamadı.");

    private WmsFulfillmentOrder FindOrder(string fulfillmentOrderId)
    {
        lock (gate)
        {
            return orders.GetValueOrDefault(fulfillmentOrderId) ?? throw Error("NotFound", "Mock fulfillment talebi bulunamadı.");
        }
    }

    private static WmsResult<T> Result<T>(string operation, T data) =>
        new(data, $"mock-{operation}-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);

    private static WmsProviderException Error(string code, string message) => new(new(code, message));
}
