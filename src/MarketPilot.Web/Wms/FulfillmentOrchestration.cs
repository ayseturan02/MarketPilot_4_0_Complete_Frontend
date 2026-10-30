using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketPilot.Web.Contracts;
using MarketPilot.Web.Enterprise.Nebim;
using MarketPilot.Web.Integrations;
using ContractOrder = MarketPilot.Web.Contracts.MarketplaceOrder;

namespace MarketPilot.Web.Wms;

public static class FulfillmentWriteSteps
{
    public const string ErpOrderRegistration = "erp-order-registration";
    public const string WmsFulfillmentSubmission = "wms-fulfillment-submission";
}

public static class FulfillmentUpdateTargets
{
    public const string Marketplace = "marketplace";
    public const string Erp = "erp";
}

public sealed record FulfillmentWorkflowPolicy(
    bool Approved,
    string[] WriteSequence,
    string[] WarehousePriority,
    bool AllowPartialShipments,
    string[] UpdateTargets);

public sealed record FulfillmentWorkflowOrderLine(
    string ExternalLineId,
    string Sku,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    string Currency);

public sealed record FulfillmentWorkflowOrder(
    string Marketplace,
    string AccountId,
    string ExternalOrderId,
    string IdempotencyKey,
    string Status,
    DateTimeOffset OrderedAt,
    string Currency,
    IReadOnlyList<FulfillmentWorkflowOrderLine> Lines);

public sealed record FulfillmentWorkflowEvent(
    DateTimeOffset At,
    string State,
    string Message,
    string? ExternalRequestId = null,
    string? CorrelationId = null);

public sealed class FulfillmentWarehouseTask
{
    public string WarehouseId { get; set; } = "";
    public string ExternalRequestId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public WmsFulfillmentLine[] Lines { get; set; } = [];
    public string SubmissionState { get; set; } = "pending";
    public string AcceptanceState { get; set; } = "pending";
    public string ReservationState { get; set; } = "pending";
    public string PickingState { get; set; } = "pending";
    public string PackingState { get; set; } = "pending";
}

public sealed class FulfillmentWorkflowPublication
{
    public string Target { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string State { get; set; } = "pending";
    public string? CorrelationId { get; set; }
}

public sealed class FulfillmentWorkflowRecord
{
    public bool RequiresPreparedErpAcceptance { get; set; }
    public bool PreparedErpValidationComplete { get; set; }
    public string Id { get; set; } = "";
    public string ExternalRequestId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public FulfillmentWorkflowOrder Order { get; set; } = new("", "", "", "", "", default, "", []);
    public string State { get; set; } = "received";
    public FulfillmentWorkflowPolicy? PolicySnapshot { get; set; }
    public int NextWriteStep { get; set; }
    public string ErpRegistrationState { get; set; } = "not-required";
    public string? ErpOrderId { get; set; }
    public FulfillmentWarehouseTask[] Warehouses { get; set; } = [];
    public List<WmsShipmentPackage> Packages { get; set; } = [];
    public Dictionary<string, decimal> UnallocatedByLine { get; set; } = new(StringComparer.Ordinal);
    public List<FulfillmentWorkflowPublication> Publications { get; set; } = [];
    public List<FulfillmentWorkflowEvent> History { get; set; } = [];
    public string? BlockReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class FulfillmentWorkflowState
{
    public FulfillmentWorkflowPolicy Policy { get; set; } = new(false, [], [], false,
        [FulfillmentUpdateTargets.Marketplace, FulfillmentUpdateTargets.Erp]);
    public List<FulfillmentWorkflowRecord> Workflows { get; set; } = [];
}

public sealed record FulfillmentStatusUpdate(
    string WorkflowId,
    string Marketplace,
    string AccountId,
    string ExternalOrderId,
    string Status,
    WmsShipmentPackage Package);

public sealed record FulfillmentStatusReceipt(
    string Target,
    string IdempotencyKey,
    string State,
    string CorrelationId,
    DateTimeOffset UpdatedAt,
    bool IsMock);

public interface IFulfillmentStatusPublisher
{
    bool IsMock { get; }
    Task<FulfillmentStatusReceipt> PublishAsync(
        string target,
        FulfillmentStatusUpdate update,
        string idempotencyKey,
        CancellationToken ct = default);
}

public sealed class MockFulfillmentStatusPublisher(
    WmsConnectionProfile profile,
    WmsMockBehavior behavior) : IFulfillmentStatusPublisher
{
    private readonly object gate = new();
    private readonly Dictionary<string, (string Fingerprint, FulfillmentStatusReceipt Receipt)> receipts = new(StringComparer.Ordinal);
    public bool IsMock => profile.Mode == "mock";

    public async Task<FulfillmentStatusReceipt> PublishAsync(
        string target,
        FulfillmentStatusUpdate update,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsMock || !profile.Enabled || !profile.AllowMockWrites)
            throw new WmsProviderException(new("WriteDisabled", "Mock kanal/ERP durum güncellemesi etkin değil."));
        var operation = target switch
        {
            FulfillmentUpdateTargets.Marketplace => WmsMockOperations.MarketplaceUpdate,
            FulfillmentUpdateTargets.Erp => WmsMockOperations.ErpUpdate,
            _ => throw new WmsProviderException(new("InvalidRequest", "Bilinmeyen fulfillment güncelleme hedefi."))
        };
        if (behavior.DelayMilliseconds > 0)
            await Task.Delay(behavior.DelayMilliseconds, ct);
        if (behavior.FailureOperation is "*" || behavior.FailureOperation == operation)
            throw new WmsProviderException(new("SimulatedFailure", $"Mock '{target}' durum güncellemesi başarısız oldu.", true));

        var fingerprint = JsonSerializer.Serialize(update);
        lock (gate)
        {
            if (receipts.TryGetValue(idempotencyKey, out var saved))
            {
                if (saved.Fingerprint != fingerprint)
                    throw new WmsProviderException(new("IdempotencyConflict", "Dış durum güncelleme anahtarı farklı içerikle tekrar kullanıldı."));
                return saved.Receipt;
            }

            var receipt = new FulfillmentStatusReceipt(target, idempotencyKey, "acknowledged",
                "mock-update-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, true);
            receipts.Add(idempotencyKey, (fingerprint, receipt));
            return receipt;
        }
    }
}

public sealed class FulfillmentOrchestrationService
{
    private const int MaximumWorkflows = 10000;
    private const int MaximumHistory = 500;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly string stateFile;
    private readonly IWmsProvider wms;
    private readonly WmsInventoryService inventory;
    private readonly INebimErpProvider erp;
    private readonly CanonicalStore canonical;
    private readonly IFulfillmentStatusPublisher publisher;
    private FulfillmentWorkflowState state;

    public FulfillmentOrchestrationService(
        IWmsProvider wms,
        WmsInventoryService inventory,
        INebimErpProvider erp,
        CanonicalStore canonical,
        IFulfillmentStatusPublisher publisher,
        IWebHostEnvironment environment)
    {
        this.wms = wms;
        this.inventory = inventory;
        this.erp = erp;
        this.canonical = canonical;
        this.publisher = publisher;
        stateFile = Path.Combine(environment.ContentRootPath, "Data", "wms-fulfillment.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        state = File.Exists(stateFile)
            ? JsonSerializer.Deserialize<FulfillmentWorkflowState>(File.ReadAllText(stateFile), Json)
              ?? throw new InvalidOperationException("WMS fulfillment workflow state cannot be read.")
            : new();
        NormalizeState();
    }

    public FulfillmentWorkflowPolicy ReadPolicy()
    {
        operationGate.Wait();
        try
        {
            return CopyPolicy(state.Policy);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public FulfillmentWorkflowPolicy UpdatePolicy(FulfillmentWorkflowPolicy policy)
    {
        ValidatePolicy(policy);
        operationGate.Wait();
        try
        {
            var old = JsonSerializer.Serialize(state, Json);
            try
            {
                state.Policy = CopyPolicy(policy);
                Save();
                return CopyPolicy(state.Policy);
            }
            catch
            {
                Restore(old);
                throw;
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public FulfillmentWorkflowRecord[] List()
    {
        operationGate.Wait();
        try
        {
            return state.Workflows.OrderByDescending(row => row.UpdatedAt).Take(250).Select(Clone).ToArray();
        }
        finally
        {
            operationGate.Release();
        }
    }

    public FulfillmentWorkflowRecord Get(string id)
    {
        operationGate.Wait();
        try
        {
            return Clone(Find(id));
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<FulfillmentWorkflowRecord> ReceiveAsync(
        FulfillmentWorkflowOrder order,
        CancellationToken ct = default,
        bool requirePreparedErpAcceptance = false)
    {
        ValidateOrder(order);
        await operationGate.WaitAsync(ct);
        try
        {
            var fingerprint = Fingerprint(order);
            var scopeKey = OrderScope(order);
            {
                var existing = state.Workflows.FirstOrDefault(row =>
                    row.Order.Marketplace.Equals(order.Marketplace, StringComparison.OrdinalIgnoreCase) &&
                    row.Order.AccountId == order.AccountId &&
                    row.IdempotencyKey == order.IdempotencyKey ||
                    OrderScope(row.Order) == scopeKey);
                if (existing is not null)
                {
                    if (existing.Fingerprint != fingerprint)
                        throw Error("IdempotencyConflict", "Aynı sipariş/işlem anahtarı farklı içerikle tekrar gönderildi.");
                    if(requirePreparedErpAcceptance && !existing.RequiresPreparedErpAcceptance)
                    {existing.RequiresPreparedErpAcceptance=true;Save();}
                    return Clone(existing);
                }

                if (state.Workflows.Count >= MaximumWorkflows)
                    throw Error("CapacityExceeded", "Fulfillment iş akışı kapasitesi dolu.");
                var now = DateTimeOffset.UtcNow;
                var id = StableId("fulfillment", order.Marketplace, order.AccountId, order.ExternalOrderId);
                var row = new FulfillmentWorkflowRecord
                {
                    Id = id,
                    ExternalRequestId = StableId("request", order.Marketplace, order.AccountId, order.IdempotencyKey),
                    IdempotencyKey = order.IdempotencyKey,
                    Fingerprint = fingerprint,
                    Order = CloneOrder(order),
                    RequiresPreparedErpAcceptance = requirePreparedErpAcceptance,
                    State = "received",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                AddEvent(row, "received", "Pazaryeri siparişi alındı; doğrulama bekliyor.", row.ExternalRequestId);
                state.Workflows.Add(row);
                Save();
                if (!state.Policy.Approved)
                {
                    Block(row, "blocked-policy", "İşletme fulfillment sırasını henüz onaylamadı.");
                    return Clone(row);
                }

                await PrepareAsync(row, ct);
                return Clone(row);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    // Only the central orchestrator calls this after its ERP provider query confirmed acceptance.
    internal async Task MarkErpPreparationValidatedAsync(string id,CancellationToken ct)
    {
        await operationGate.WaitAsync(ct);
        try{Find(id).PreparedErpValidationComplete=true;Save();}
        finally{operationGate.Release();}
    }

    internal async Task ConfirmPreparedErpAcceptanceAsync(string id,string acceptanceReference,CancellationToken ct)
    {
        await operationGate.WaitAsync(ct);
        try
        {
            var row=Find(id);
            if(string.IsNullOrWhiteSpace(acceptanceReference))throw new ArgumentException("ERP kabul referansı gerekli.");
            if(row.ErpRegistrationState=="registered" && row.ErpOrderId!=acceptanceReference)
                throw new ArgumentException("Workflow farklı bir ERP kaydıyla eşleşmiş; inceleme gerekli.");
            row.ErpRegistrationState="registered";row.ErpOrderId=acceptanceReference;Save();
        }
        finally{operationGate.Release();}
    }

    public async Task<FulfillmentWorkflowRecord> AdvanceAsync(string id, CancellationToken ct = default)
    {
        await operationGate.WaitAsync(ct);
        try
        {
            var row = Find(id);
            if (row.State == "blocked-policy")
            {
                if (!state.Policy.Approved)
                    return Clone(row);
                await PrepareAsync(row, ct);
                return Clone(row);
            }
            if (row.State is "blocked" or "wms-rejected" or "processed")
                return Clone(row);
            if (row.PolicySnapshot is null)
                return BlockAndClone(row, "blocked-policy", "Onaylı fulfillment politikası bulunamadı.");
            if(row.RequiresPreparedErpAcceptance && !row.PreparedErpValidationComplete &&
                row.PolicySnapshot.WriteSequence.Contains(FulfillmentWriteSteps.ErpOrderRegistration))
                return BlockAndClone(row,"blocked-erp-preparation","Merkezi ERP sipariş hazırlık doğrulaması bekleniyor.");

            if (row.NextWriteStep < row.PolicySnapshot.WriteSequence.Length)
            {
                var step = row.PolicySnapshot.WriteSequence[row.NextWriteStep];
                if (step == FulfillmentWriteSteps.ErpOrderRegistration)
                    await AdvanceErpRegistrationAsync(row, ct);
                else
                    await AdvanceWmsSubmissionAsync(row, ct);
                return Clone(row);
            }

            if (row.Warehouses.Any(task => task.AcceptanceState != "accepted"))
                return await PollAcceptanceAsync(row, ct);
            if (row.Warehouses.Any(task => task.ReservationState != "reserved"))
                return await PollReservationsAsync(row, ct);
            if (row.Warehouses.Any(task => task.PickingState != "picked"))
                return await PollPickingAsync(row, ct);
            if (row.Warehouses.Any(task => task.PackingState != "packed"))
                return await PollPackingAsync(row, ct);

            await ReadShipmentsAndPublishAsync(row, ct);
            return Clone(row);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<FulfillmentWorkflowRecord> RetryValidationAsync(string id, CancellationToken ct = default)
    {
        await operationGate.WaitAsync(ct);
        try
        {
            var row = Find(id);
            if (row.Warehouses.Any(task => task.SubmissionState != "pending") ||
                row.ErpRegistrationState is "registered" or "unknown")
                throw Error("ReviewRequired", "Dış yazma başlamış olabilir; otomatik yeniden planlama güvenli değil.");
            if (!state.Policy.Approved)
                return BlockAndClone(row, "blocked-policy", "İşletme fulfillment sırasını henüz onaylamadı.");
            await PrepareAsync(row, ct);
            return Clone(row);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task PrepareAsync(FulfillmentWorkflowRecord row, CancellationToken ct)
    {
        var policy = state.Policy;
        if (!policy.Approved)
        {
            Block(row, "blocked-policy", "İşletme fulfillment sırasını henüz onaylamadı.");
            return;
        }
        if (!wms.Profile.Enabled || wms.Profile.Mode != "mock" || !wms.Capabilities.IsMock ||
            !wms.Capabilities.Supports(WmsCapability.FulfillmentSubmission | WmsCapability.AcceptanceStatus |
                WmsCapability.Reservations | WmsCapability.PickingStatus | WmsCapability.PackingStatus |
                WmsCapability.ShipmentStatus))
        {
            Block(row, "blocked-capability", "WMS bağlantısı kapalı, gerçek modda veya gerekli capabilities eksik.");
            return;
        }
        if (!publisher.IsMock || !wms.Profile.AllowMockWrites)
        {
            Block(row, "blocked-write-disabled", "Mock dış sistem yazma izni kapalı; hiçbir talep gönderilmedi.");
            return;
        }
        if (policy.UpdateTargets.Contains(FulfillmentUpdateTargets.Erp, StringComparer.Ordinal) &&
            (!erp.Profile.Enabled || erp.Profile.Mode != "mock" || !erp.Capabilities.IsMock || !erp.Profile.AllowMockWrites))
        {
            Block(row, "blocked-write-disabled", "ERP mock yazma izni hazır değil; hiçbir fulfillment talebi gönderilmedi.");
            return;
        }
        if (policy.WriteSequence.Contains(FulfillmentWriteSteps.ErpOrderRegistration, StringComparer.Ordinal) &&
            (erp.Profile.Mode != "mock" || !erp.Capabilities.IsMock ||
             (erp.Capabilities.Write & ErpWriteCapability.RegisterOrder) == 0 || !erp.Profile.AllowMockWrites))
        {
            Block(row, "blocked-capability", "ERP sipariş kaydı capability veya mock yazma izni hazır değil.");
            return;
        }
        if (policy.WarehousePriority.Length == 0)
        {
            Block(row, "blocked-policy", "Politikada öncelikli depo tanımlı değil.");
            return;
        }

        try
        {
            var overview = await inventory.ReadAsync(ct);
            var knownWarehouses = overview.Warehouses.Where(warehouse => warehouse.IsActive)
                .Select(warehouse => warehouse.Id).ToHashSet(StringComparer.Ordinal);
            if (policy.WarehousePriority.Any(id => !knownWarehouses.Contains(id)))
            {
                Block(row, "blocked-policy", "Politikadaki depolardan biri etkin WMS deposu değil.");
                return;
            }

            if (policy.WriteSequence.Contains(FulfillmentWriteSteps.ErpOrderRegistration, StringComparer.Ordinal))
                row.ErpOrderId = BuildErpOrder(row).Id;

            var available = overview.Stock
                .Where(stock => policy.WarehousePriority.Contains(stock.Warehouse.Id, StringComparer.Ordinal) &&
                    !stock.Snapshot.ProviderManaged)
                .GroupBy(stock => stock.Warehouse.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var allocationByWarehouse = new Dictionary<string, List<WmsFulfillmentLine>>(StringComparer.Ordinal);
            var usedByWarehouseSku = new Dictionary<(string Warehouse, string Sku), decimal>();
            foreach (var line in row.Order.Lines)
            {
                var remaining = line.Quantity;
                foreach (var warehouseId in policy.WarehousePriority)
                {
                    if (remaining <= 0 || !available.TryGetValue(warehouseId, out var rows))
                        continue;
                    var matching = rows.Where(stock => stock.Snapshot.Sku.Equals(line.Sku, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matching.Length > 1)
                    {
                        Block(row, "blocked-ambiguity", $"Depo {warehouseId} içinde SKU {line.Sku} için birden fazla stok kaydı var.");
                        return;
                    }
                    if (matching.Length == 0)
                        continue;
                    var stock = matching[0];
                    if (stock.SellableQuantity is null || stock.SellableQuantity < 0 ||
                        !stock.CanAllocateToChannels || string.IsNullOrWhiteSpace(stock.Snapshot.Unit))
                        continue;
                    var stockKey = (warehouseId, line.Sku.ToUpperInvariant());
                    var take = Math.Min(remaining, Math.Max(0, stock.SellableQuantity.Value - usedByWarehouseSku.GetValueOrDefault(stockKey)));
                    if (take <= 0)
                        continue;
                    if (stock.Snapshot.Unit != line.Unit)
                    {
                        Block(row, "blocked-ambiguity", $"SKU {line.Sku} için WMS birimi {stock.Snapshot.Unit}, sipariş birimi {line.Unit} ile eşleşmiyor.");
                        return;
                    }
                    if (!allocationByWarehouse.TryGetValue(warehouseId, out var tasks))
                        allocationByWarehouse.Add(warehouseId, tasks = []);
                    tasks.Add(new(line.Sku, take, line.Unit, line.ExternalLineId));
                    usedByWarehouseSku[stockKey] = usedByWarehouseSku.GetValueOrDefault(stockKey) + take;
                    remaining -= take;
                }
                if (remaining > 0)
                    row.UnallocatedByLine[line.ExternalLineId] = remaining;
            }

            if (row.UnallocatedByLine.Count > 0 && !policy.AllowPartialShipments)
            {
                Block(row, "blocked-stock", "Stok yetersiz veya bilinmiyor; politika kısmi sevke izin vermiyor.");
                return;
            }
            if (allocationByWarehouse.Count == 0)
            {
                Block(row, "blocked-stock", "Depolarda güvenle tahsis edilebilir stok yok.");
                return;
            }

            row.PolicySnapshot = CopyPolicy(policy);
            row.Warehouses = allocationByWarehouse.Select(pair =>
            {
                var externalRequestId = StableId(row.Id, pair.Key);
                return new FulfillmentWarehouseTask
                {
                    WarehouseId = pair.Key,
                    ExternalRequestId = externalRequestId,
                    IdempotencyKey = StableId(row.Id, row.IdempotencyKey, pair.Key),
                    Lines = pair.Value.ToArray()
                };
            }).ToArray();
            row.NextWriteStep = 0;
            row.State = row.UnallocatedByLine.Count > 0 ? "partially-allocated" : "validated";
            row.BlockReason = null;
            AddEvent(row, row.State, row.UnallocatedByLine.Count > 0
                ? "Sipariş doğrulandı; yalnızca bilinen ve satılabilir stok kısmi sevk için tahsis edildi."
                : "Sipariş, kimlikler, stok ve depo dağılımı doğrulandı.");
            Save();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (WmsProviderException error)
        {
            Block(row, "blocked", $"WMS doğrulaması başarısız: {error.Error.Message}");
        }
        catch (IOException)
        {
            Block(row, "blocked", "Stok görünümü okunamadı; sipariş güvenli biçimde bloke edildi.");
        }
        catch (ArgumentException error)
        {
            Block(row, "blocked-validation", error.Message);
        }
    }

    private async Task AdvanceErpRegistrationAsync(FulfillmentWorkflowRecord row, CancellationToken ct)
    {
        if (row.ErpRegistrationState == "registered")
        {
            row.NextWriteStep++;
            SetState(row, "erp-registered", "ERP sipariş kaydı hazır.");
            return;
        }
        if (row.ErpRegistrationState == "unknown")
        {
            try
            {
                var status = await erp.GetOrderStatusAsync(row.ErpOrderId!, ct);
                if (status.Data.Status == "registered")
                {
                    row.ErpRegistrationState = "registered";
                    row.NextWriteStep++;
                    SetState(row, "erp-registered", "ERP sipariş kaydı durum sorgusuyla doğrulandı.", status.CorrelationId);
                }
                else
                    SetState(row, "erp-outcome-unknown", "ERP yanıtı henüz kesin değil; tekrar kayıt gönderilmedi.", status.CorrelationId);
            }
            catch (ErpProviderException error)
            {
                SetState(row, "erp-outcome-unknown", $"ERP sonucu belirsiz ({error.Error.Code}); yeni kayıt gönderilmedi.");
            }
            return;
        }
        if(row.RequiresPreparedErpAcceptance)
        {
            SetState(row,"erp-preparation-required","Merkezi ERP aktarım servisinden doğrulanmış kabul referansı bekleniyor.");
            return;
        }

        row.ErpRegistrationState = "submitting";
        SetState(row, "erp-registration-pending", "ERP sipariş kaydı gönderiliyor.");
        try
        {
            var result = await erp.RegisterOrderAsync(BuildErpOrder(row), ct);
            if (result.Data.Status != "registered")
            {
                row.ErpRegistrationState = "unknown";
                SetState(row, "erp-outcome-unknown", $"ERP kayıt durumu '{result.Data.Status}' belirsiz; tekrar gönderim bloke edildi.", result.CorrelationId);
                return;
            }
            row.ErpRegistrationState = "registered";
            row.NextWriteStep++;
            SetState(row, "erp-registered", "ERP sipariş kaydı kabul edildi.", result.CorrelationId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            row.ErpRegistrationState = "unknown";
            SetState(row, "erp-outcome-unknown", "ERP isteği iptal oldu; dış sonuç bilinmiyor ve tekrar kayıt gönderilmedi.");
            throw;
        }
        catch (ErpProviderException error)
        {
            row.ErpRegistrationState = "unknown";
            SetState(row, "erp-outcome-unknown", $"ERP yanıtı belirsiz ({error.Error.Code}); tekrar kayıt gönderilmedi.");
        }
    }

    private async Task AdvanceWmsSubmissionAsync(FulfillmentWorkflowRecord row, CancellationToken ct)
    {
        var pending = row.Warehouses.FirstOrDefault(task => task.SubmissionState == "pending");
        if (pending is not null)
        {
            var order = new WmsFulfillmentOrder(
                pending.ExternalRequestId,
                row.Order.ExternalOrderId,
                pending.WarehouseId,
                pending.Lines,
                row.Order.OrderedAt,
                pending.IdempotencyKey);
            pending.SubmissionState = "submitting";
            SetState(row, "wms-submission-pending", $"WMS sevk talebi {pending.WarehouseId} deposuna gönderiliyor.", pending.ExternalRequestId);
            try
            {
                var result = await wms.SubmitFulfillmentOrderAsync(order, ct);
                pending.SubmissionState = "submitted";
                pending.AcceptanceState = result.Data.Status == "rejected" ? "rejected" : "pending";
                if (pending.AcceptanceState == "rejected")
                {
                    Block(row, "wms-rejected", $"WMS {pending.WarehouseId} deposu sevk talebini reddetti: {result.Data.Message}");
                    return;
                }
                SetState(row, "wms-submitted", $"WMS talebi alındı; kabul ve işlenme durumu ayrıca sorgulanacak. Depo: {pending.WarehouseId}.", result.CorrelationId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                pending.SubmissionState = "outcome-unknown";
                SetState(row, "wms-outcome-unknown", "WMS gönderimi zaman aşımına/iptale uğradı; önce kabul durumu sorgulanacak.", pending.ExternalRequestId);
                throw;
            }
            catch (WmsProviderException error)
            {
                pending.SubmissionState = "outcome-unknown";
                SetState(row, "wms-outcome-unknown", $"WMS sonucu belirsiz ({error.Error.Code}); aynı external request ID sorgulanacak, yeniden gönderim yapılmayacak.", pending.ExternalRequestId);
            }
            return;
        }

        await PollAcceptanceAsync(row, ct);
    }

    private async Task<FulfillmentWorkflowRecord> PollAcceptanceAsync(FulfillmentWorkflowRecord row, CancellationToken ct)
    {
        foreach (var task in row.Warehouses.Where(task => task.AcceptanceState != "accepted"))
        {
            if (task.SubmissionState is not ("submitted" or "outcome-unknown"))
                return Clone(row);
            try
            {
                var result = await wms.GetAcceptanceStatusAsync(task.ExternalRequestId, ct);
                if (result.Data.FulfillmentOrderId != task.ExternalRequestId)
                    throw Error("InvalidResponse", "WMS kabul sonucu farklı bir talebe ait.");
                if (result.Data.Status == "accepted")
                {
                    task.SubmissionState = "submitted";
                    task.AcceptanceState = "accepted";
                    AddEvent(row, "wms-accepted", $"WMS talebi kabul edildi; fulfillment işlenmiş sayılmıyor. Depo: {task.WarehouseId}.",
                        task.ExternalRequestId, result.CorrelationId);
                }
                else if (result.Data.Status == "rejected")
                {
                    task.AcceptanceState = "rejected";
                    Block(row, "wms-rejected", $"WMS {task.WarehouseId} deposu talebi reddetti: {result.Data.Message}");
                    return Clone(row);
                }
                else
                    SetState(row, "wms-acceptance-pending", $"WMS kabul durumu '{result.Data.Status}' kesin değil.", result.CorrelationId);
            }
            catch (WmsProviderException error)
            {
                SetState(row, "wms-outcome-unknown", $"WMS kabul sonucu henüz doğrulanamadı ({error.Error.Code}); yeniden gönderim yapılmadı.", task.ExternalRequestId);
                return Clone(row);
            }
        }
        if (row.Warehouses.All(task => task.AcceptanceState == "accepted"))
        {
            row.NextWriteStep++;
            SetState(row, "wms-accepted", "Tüm depo talepleri WMS tarafından kabul edildi; rezervasyon/toplama/paketleme/sevk henüz tamamlanmadı.");
        }
        else
            Save();
        return Clone(row);
    }

    private async Task<FulfillmentWorkflowRecord> PollReservationsAsync(FulfillmentWorkflowRecord row, CancellationToken ct)
    {
        foreach (var task in row.Warehouses.Where(task => task.ReservationState != "reserved"))
        {
            try
            {
                var result = await wms.GetReservationStatusAsync(task.ExternalRequestId, ct);
                if (result.Data.FulfillmentOrderId != task.ExternalRequestId || result.Data.WarehouseId != task.WarehouseId ||
                    result.Data.Lines is null || result.Data.Lines.Any(line => line.Quantity <= 0) ||
                    !ReservationMatches(task.Lines, result.Data.Lines))
                {
                    Block(row, "blocked-reservation", "WMS rezervasyon kimliği, deposu, birimi veya miktarı talep ile eşleşmiyor.");
                    return Clone(row);
                }
                if (result.Data.Status is "reserved" or "held")
                {
                    task.ReservationState = "reserved";
                    AddEvent(row, "reserved", $"WMS rezervasyonu doğrulandı: {task.WarehouseId}.", task.ExternalRequestId, result.CorrelationId);
                }
                else
                {
                    SetState(row, "reservation-pending", $"Rezervasyon durumu '{result.Data.Status}' doğrulanana kadar işlem bloke.");
                    return Clone(row);
                }
            }
            catch (WmsProviderException error)
            {
                Block(row, "blocked-reservation", $"WMS rezervasyonu doğrulanamadı ({error.Error.Code}).");
                return Clone(row);
            }
        }
        SetState(row, "reserved", "Tüm depo rezervasyonları doğrulandı.");
        return Clone(row);
    }

    private static bool ReservationMatches(IReadOnlyList<WmsFulfillmentLine> requested, IReadOnlyList<WmsReservationLine> reserved)
    {
        var expected = requested.GroupBy(line => (line.Sku, line.Unit))
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));
        var actual = reserved.GroupBy(line => (line.Sku, line.Unit))
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));
        return expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var quantity) && quantity == pair.Value);
    }

    private async Task<FulfillmentWorkflowRecord> PollPickingAsync(FulfillmentWorkflowRecord row, CancellationToken ct)
    {
        foreach (var task in row.Warehouses.Where(task => task.PickingState != "picked"))
        {
            try
            {
                var result = await wms.GetPickingStatusAsync(task.ExternalRequestId, ct);
                if (result.Data.FulfillmentOrderId != task.ExternalRequestId)
                    throw Error("InvalidResponse", "Toplama sonucu farklı bir talebe ait.");
                if (result.Data.Status is "picked" or "completed")
                    task.PickingState = "picked";
                else
                {
                    SetState(row, "picking", $"Toplama henüz tamamlanmadı: {task.WarehouseId} ({result.Data.Status}).", result.CorrelationId);
                    return Clone(row);
                }
            }
            catch (WmsProviderException error)
            {
                Block(row, "blocked-picking", $"Toplama durumu doğrulanamadı ({error.Error.Code}).");
                return Clone(row);
            }
        }
        SetState(row, "picked", "Tüm depolarda toplama tamamlandı.");
        return Clone(row);
    }

    private async Task<FulfillmentWorkflowRecord> PollPackingAsync(FulfillmentWorkflowRecord row, CancellationToken ct)
    {
        foreach (var task in row.Warehouses.Where(task => task.PackingState != "packed"))
        {
            try
            {
                var result = await wms.GetPackingStatusAsync(task.ExternalRequestId, ct);
                if (result.Data.FulfillmentOrderId != task.ExternalRequestId)
                    throw Error("InvalidResponse", "Paketleme sonucu farklı bir talebe ait.");
                if (result.Data.Status is "packed" or "completed")
                    task.PackingState = "packed";
                else
                {
                    SetState(row, "packing", $"Paketleme henüz tamamlanmadı: {task.WarehouseId} ({result.Data.Status}).", result.CorrelationId);
                    return Clone(row);
                }
            }
            catch (WmsProviderException error)
            {
                Block(row, "blocked-packing", $"Paketleme durumu doğrulanamadı ({error.Error.Code}).");
                return Clone(row);
            }
        }
        SetState(row, "packed", "Tüm depolarda paketleme tamamlandı.");
        return Clone(row);
    }

    private async Task ReadShipmentsAndPublishAsync(FulfillmentWorkflowRecord row, CancellationToken ct)
    {
        foreach (var task in row.Warehouses)
        {
            try
            {
                var result = await wms.GetShipmentStatusAsync(task.ExternalRequestId, ct);
                if (result.Data.FulfillmentOrderId != task.ExternalRequestId)
                    throw Error("InvalidResponse", "Sevkiyat sonucu farklı bir talebe ait.");
                if (result.Data.Status is not ("shipped" or "partially-shipped" or "in-transit" or "delivered"))
                {
                    SetState(row, "shipment-pending", $"Sevk henüz doğrulanmadı: {task.WarehouseId} ({result.Data.Status}).", result.CorrelationId);
                    return;
                }
                var packages = result.Data.Packages;
                if (packages is null || packages.Count == 0)
                {
                    Block(row, "blocked-shipment-ambiguity", "WMS paket satırları olmadan sevk sonucu döndürdü; paket veya sevk miktarı varsayılmadı.");
                    return;
                }
                if (!MergePackages(row, task, packages))
                    return;
            }
            catch (WmsProviderException error)
            {
                Block(row, "blocked-shipment", $"Sevkiyat durumu doğrulanamadı ({error.Error.Code}).");
                return;
            }
        }

        var shippedByLine = row.Packages.SelectMany(package => package.Lines)
            .GroupBy(line => line.ExternalLineId ?? line.Sku, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity), StringComparer.Ordinal);
        var complete = row.Order.Lines.All(line => shippedByLine.GetValueOrDefault(line.ExternalLineId) >= line.Quantity);
        await PublishUpdatesAsync(row, complete ? "shipped" : "partially-shipped", ct);
        if (row.Publications.Any(publication => publication.State != "acknowledged"))
        {
            SetState(row, "updates-pending", "Sevkiyat kaydedildi; pazaryeri/ERP güncelleme onayı bekleniyor.");
            return;
        }
        SetState(row, complete ? "processed" : "partially-shipped",
            complete
                ? "Sipariş WMS'de işlendi; paket ve takip bilgileri mock pazaryeri/ERP uçlarına iletildi."
                : "Kısmi sevk bilgisi iletildi; kalan miktar için yeni WMS sevk bilgisi bekleniyor.");
    }

    private bool MergePackages(
        FulfillmentWorkflowRecord row,
        FulfillmentWarehouseTask task,
        IReadOnlyList<WmsShipmentPackage> incoming)
    {
        var allocated = task.Lines.GroupBy(line => line.ExternalLineId ?? line.Sku, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity), StringComparer.Ordinal);
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (incoming.Select(package => package.PackageId).Distinct(StringComparer.Ordinal).Count() != incoming.Count)
        {
            Block(row, "blocked-shipment-ambiguity", "WMS yanıtında tekrarlanan paket kimliği var.");
            return false;
        }
        var replacedIds = incoming.Select(package => task.WarehouseId + "|" + package.PackageId).ToHashSet(StringComparer.Ordinal);
        foreach (var savedPackage in row.Packages.Where(package => package.PackageId.StartsWith(task.WarehouseId + "|", StringComparison.Ordinal) && !replacedIds.Contains(package.PackageId)))
        foreach (var line in savedPackage.Lines)
        {
            var key = line.ExternalLineId ?? line.Sku;
            totals[key] = totals.GetValueOrDefault(key) + line.Quantity;
        }
        foreach (var package in incoming)
        {
            if (string.IsNullOrWhiteSpace(package.PackageId) || package.Lines is null ||
                package.Lines.Count == 0 || package.ShippedAt == default || string.IsNullOrWhiteSpace(package.TrackingNumber) ||
                package.Lines.Any(line => line.Quantity <= 0 || string.IsNullOrWhiteSpace(line.Sku)))
            {
                Block(row, "blocked-shipment-ambiguity", "WMS paket kimliği veya satırları geçersiz.");
                return false;
            }
            foreach (var line in package.Lines)
            {
                var key = line.ExternalLineId ?? line.Sku;
                var requested = task.Lines.FirstOrDefault(item => (item.ExternalLineId ?? item.Sku) == key);
                if (!allocated.ContainsKey(key) || requested is null || requested.Sku != line.Sku || requested.Unit != line.Unit)
                {
                    Block(row, "blocked-shipment-ambiguity", "WMS sevk satırı bu depo tahsisiyle eşleşmiyor.");
                    return false;
                }
                totals[key] = totals.GetValueOrDefault(key) + line.Quantity;
            }
        }
        if (totals.Any(pair => pair.Value > allocated[pair.Key]))
        {
            Block(row, "blocked-shipment-ambiguity", "WMS sevk miktarı depo tahsisinden fazla; otomatik güncelleme yapılmadı.");
            return false;
        }

        foreach (var package in incoming)
        {
            var scoped = package with
            {
                PackageId = task.WarehouseId + "|" + package.PackageId,
                Lines = package.Lines.ToArray()
            };
            var existing = row.Packages.FirstOrDefault(item => item.PackageId == scoped.PackageId);
            if (existing is not null && JsonSerializer.Serialize(existing) != JsonSerializer.Serialize(scoped))
            {
                Block(row, "blocked-shipment-ambiguity", "WMS aynı paket kimliğine farklı içerik döndürdü.");
                return false;
            }
            if (existing is null)
                row.Packages.Add(scoped);
        }
        Save();
        return true;
    }

    private async Task PublishUpdatesAsync(FulfillmentWorkflowRecord row, string status, CancellationToken ct)
    {
        foreach (var package in row.Packages)
        foreach (var target in row.PolicySnapshot!.UpdateTargets)
        {
            var key = StableId(row.IdempotencyKey, target, package.PackageId);
            var publication = row.Publications.FirstOrDefault(item => item.IdempotencyKey == key);
            if (publication?.State == "acknowledged")
                continue;
            if (publication is null)
            {
                publication = new FulfillmentWorkflowPublication
                {
                    Target = target,
                    PackageId = package.PackageId,
                    IdempotencyKey = key
                };
                row.Publications.Add(publication);
                Save();
            }
            try
            {
                var receipt = await publisher.PublishAsync(target,
                    new(row.Id, row.Order.Marketplace, row.Order.AccountId, row.Order.ExternalOrderId, status, package),
                    key, ct);
                publication.State = "acknowledged";
                publication.CorrelationId = receipt.CorrelationId;
                AddEvent(row, "status-update-acknowledged", $"{target} uç noktası {package.PackageId} paket bilgisini onayladı.", key, receipt.CorrelationId);
                Save();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                publication.State = "outcome-unknown";
                SetState(row, "updates-pending", $"{target} durum güncellemesi belirsiz; aynı anahtarla güvenle sorgulanmalı.", key);
                throw;
            }
            catch (WmsProviderException error)
            {
                publication.State = "outcome-unknown";
                SetState(row, "updates-pending", $"{target} durum güncellemesi bekliyor ({error.Error.Code}); aynı idempotency key korunuyor.", key);
                return;
            }
        }
    }

    private ContractOrder BuildErpOrder(FulfillmentWorkflowRecord row)
    {
        var variants = canonical.Records<ProductVariant>()
            .GroupBy(variant => variant.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var barcodes = canonical.Records<Barcode>();
        var lines = row.Order.Lines.Select((line, index) =>
        {
            if (!variants.TryGetValue(line.Sku, out var matches) || matches.Length != 1)
                throw Error("MappingRequired", $"ERP sipariş kaydı için SKU {line.Sku} tekil kanonik varyantla eşleşmiyor.");
            var variant = matches[0];
            var barcode = barcodes.Where(item => item.VariantId == variant.Id && item.UnitCode == line.Unit)
                .Select(item => item.Value).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            if (variant.Status != "active" || variant.BaseUnit != line.Unit || barcode.Length > 1)
                throw Error("MappingRequired", $"ERP sipariş kaydı için SKU {line.Sku} varyantı/barkodu belirsiz.");
            var externalLineId = line.ExternalLineId;
            return new OrderLine(
                StableId(row.Id, externalLineId),
                variant.Id,
                externalLineId,
                variant.Sku,
                barcode.SingleOrDefault(),
                line.Quantity,
                line.Unit,
                line.UnitPrice,
                line.Currency);
        }).ToArray();
        var now = DateTimeOffset.UtcNow;
        var context = new RecordContext(erp.Profile.TenantId, "marketplace:" + row.Order.Marketplace,
            row.Order.ExternalOrderId, row.Order.AccountId, row.Id, row.IdempotencyKey, 1, row.Order.OrderedAt, now);
        return new ContractOrder(row.Id, context, row.Order.ExternalOrderId, "new", row.Order.Currency, lines, row.Order.OrderedAt);
    }

    private static void ValidateOrder(FulfillmentWorkflowOrder order)
    {
        if (order is null || !Present(order.Marketplace, 50) || !Present(order.AccountId, 120) ||
            !Present(order.ExternalOrderId, 120) || !Present(order.IdempotencyKey, 160) ||
            !Present(order.Status, 40) || order.OrderedAt == default || !Present(order.Currency, 3) ||
            !string.Equals(order.Currency, order.Currency.ToUpperInvariant(), StringComparison.Ordinal) ||
            order.Currency.Length != 3 || order.Currency.Any(character => character is < 'A' or > 'Z') ||
            order.Lines is null || order.Lines.Count is < 1 or > 1000)
            throw new ArgumentException("Pazaryeri sipariş kimliği, hesabı, para birimi, zamanı veya satırları geçersiz.");
        if (order.Status is not ("Created" or "Picking" or "Invoiced" or "Unshipped" or "Pending" or "PendingAvailability"))
            throw new ArgumentException("Sipariş durumu fulfillment için uygun değil.");
        if (order.Lines.Any(line => line is null || !Present(line.ExternalLineId, 120) ||
            !Present(line.Sku, 120) || !Present(line.Unit, 30) || line.Quantity <= 0 ||
            line.Quantity > 1000000 || decimal.Truncate(line.Quantity) != line.Quantity ||
            line.UnitPrice < 0 || line.UnitPrice > 10000000 || line.Currency != order.Currency) ||
            order.Lines.Select(line => line.ExternalLineId).Distinct(StringComparer.Ordinal).Count() != order.Lines.Count)
            throw new ArgumentException("Sipariş satır kimlikleri, SKU, birim, miktar veya fiyat geçersiz.");
    }

    private void ValidatePolicy(FulfillmentWorkflowPolicy policy)
    {
        if (policy is null || policy.WriteSequence is null || policy.WarehousePriority is null ||
            policy.UpdateTargets is null || policy.WriteSequence.Length is < 1 or > 2 ||
            policy.WriteSequence.Count(step => step == FulfillmentWriteSteps.WmsFulfillmentSubmission) != 1 ||
            policy.WriteSequence.Any(step => step is not (FulfillmentWriteSteps.ErpOrderRegistration or FulfillmentWriteSteps.WmsFulfillmentSubmission)) ||
            policy.WriteSequence.Distinct(StringComparer.Ordinal).Count() != policy.WriteSequence.Length ||
            policy.WarehousePriority.Length is < 1 or > 20 ||
            policy.WarehousePriority.Any(id => !Present(id, 120)) ||
            policy.WarehousePriority.Distinct(StringComparer.Ordinal).Count() != policy.WarehousePriority.Length ||
            policy.UpdateTargets.Length != 2 ||
            !policy.UpdateTargets.Contains(FulfillmentUpdateTargets.Marketplace, StringComparer.Ordinal) ||
            !policy.UpdateTargets.Contains(FulfillmentUpdateTargets.Erp, StringComparer.Ordinal) ||
            policy.UpdateTargets.Distinct(StringComparer.Ordinal).Count() != 2)
            throw new ArgumentException("Onaylı politika bir WMS adımı, benzersiz depo sırası ve marketplace+ERP güncellemelerini içermelidir.");
        if (policy.Approved && !publisher.IsMock)
            throw new ArgumentException("Bu sürümde yalnızca açıkça mock olan dış uçlar onaylanabilir.");
    }

    private void NormalizeState()
    {
        state.Workflows ??= [];
        state.Policy ??= new(false, [], [], false, [FulfillmentUpdateTargets.Marketplace, FulfillmentUpdateTargets.Erp]);
        foreach (var row in state.Workflows)
        {
            row.Warehouses ??= [];
            row.Packages ??= [];
            row.UnallocatedByLine ??= new(StringComparer.Ordinal);
            row.Publications ??= [];
            row.History ??= [];
        }
    }

    private FulfillmentWorkflowRecord Find(string id) =>
        state.Workflows.SingleOrDefault(row => row.Id == id) ?? throw Error("NotFound", "Fulfillment iş akışı bulunamadı.");

    private FulfillmentWorkflowRecord BlockAndClone(FulfillmentWorkflowRecord row, string status, string message)
    {
        Block(row, status, message);
        return Clone(row);
    }

    private void Block(FulfillmentWorkflowRecord row, string status, string reason)
    {
        row.BlockReason = reason;
        SetState(row, status, reason);
    }

    private void SetState(
        FulfillmentWorkflowRecord row,
        string status,
        string message,
        string? externalRequestId = null,
        string? correlationId = null)
    {
        row.State = status;
        row.BlockReason = status.StartsWith("blocked", StringComparison.Ordinal) || status == "wms-rejected" ? message : null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        AddEvent(row, status, message, externalRequestId, correlationId);
        Save();
    }

    private static void AddEvent(
        FulfillmentWorkflowRecord row,
        string status,
        string message,
        string? externalRequestId = null,
        string? correlationId = null)
    {
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.History.Add(new(row.UpdatedAt, status, message, externalRequestId, correlationId));
        if (row.History.Count > MaximumHistory)
            row.History.RemoveRange(0, row.History.Count - MaximumHistory);
    }

    private void Save()
    {
        var temporary = stateFile + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json));
            File.Move(temporary, stateFile, true);
        }
        catch
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            throw;
        }
    }

    private void Restore(string serialized)
    {
        state = JsonSerializer.Deserialize<FulfillmentWorkflowState>(serialized, Json)
            ?? throw new InvalidOperationException("Fulfillment state rollback failed.");
        NormalizeState();
    }

    private static FulfillmentWorkflowPolicy CopyPolicy(FulfillmentWorkflowPolicy policy) =>
        policy with
        {
            WriteSequence = policy.WriteSequence?.ToArray() ?? [],
            WarehousePriority = policy.WarehousePriority?.ToArray() ?? [],
            UpdateTargets = policy.UpdateTargets?.ToArray() ?? []
        };

    private static FulfillmentWorkflowOrder CloneOrder(FulfillmentWorkflowOrder order) =>
        order with { Lines = order.Lines.Select(line => line with { }).ToArray() };

    private static FulfillmentWorkflowRecord Clone(FulfillmentWorkflowRecord row) =>
        JsonSerializer.Deserialize<FulfillmentWorkflowRecord>(JsonSerializer.Serialize(row, Json), Json)
        ?? throw new InvalidOperationException("Fulfillment record could not be cloned.");

    private static string Fingerprint(FulfillmentWorkflowOrder order) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            order with { Lines = order.Lines.OrderBy(line => line.ExternalLineId, StringComparer.Ordinal).ToArray() }, Json))));

    private static string OrderScope(FulfillmentWorkflowOrder order) =>
        $"{order.Marketplace.ToUpperInvariant()}\u001f{order.AccountId}\u001f{order.ExternalOrderId}";

    private static string StableId(params string[] values)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', values)));
        return Convert.ToHexString(digest).ToLowerInvariant()[..32];
    }

    private static bool Present(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max && value == value.Trim();

    private static WmsProviderException Error(string code, string message) => new(new(code, message));
}
