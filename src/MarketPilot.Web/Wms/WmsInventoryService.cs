using MarketPilot.Web.Contracts;
using MarketPilot.Web.Integrations;
using System.Text.Json;

namespace MarketPilot.Web.Wms;

public sealed class WmsInventoryState
{
    public WmsInventoryPolicy Policy { get; set; } = new();
    public Dictionary<string, WmsStockSnapshot> Snapshots { get; set; } = new(StringComparer.Ordinal);
    public List<WmsSnapshotHistoryEntry> History { get; set; } = [];
    public List<WmsStockMovement> Movements { get; set; } = [];
    public List<string> MovementIds { get; set; } = [];
}

public sealed class WmsInventoryPolicy
{
    public string ApprovedSellableRule { get; set; } = "strict-deduction";
    public DateTimeOffset? RuleApprovedAt { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, decimal> SafetyStockByWarehouse { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string[]> WarehouseAssignmentsByAccount { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, decimal> ChannelStockLimits { get; set; } = new(StringComparer.Ordinal);
}

public sealed record WmsInventoryPolicyUpdate(
    Dictionary<string, decimal> SafetyStockByWarehouse,
    Dictionary<string, string[]> WarehouseAssignmentsByAccount,
    Dictionary<string, decimal> ChannelStockLimits);

public sealed record WmsSnapshotHistoryEntry(
    string Id,
    string WarehouseId,
    string Sku,
    long? SourceVersion,
    DateTimeOffset SourceTime,
    DateTimeOffset ReceivedAt,
    decimal? PreviousOnHand,
    decimal? IncomingOnHand,
    string Disposition,
    string? Reason);

public sealed record WmsInventoryRow(
    WmsWarehouse Warehouse,
    WmsStockSnapshot Snapshot,
    decimal? SafetyStock,
    decimal? SellableQuantity,
    string SellableState,
    bool CanAllocateToChannels);

public sealed record WmsChannelStock(
    string AccountId,
    string Marketplace,
    string AccountName,
    string[] WarehouseIds,
    decimal? ChannelStockLimit,
    string Sku,
    decimal? SellableQuantity);

public sealed record WmsStockDiscrepancy(
    string WarehouseCode,
    string Sku,
    decimal? WmsQuantity,
    decimal? ErpQuantity,
    string WmsUnit,
    string ErpUnit,
    string State,
    DateTimeOffset WmsSourceTime);

public sealed record WmsInventoryOverview(
    WmsConnectionProfile Profile,
    WmsCapabilities Capabilities,
    WmsInventoryPolicy Policy,
    string ApprovedSellableRule,
    DateTimeOffset? RuleApprovedAt,
    IReadOnlyList<WmsWarehouse> Warehouses,
    IReadOnlyList<WmsInventoryRow> Stock,
    IReadOnlyList<WmsStockMovement> Movements,
    IReadOnlyList<WmsSnapshotHistoryEntry> History,
    IReadOnlyList<WmsStockDiscrepancy> Discrepancies,
    IReadOnlyList<WmsChannelStock> ChannelStock,
    IReadOnlyList<WmsMarketplaceAccount> MarketplaceAccounts,
    DateTimeOffset ReadAt);

public sealed record WmsMarketplaceAccount(string Id, string Marketplace, string Name);

public sealed class WmsInventoryService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();
    private readonly IWmsProvider provider;
    private readonly CanonicalStore canonicalStore;
    private readonly IntegrationRegistry registry;
    private readonly string stateFile;
    private WmsInventoryState state;

    public WmsInventoryService(
        IWmsProvider provider,
        CanonicalStore canonicalStore,
        IntegrationRegistry registry,
        IWebHostEnvironment environment)
    {
        this.provider = provider;
        this.canonicalStore = canonicalStore;
        this.registry = registry;
        stateFile = Path.Combine(environment.ContentRootPath, "Data", "wms-inventory.json");
        state = LoadState();
    }

    public async Task<WmsInventoryOverview> ReadAsync(CancellationToken ct = default)
    {
        var warehousesResult = await provider.ReadWarehousesAsync(ct);
        var warehouses = warehousesResult.Data;
        var snapshots = new List<WmsStockSnapshot>();
        var movements = new List<WmsStockMovement>();

        foreach (var warehouse in warehouses.Where(warehouse => warehouse.IsActive))
        {
            ct.ThrowIfCancellationRequested();
            var stock = await provider.ReadStockAsync(warehouse.Id, ct);
            snapshots.AddRange(stock.Data);
            if (provider.Capabilities.Supports(WmsCapability.StockMovements))
            {
                var history = await provider.ReadStockMovementsAsync(warehouse.Id, null, ct);
                movements.AddRange(history.Data);
            }
        }

        foreach (var snapshot in snapshots)
            ApplySnapshot(snapshot);

        Modify(saved =>
        {
            foreach (var movement in movements)
            {
                if (!saved.MovementIds.Contains(movement.Id))
                {
                    saved.MovementIds.Add(movement.Id);
                    saved.Movements.Add(movement);
                }
            }
            if (saved.Movements.Count > 2000)
                saved.Movements.RemoveRange(0, saved.Movements.Count - 2000);
            if (saved.MovementIds.Count > 4000)
                saved.MovementIds.RemoveRange(0, saved.MovementIds.Count - 4000);
            return true;
        });

        var savedState = Read(saved => new
        {
            Policy = CopyPolicy(saved.Policy),
            Snapshots = saved.Snapshots.Values.ToArray(),
            Movements = saved.Movements.ToArray(),
            History = saved.History.ToArray()
        });
        {
            var saved = savedState;
            var rows = saved.Snapshots
                .Where(snapshot => warehouses.Any(warehouse => warehouse.Id == snapshot.WarehouseId))
                .OrderBy(snapshot => snapshot.WarehouseId, StringComparer.Ordinal)
                .ThenBy(snapshot => snapshot.Sku, StringComparer.Ordinal)
                .Select(snapshot =>
                {
                    var warehouse = warehouses.Single(row => row.Id == snapshot.WarehouseId);
                    var safety = saved.Policy.SafetyStockByWarehouse.GetValueOrDefault(warehouse.Id);
                    var (quantity, status) = CalculateSellable(snapshot, safety);
                    return new WmsInventoryRow(warehouse, snapshot, safety, quantity, status, !snapshot.ProviderManaged);
                }).ToArray();

            var accounts = registry.Connections.Values
                .OrderBy(row => row.Settings.Marketplace, StringComparer.Ordinal)
                .ThenBy(row => row.Settings.Name, StringComparer.Ordinal)
                .Select(row => new WmsMarketplaceAccount(row.Id, row.Settings.Marketplace, row.Settings.Name))
                .ToArray();

            return new WmsInventoryOverview(
                provider.Profile,
                provider.Capabilities,
                saved.Policy,
                saved.Policy.ApprovedSellableRule,
                saved.Policy.RuleApprovedAt,
                warehouses,
                rows,
                saved.Movements.OrderByDescending(row => row.OccurredAt).Take(250).ToArray(),
                saved.History.OrderByDescending(row => row.ReceivedAt).Take(250).ToArray(),
                FindDiscrepancies(rows),
                CalculateChannelStock(rows, saved.Policy, accounts),
                accounts,
                DateTimeOffset.UtcNow);
        }
    }

    public async Task<WmsInventoryPolicy> UpdatePolicyAsync(WmsInventoryPolicyUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var warehouses = (await provider.ReadWarehousesAsync(ct)).Data;
        var warehouseIds = warehouses.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var accountIds = registry.Connections.Keys.ToHashSet(StringComparer.Ordinal);

        if (update.SafetyStockByWarehouse is null ||
            update.SafetyStockByWarehouse.Any(pair => !warehouseIds.Contains(pair.Key) || pair.Value is < 0 or > 1000000))
            throw new ArgumentException("Güvenlik stoğu yalnız bilinen depolar için 0-1.000.000 aralığında tanımlanabilir.");
        if (update.WarehouseAssignmentsByAccount is null ||
            update.WarehouseAssignmentsByAccount.Any(pair =>
                !accountIds.Contains(pair.Key) || pair.Value is null ||
                pair.Value.Any(warehouseId => !warehouseIds.Contains(warehouseId)) ||
                pair.Value.Distinct(StringComparer.Ordinal).Count() != pair.Value.Length))
            throw new ArgumentException("Kanal depo eşlemesi geçersiz; yalnız bilinen hesap ve depolar seçilebilir.");
        if (update.ChannelStockLimits is null ||
            update.ChannelStockLimits.Any(pair => !accountIds.Contains(pair.Key) || pair.Value is < 0 or > 1000000))
            throw new ArgumentException("Kanal stok limiti yalnız bilinen hesaplar için 0-1.000.000 aralığında tanımlanabilir.");

        return Modify(saved =>
        {
            saved.Policy.SafetyStockByWarehouse =
                new(update.SafetyStockByWarehouse, StringComparer.Ordinal);
            saved.Policy.WarehouseAssignmentsByAccount =
                update.WarehouseAssignmentsByAccount.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToArray(),
                    StringComparer.Ordinal);
            saved.Policy.ChannelStockLimits =
                new(update.ChannelStockLimits, StringComparer.Ordinal);
            saved.Policy.ApprovedSellableRule = "strict-deduction";
            saved.Policy.RuleApprovedAt ??= DateTimeOffset.UtcNow;
            return CopyPolicy(saved.Policy);
        });
    }

    public IReadOnlyList<WmsStockMovement> ReadMovements(string? warehouseId, string? sku, int size = 250)
    {
        if (size is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(size), "Stok hareket sayfa boyutu 1-1000 aralığında olmalıdır.");

        return Read(state => (IReadOnlyList<WmsStockMovement>)state.Movements
            .Where(movement => string.IsNullOrWhiteSpace(warehouseId) || movement.WarehouseId == warehouseId)
            .Where(movement => string.IsNullOrWhiteSpace(sku) || movement.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(movement => movement.OccurredAt)
            .Take(size)
            .ToArray());
    }

    public IReadOnlyList<WmsSnapshotHistoryEntry> ReadHistory(string? warehouseId, string? sku, int size = 250)
    {
        if (size is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(size), "Stok geçmişi sayfa boyutu 1-1000 aralığında olmalıdır.");

        return Read(state => (IReadOnlyList<WmsSnapshotHistoryEntry>)state.History
            .Where(entry => string.IsNullOrWhiteSpace(warehouseId) || entry.WarehouseId == warehouseId)
            .Where(entry => string.IsNullOrWhiteSpace(sku) || entry.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.ReceivedAt)
            .Take(size)
            .ToArray());
    }

    public string ApplySnapshot(WmsStockSnapshot incoming)
    {
        ValidateSnapshot(incoming);
        var key = SnapshotKey(incoming.WarehouseId, incoming.Sku);
        return Modify(state =>
        {
            var ledger = state;
            var current = ledger.Snapshots.GetValueOrDefault(key);
            var (disposition, reason) = Decide(current, incoming);
            if (disposition == "applied")
                ledger.Snapshots[key] = incoming;

            if (disposition != "duplicate")
                ledger.History.Add(new(
                    Guid.NewGuid().ToString("N"),
                    incoming.WarehouseId,
                    incoming.Sku,
                    incoming.SourceVersion,
                    incoming.CapturedAt,
                    DateTimeOffset.UtcNow,
                    current?.OnHandQuantity,
                    incoming.OnHandQuantity,
                    disposition,
                    reason));
            if (ledger.History.Count > 5000)
                ledger.History.RemoveRange(0, ledger.History.Count - 5000);
            return disposition;
        });
    }

    private WmsStockDiscrepancy[] FindDiscrepancies(IReadOnlyList<WmsInventoryRow> rows)
    {
        var variants = canonicalStore.Records<ProductVariant>()
            .GroupBy(variant => variant.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var warehouses = canonicalStore.Records<Warehouse>()
            .GroupBy(warehouse => warehouse.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var erpStock = canonicalStore.Records<StockPosition>()
            .Where(position => !position.Context.SourceSystem.Equals("wms", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var differences = new List<WmsStockDiscrepancy>();

        foreach (var row in rows.Where(row => !row.Snapshot.ProviderManaged))
        {
            var hasVariants = variants.TryGetValue(row.Snapshot.Sku, out var matchingVariants);
            var hasWarehouses = warehouses.TryGetValue(row.Warehouse.Code, out var matchingWarehouses);
            if (!hasVariants || matchingVariants!.Length != 1 ||
                !hasWarehouses || matchingWarehouses!.Length != 1)
            {
                var identityState = hasVariants && matchingVariants!.Length > 1 ||
                    hasWarehouses && matchingWarehouses!.Length > 1
                    ? "ambiguous-erp-identity"
                    : "missing-erp-identity";
                differences.Add(new(row.Warehouse.Code, row.Snapshot.Sku, row.Snapshot.OnHandQuantity,
                    null, row.Snapshot.Unit, "unknown", identityState, row.Snapshot.CapturedAt));
                continue;
            }

            var positions = erpStock.Where(position =>
                position.VariantId == matchingVariants[0].Id && position.WarehouseId == matchingWarehouses[0].Id).ToArray();
            if (positions.Length == 0)
                continue;
            if (positions.Length > 1)
            {
                differences.Add(new(row.Warehouse.Code, row.Snapshot.Sku, row.Snapshot.OnHandQuantity,
                    null, row.Snapshot.Unit, "unknown", "ambiguous-erp-source", row.Snapshot.CapturedAt));
                continue;
            }

            var erp = positions[0];
            var state = erp.UnitCode != row.Snapshot.Unit
                ? "unit-mismatch"
                : row.Snapshot.OnHandQuantity is null
                    ? "wms-unknown"
                    : erp.Quantity == row.Snapshot.OnHandQuantity
                        ? "matched"
                        : "conflict";
            if (state != "matched")
                differences.Add(new(row.Warehouse.Code, row.Snapshot.Sku, row.Snapshot.OnHandQuantity,
                    erp.Quantity, row.Snapshot.Unit, erp.UnitCode, state, row.Snapshot.CapturedAt));
        }
        return differences.ToArray();
    }

    private static WmsChannelStock[] CalculateChannelStock(
        IReadOnlyList<WmsInventoryRow> rows,
        WmsInventoryPolicy policy,
        IReadOnlyList<WmsMarketplaceAccount> accounts)
    {
        var output = new List<WmsChannelStock>();
        var skus = rows.Where(row => !row.Snapshot.ProviderManaged)
            .Select(row => row.Snapshot.Sku)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var account in accounts)
        {
            var assignedWarehouses = policy.WarehouseAssignmentsByAccount.GetValueOrDefault(account.Id, []);
            foreach (var sku in skus)
            {
                var matchingRows = rows.Where(row =>
                    row.Snapshot.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase) &&
                    assignedWarehouses.Contains(row.Warehouse.Id, StringComparer.Ordinal) &&
                    !row.Snapshot.ProviderManaged).ToArray();
                decimal? quantity = assignedWarehouses.Length == 0 ||
                    assignedWarehouses.Any(warehouseId => matchingRows.All(row => row.Warehouse.Id != warehouseId)) ||
                    matchingRows.Any(row => row.SellableQuantity is null)
                    ? null
                    : matchingRows.Sum(row => row.SellableQuantity!.Value);
                if (quantity is not null && policy.ChannelStockLimits.TryGetValue(account.Id, out var limit))
                    quantity = Math.Min(quantity.Value, limit);
                output.Add(new(account.Id, account.Marketplace, account.Name,
                    assignedWarehouses, policy.ChannelStockLimits.TryGetValue(account.Id, out var configuredLimit) ? configuredLimit : null,
                    sku, quantity));
            }
        }
        return output.ToArray();
    }

    private static (decimal? Quantity, string State) CalculateSellable(WmsStockSnapshot snapshot, decimal safetyStock)
    {
        if (snapshot.ProviderManaged)
            return (null, "provider-managed");
        var components = new[]
        {
            snapshot.OnHandQuantity,
            snapshot.ReservedQuantity,
            snapshot.DamagedQuantity,
            snapshot.BlockedQuantity,
            snapshot.ReturnInspectionQuantity,
            snapshot.AllocatedQuantity,
            snapshot.InTransferQuantity
        };
        if (components.Any(component => component is null))
            return (null, "unknown");
        if (components.Any(component => component < 0))
            return (0, "negative-stock-blocked");

        return (Math.Max(0, snapshot.OnHandQuantity!.Value - snapshot.ReservedQuantity!.Value -
            snapshot.DamagedQuantity!.Value - snapshot.BlockedQuantity!.Value -
            snapshot.ReturnInspectionQuantity!.Value - snapshot.AllocatedQuantity!.Value -
            snapshot.InTransferQuantity!.Value - safetyStock), "known");
    }

    private static (string Disposition, string? Reason) Decide(WmsStockSnapshot? current, WmsStockSnapshot incoming)
    {
        if (current is null)
            return ("applied", null);

        var sameContent = current with { CapturedAt = incoming.CapturedAt, SourceVersion = incoming.SourceVersion } == incoming;
        if (current.SourceVersion.HasValue && incoming.SourceVersion.HasValue)
        {
            if (incoming.SourceVersion < current.SourceVersion)
                return ("stale", "Kaynak sürümü mevcut WMS görüntüsünden eski.");
            if (incoming.SourceVersion == current.SourceVersion)
                return sameContent
                    ? ("duplicate", null)
                    : ("conflict", "Aynı kaynak sürümü farklı stok içeriği taşıyor.");
            return ("applied", null);
        }

        if (incoming.CapturedAt < current.CapturedAt)
            return ("stale", "Kaynak zamanı mevcut WMS görüntüsünden eski.");
        if (incoming.CapturedAt == current.CapturedAt)
            return sameContent
                ? ("duplicate", null)
                : ("conflict", "Sıralanabilir sürüm olmadan aynı zamanda farklı stok içeriği geldi.");
        return ("applied", null);
    }

    private static void ValidateSnapshot(WmsStockSnapshot snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.WarehouseId) ||
            string.IsNullOrWhiteSpace(snapshot.Sku) || snapshot.WarehouseId.Length > 120 ||
            snapshot.Sku.Length > 120 || string.IsNullOrWhiteSpace(snapshot.Unit) ||
            snapshot.Unit.Length > 30 || snapshot.CapturedAt == default ||
            snapshot.SourceVersion is < 0 ||
            new[]
            {
                snapshot.OnHandQuantity, snapshot.ReservedQuantity, snapshot.DamagedQuantity,
                snapshot.BlockedQuantity, snapshot.ReturnInspectionQuantity,
                snapshot.AllocatedQuantity, snapshot.InTransferQuantity
            }.Any(quantity => quantity is < -1000000000 or > 1000000000) ||
            snapshot.ProviderName?.Length > 120)
            throw new ArgumentException("WMS stok görüntüsü geçersiz.");
    }

    private static string SnapshotKey(string warehouseId, string sku) => warehouseId + "\0" + sku.ToUpperInvariant();

    private T Read<T>(Func<WmsInventoryState, T> action)
    {
        lock (gate)
            return action(state);
    }

    private T Modify<T>(Func<WmsInventoryState, T> action)
    {
        lock (gate)
        {
            var previous = JsonSerializer.Serialize(state, Json);
            var working = JsonSerializer.Deserialize<WmsInventoryState>(previous, Json) ??
                throw new InvalidOperationException("WMS stok durumu kopyalanamadı.");
            var result = action(working);
            var updated = JsonSerializer.Serialize(working, Json);
            if (updated == previous)
                return result;
            var temp = stateFile + ".tmp";
            File.WriteAllText(temp, updated);
            File.Move(temp, stateFile, true);
            state = working;
            return result;
        }
    }

    private WmsInventoryState LoadState()
    {
        var folder = Path.GetDirectoryName(stateFile)!;
        Directory.CreateDirectory(folder);
        if (!File.Exists(stateFile))
            return new WmsInventoryState();
        return JsonSerializer.Deserialize<WmsInventoryState>(File.ReadAllText(stateFile), Json) ??
            throw new InvalidOperationException("WMS stok geçmişi okunamadı.");
    }

    private static WmsInventoryPolicy CopyPolicy(WmsInventoryPolicy policy) => new()
    {
        ApprovedSellableRule = policy.ApprovedSellableRule,
        RuleApprovedAt = policy.RuleApprovedAt,
        SafetyStockByWarehouse = new(policy.SafetyStockByWarehouse, StringComparer.Ordinal),
        WarehouseAssignmentsByAccount = policy.WarehouseAssignmentsByAccount.ToDictionary(
            pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal),
        ChannelStockLimits = new(policy.ChannelStockLimits, StringComparer.Ordinal)
    };
}
