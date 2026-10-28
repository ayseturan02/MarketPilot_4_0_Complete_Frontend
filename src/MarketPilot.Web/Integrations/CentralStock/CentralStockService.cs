using System.Text.Json;
using MarketPilot.Web.Integrations.Demo;
using MarketPilot.Web.Integrations.Trendyol;

namespace MarketPilot.Web.Integrations.CentralStock;

public sealed class CentralStockItem
{
    public string Sku { get; set; } = "";
    public string Barcode { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Last quantity read from the stock application, minus sales it has not been told about yet.</summary>
    public int Quantity { get; set; }
}
public sealed class CentralSale
{
    public string Id { get; set; } = "";
    public string StoreId { get; set; } = "";
    public string StoreName { get; set; } = "";
    public string Marketplace { get; set; } = "";
    public string OrderNumber { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Barcode { get; set; } = "";
    public int Quantity { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public bool Reported { get; set; }
    public string? ReportError { get; set; }
}
public sealed class PendingStockPush
{
    public int Quantity { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? BatchId { get; set; }
}
public sealed record CentralStockSettings(bool AutoSync = false, int IntervalSeconds = 60, int SafetyStock = 0);
public sealed record CentralStockEvent(DateTimeOffset Date, string Level, string Message);
public sealed record CentralStoreStatus(string Status, string Message, DateTimeOffset At);
public sealed record DemoSaleRequest(string ConnectionId, string Barcode, int Quantity);
public sealed class CentralStockState
{
    public CentralStockSettings Settings { get; set; } = new();
    public Dictionary<string, CentralStockItem> Items { get; set; } = new();
    public List<CentralSale> Sales { get; set; } = new();
    public HashSet<string> SeenOrderLines { get; set; } = new();
    public HashSet<string> WatchedStores { get; set; } = new();
    public Dictionary<string, int> Pushed { get; set; } = new();
    public Dictionary<string, PendingStockPush> PendingPushes { get; set; } = new();
    public Dictionary<string, int> AppliedQuantities { get; set; } = new();
    public Dictionary<string, int> AdjustmentVersions { get; set; } = new();
    public HashSet<string> ApprovedReturns { get; set; } = new();
    public Dictionary<string, string> UnmatchedSales { get; set; } = new();
    public Dictionary<string, DateTimeOffset> ScannedThrough { get; set; } = new();
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string LastRunStatus { get; set; } = "idle";
    public Dictionary<string, CentralStoreStatus> Stores { get; set; } = new();
    /// <summary>Quantities of the built-in sample stock application, used while no real one is configured.</summary>
    public Dictionary<string, int> DemoSource { get; set; } = new();
    public List<CentralStockEvent> Events { get; set; } = new();
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? LastSourceAt { get; set; }
    public string? LastError { get; set; }
}

// One stock truth for every channel: read the stock application, subtract new channel sales, report them back and
// push the remaining quantity (minus safety stock) to every other store so the same unit cannot be sold twice.
public sealed class CentralStockService
{
    private static readonly HashSet<string> NotSold = new(StringComparer.OrdinalIgnoreCase) { "Cancelled", "UnSupplied", "Returned", "UnDelivered", "İptal" };
    private readonly object gate = new();
    private readonly SemaphoreSlim run = new(1, 1);
    private readonly string file;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IServiceProvider services;
    private readonly IConfiguration configuration;
    private CentralStockState state;
    private string persistedSnapshot;
    private long revision;
    public long Revision => Interlocked.Read(ref revision);
    private readonly string sourceMode;
    private static string StoreSource(ConnectionState store) => store.Settings.Mode == "real" ? store.Settings.Marketplace + "|" + store.Settings.Environment : store.Id;

    public CentralStockService(IWebHostEnvironment env, IServiceProvider services, IConfiguration configuration)
    {
        this.services = services; this.configuration = configuration;
        var folder = Path.Combine(env.ContentRootPath, "Data"); Directory.CreateDirectory(folder); sourceMode = CentralStockSource.IsConfigured(configuration) ? "real" : "demo";
        file = Path.Combine(folder, sourceMode == "real" ? "central-stock-real.json" : "central-stock.json");
        state = File.Exists(file) ? JsonSerializer.Deserialize<CentralStockState>(File.ReadAllText(file), json) ?? throw new InvalidOperationException("Central stock file cannot be read.") : new();
        if (state.DemoSource.Count == 0) state.DemoSource = DemoSourceSeed();
        if (state.LastRunStatus == "running") { state.LastRunStatus = "blocked"; state.LastError = "Önceki senkronizasyon yarıda kaldı. Yeniden çalıştırılabilir."; }
        persistedSnapshot = JsonSerializer.Serialize(state, json);
    }

    public bool RealSource => sourceMode == "real";
    public bool LastRunSucceeded { get { lock (gate) return state.LastRunStatus == "completed"; } }
    public CentralStockSettings Settings { get { lock (gate) return state.Settings; } }
    public bool Due { get { lock (gate) return state.Settings.AutoSync && (state.LastAttemptAt is null || DateTimeOffset.UtcNow - state.LastAttemptAt >= TimeSpan.FromSeconds(state.Settings.IntervalSeconds)); } }

    public void UpdateSettings(CentralStockSettings settings)
    {
        if (settings.IntervalSeconds is < 30 or > 3600 || settings.SafetyStock is < 0 or > 100000) throw new ArgumentException("Aralık 30–3600 saniye, güvenlik stoğu 0–100.000 olmalı.");
        lock (gate) { state.Settings = settings; Event("info", $"Ayarlar güncellendi: otomatik {(settings.AutoSync ? "açık" : "kapalı")}, {settings.IntervalSeconds} sn, güvenlik stoğu {settings.SafetyStock}."); Save(); }
    }

    public async Task<bool> RunAsync(CancellationToken ct)
    {
        if (!await run.WaitAsync(0, ct)) return false;
        try
        {
            lock (gate) { state.LastAttemptAt = DateTimeOffset.UtcNow; state.LastRunStatus = "running"; Save(); }
            using var scope = services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IntegrationRegistry>();
            var demo = scope.ServiceProvider.GetRequiredService<DemoWorkspace>();
            // A demo sale can never reach the real ERP; aliases of one real account are counted once.
            var stores = registry.Connections.Values.Where(s => s.Settings.Mode == sourceMode)
                .OrderBy(s => s.Id).GroupBy(StoreSource).Select(g => g.First()).ToArray();
            if (!await PullSourceAsync(scope.ServiceProvider, ct)) { Finish(false); return true; }
            var complete = true;
            foreach (var store in stores) complete &= await CollectSalesAsync(scope.ServiceProvider, registry, demo, store, ct);
            await ReportSalesAsync(scope.ServiceProvider, ct);
            if (complete)
                foreach (var store in stores) await DistributeAsync(scope.ServiceProvider, registry, demo, store, ct);
            else lock (gate) state.LastError = "Bazı satışlar okunamadı veya eşleşmedi. Eksik stokla kanallara gönderim durduruldu.";
            Finish(complete);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Finish(false); throw; }
        catch
        {
            lock (gate) { state.LastRunStatus = "error"; state.LastError = "Senkronizasyon tamamlanamadı. Yeniden deneyin."; }
            throw;
        }
        finally { run.Release(); }
    }

    private void Finish(bool complete)
    {
        lock (gate) { state.LastRunStatus = complete ? "completed" : "blocked"; if (complete) state.LastRunAt = DateTimeOffset.UtcNow; Save(); }
    }

    public object Status()
    {
        lock (gate) return new { sourceMode, state.LastAttemptAt, state.LastRunAt, state.LastRunStatus, state.LastError,
            pendingSales = state.Sales.Count(s => !s.Reported), unmatchedSales = state.UnmatchedSales.Count,
            pendingPushes = state.PendingPushes.Count, autoSync = state.Settings.AutoSync, running = run.CurrentCount == 0 };
    }

    private async Task<bool> PullSourceAsync(IServiceProvider sp, CancellationToken ct)
    {
        List<SourceStockItem> rows;
        try
        {
            if (RealSource) rows = await sp.GetRequiredService<CentralStockSource>().ReadAsync(ct);
            else lock (gate) rows = DemoCatalog.Select(x => new SourceStockItem(x.Sku, x.Barcode, x.Name, state.DemoSource.GetValueOrDefault(x.Sku))).ToList();
        }
        catch (Exception e) when (e is IntegrationException or HttpRequestException or JsonException or OperationCanceledException)
        {
            lock (gate)
            {
                state.LastError = e is IntegrationException ie ? ie.Message : e is OperationCanceledException ? "Stok uygulaması zaman aşımına uğradı." : "Stok uygulamasına ulaşılamadı veya yanıtı okunamadı.";
                // Keep the displayable snapshot, but never send stale stock to any store.
                Event("error", state.LastError + " Güncel stok okunamadığı için dağıtım durduruldu."); Save(); return false;
            }
        }
        lock (gate)
        {
            var unreported = state.Sales.Where(s => !s.Reported).GroupBy(s => Key(s.Sku, s.Barcode)).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
            var items = new Dictionary<string, CentralStockItem>();
            foreach (var row in rows)
            {
                var key = Key(row.Sku, row.Barcode);
                if (items.ContainsKey(key) || row.Barcode.Length > 0 && items.Values.Any(i => i.Barcode == row.Barcode))
                    throw new IntegrationException("ambiguous_stock", "Stok kaynağında tekrarlanan SKU veya barkod var. Dağıtım durduruldu.", 409);
                items[key] = new() { Sku = row.Sku, Barcode = row.Barcode, Name = row.Name, Quantity = row.Quantity - unreported.GetValueOrDefault(key) };
            }
            state.Items = items; state.LastSourceAt = DateTimeOffset.UtcNow; state.LastError = null; Save();
        }
        return true;
    }

    private async Task<bool> CollectSalesAsync(IServiceProvider sp, IntegrationRegistry registry, DemoWorkspace demo, ConnectionState store, CancellationToken ct)
    {
        IReadOnlyList<MarketplaceOrder> orders;
        DemoWorkspaceState? snapshot = null;
        var scannedUntil = DateTimeOffset.UtcNow;
        try
        {
            if (store.Settings.Mode == "demo") { snapshot = demo.Snapshot(store.Settings); orders = snapshot.Orders; }
            else
            {
                var adapter = sp.GetServices<IMarketplaceAdapter>().FirstOrDefault(a => a.Marketplace == store.Settings.Marketplace);
                if (adapter is null) { SetStore(store.Id, "error", "Sipariş adaptörü bulunamadı."); return false; }
                var gateway = registry.Gates.GetOrAdd(store.Id, _ => new(1, 1));
                if (!await gateway.WaitAsync(0, ct)) { SetStore(store.Id, "busy", "Mağazada başka bir işlem sürüyor; bu tur stok dağıtılmayacak."); return false; }
                try
                {
                    DateTimeOffset from;
                    lock (gate) from = state.ScannedThrough.TryGetValue(StoreSource(store), out var previousScan) ? previousScan.AddMinutes(-5) : scannedUntil.AddDays(-2);
                    orders = await ReadAllOrders(adapter, store.Settings, from, scannedUntil, ct);
                }
                finally { gateway.Release(); }
            }
        }
        catch (Exception e) when (e is IntegrationException or HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            SetStore(store.Id, "error", "Siparişler tamamen okunamadı: " + (e is IntegrationException ie ? ie.Message : "bağlantıyı kontrol edin."));
            return false;
        }
        lock (gate)
        {
            var lines = orders.SelectMany(o => o.Lines.Select(l => (Order: o, Line: l, Key: $"{StoreSource(store)}|{(o.PackageId.Length > 0 ? o.PackageId : o.OrderNumber)}|{l.Id}|{l.Barcode}"))).ToArray();
            if (state.WatchedStores.Add(StoreSource(store)))
            {
                foreach (var x in lines)
                {
                    state.SeenOrderLines.Add(x.Key);
                    state.AppliedQuantities[x.Key] = NotSold.Contains(x.Order.Status) ? 0 : x.Line.Quantity;
                }
                if (snapshot is not null) foreach (var claim in snapshot.Claims.Where(c => c.Status == "Onaylandı")) state.ApprovedReturns.Add(store.Id + "|" + claim.Id);
                state.ScannedThrough[StoreSource(store)] = scannedUntil;
                Event("info", $"{store.Settings.Name} izlemeye alındı; mevcut {lines.Length} sipariş satırı geçmiş kabul edildi.");
                Save(); return true;
            }
            var added = 0; var complete = true;
            foreach (var (order, line, key) in lines)
            {
                // Return restocking follows an approved claim, rather than an unreviewed order status.
                if (order.Status.Equals("Returned", StringComparison.OrdinalIgnoreCase)) continue;
                var desired = NotSold.Contains(order.Status) ? 0 : line.Quantity;
                var previous = state.AppliedQuantities.GetValueOrDefault(key, state.SeenOrderLines.Contains(key) ? desired : 0);
                var delta = desired - previous;
                if (delta == 0) { state.UnmatchedSales.Remove(key); continue; }
                var item = Find(line.Sku, line.Barcode);
                if (item is null)
                {
                    complete = false;
                    if (!state.UnmatchedSales.ContainsKey(key)) Event("warning", $"{store.Settings.Name} · {order.OrderNumber}: {line.Sku}/{line.Barcode} eşleşmedi; satış tekrar denenecek.");
                    state.UnmatchedSales[key] = line.Sku + "/" + line.Barcode;
                    continue;
                }
                var version = state.AdjustmentVersions.GetValueOrDefault(key) + 1;
                var id = "sale-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key + "|" + version)))[..24].ToLowerInvariant();
                AddSale(store, item, order.OrderNumber, delta, id);
                state.AppliedQuantities[key] = desired; state.AdjustmentVersions[key] = version;
                state.SeenOrderLines.Add(key); state.UnmatchedSales.Remove(key); added++;
            }
            if (snapshot is not null)
            {
                foreach (var claim in snapshot.Claims.Where(c => c.Status == "Onaylandı"))
                {
                    var key = store.Id + "|" + claim.Id;
                    if (state.ApprovedReturns.Contains(key)) continue;
                    var item = Find("", claim.Barcode);
                    if (item is null) { complete = false; state.UnmatchedSales[key] = claim.Barcode; continue; }
                    AddSale(store, item, claim.OrderNumber, -claim.Quantity, "return-" + key);
                    state.ApprovedReturns.Add(key); state.UnmatchedSales.Remove(key); added++;
                }
            }
            if (complete) state.ScannedThrough[StoreSource(store)] = scannedUntil;
            SetStore(store.Id, complete ? (store.Settings.Mode == "demo" ? "demo" : "connected") : "error", complete ? $"{added} stok hareketi işlendi." : "Eşleşmeyen satışlar var; stok dağıtımı bekliyor.", save: false);
            Save(); return complete;
        }
    }

    private void AddSale(ConnectionState store, CentralStockItem item, string order, int quantity, string id)
    {
        item.Quantity -= quantity;
        state.Sales.Insert(0, new() { Id = id, StoreId = store.Id, StoreName = store.Settings.Name, Marketplace = store.Settings.Marketplace,
            OrderNumber = order, Sku = item.Sku, Barcode = item.Barcode, Quantity = quantity });
        Event(item.Quantity < 0 ? "error" : "info", $"{store.Settings.Name} · {order} · {item.Sku}: {(quantity < 0 ? "Stok iadesi" : "Satış")} {Math.Abs(quantity)} → merkez {item.Quantity}.");
    }

    private static async Task<IReadOnlyList<MarketplaceOrder>> ReadAllOrders(IMarketplaceAdapter adapter, ConnectionSettings settings, DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        var orders = new Dictionary<string, MarketplaceOrder>();
        var requests = 0;
        // Bounded date windows also permit recovery after the application was offline.
        while (from < until)
        {
            var end = from.AddDays(2) < until ? from.AddDays(2) : until;
            var page = 0; string? cursor = null;
            var tokens = new HashSet<string>(); var fingerprints = new HashSet<string>();
            while (true)
            {
                if (++requests > 1000) throw new IntegrationException("scan_limit", "Sipariş taraması sınırı aşıldı; hiçbir sayfa tamamlanmış sayılmadı.", 409);
                var result = await adapter.OrdersAsync(settings, new(Page: page, Size: 50, StartDate: from.ToUnixTimeMilliseconds(), EndDate: end.ToUnixTimeMilliseconds(),
                    OrderByDirection: "ASC", Stream: settings.Marketplace == "trendyol", NextCursor: cursor), ct);
                if (result.Items.Count > 0)
                {
                    var signature = string.Join("|", result.Items.Select(o => o.PackageId + ":" + o.OrderNumber + ":" + o.LastModifiedDate));
                    if (!fingerprints.Add(signature)) throw new IntegrationException("repeated_page", "Sipariş servisi aynı sayfayı tekrarladı; dağıtım durduruldu.", 409);
                }
                foreach (var order in result.Items) orders[order.PackageId.Length > 0 ? order.PackageId : order.OrderNumber] = order;
                if (orders.Count > 200000) throw new IntegrationException("scan_limit", "Sipariş taraması kayıt sınırını aştı.", 409);
                var next = result.NextCursor ?? result.NextPageToken;
                if (!string.IsNullOrWhiteSpace(next))
                {
                    if (next.Length > 8192 || !tokens.Add(next)) throw new IntegrationException("repeated_cursor", "Sipariş imleci geçersiz veya tekrarlanıyor.", 409);
                    cursor = next; page++; continue;
                }
                if (result.HasMore == true) throw new IntegrationException("missing_cursor", "Sipariş devam bilgisi eksik.", 409);
                if (result.HasMore == false || result.TotalPages.HasValue && page + 1 >= result.TotalPages.Value ||
                    !result.TotalPages.HasValue && result.Items.Count < result.Size) break;
                if (result.Items.Count == 0) throw new IntegrationException("incomplete_page", "Sipariş sayfası beklenmedik şekilde boş.", 409);
                page++;
            }
            from = end;
        }
        return orders.Values.ToArray();
    }

    private async Task ReportSalesAsync(IServiceProvider sp, CancellationToken ct)
    {
        CentralSale[] pending; lock (gate) pending = state.Sales.Where(s => !s.Reported).Reverse().ToArray();
        if (pending.Length == 0) return;
        if (RealSource && !CentralStockSource.WritesAllowed(configuration))
        {
            lock (gate) { foreach (var s in pending) s.ReportError = $"Satış bildirimi kapalı ({CentralStockSource.Section}:AllowWrites). Kaynak güncellenene kadar yerelde düşülmeye devam eder."; Save(); }
            return;
        }
        var source = sp.GetRequiredService<CentralStockSource>();
        foreach (var sale in pending)
        {
            try
            {
                if (RealSource) await source.ReportSaleAsync(sale, ct);
                lock (gate)
                {
                    if (!RealSource) state.DemoSource[sale.Sku] = state.DemoSource.GetValueOrDefault(sale.Sku) - sale.Quantity;
                    sale.Reported = true; sale.ReportError = null; Save();
                }
            }
            catch (Exception e) when (e is IntegrationException or HttpRequestException or OperationCanceledException)
            {
                lock (gate) { sale.ReportError = e is IntegrationException ie ? ie.Message : "Stok uygulamasına ulaşılamadı; sonraki çalıştırmada tekrar denenecek."; Save(); }
                return; // keep order: later sales wait for the earlier one
            }
        }
        lock (gate) { var keep = state.Sales.Where(s => !s.Reported).Concat(state.Sales.Where(s => s.Reported).Take(500)).ToHashSet(); state.Sales = state.Sales.Where(keep.Contains).ToList(); Save(); }
    }

    private async Task DistributeAsync(IServiceProvider sp, IntegrationRegistry registry, DemoWorkspace demo, ConnectionState store, CancellationToken ct)
    {
        var safety = Settings.SafetyStock;
        if (store.Settings.Mode == "demo")
        {
            var snapshot = demo.Snapshot(store.Settings); int queued = 0, skipped = 0;
            foreach (var product in snapshot.Products)
            {
                int target; lock (gate) { var item = Find(product.Sku, product.Barcode); if (item is null) continue; target = Math.Max(0, item.Quantity - safety); }
                if (product.Stock == target || snapshot.Jobs.Any(j => j.State == "pending" && j.Kind == "stock" && j.Reference == product.Barcode)) continue;
                if (product.SalePrice is not > 0) { skipped++; continue; }
                try { demo.Enqueue(store.Settings, new("stock", $"central-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{store.Id}-{product.Barcode}", product.Barcode, target, product.SalePrice)); queued++; }
                catch (ArgumentException) { skipped++; } // channel-managed stock (FBA, n11depom) or a full queue
            }
            if (queued + skipped > 0) SetStore(store.Id, "demo", $"{queued} ürünün stoğu merkezi stoğa eşitleniyor{(skipped > 0 ? $"; {skipped} ürün kanal tarafından yönetiliyor veya atlandı" : "")}.");
            return;
        }
        if (store.Settings.Marketplace != "trendyol") { SetStore(store.Id, "readonly", "Bu pazaryerine stok gönderimi henüz desteklenmiyor; stok elle eşitlenmeli."); return; }
        List<(string Barcode, int Target)> changes;
        await ConfirmPushesAsync(sp, store, ct);
        lock (gate) changes = state.Items.Values.Where(i => i.Barcode.Length > 0).Select(i => (i.Barcode, Target: Math.Max(0, i.Quantity - safety)))
            .Where(x => !state.PendingPushes.ContainsKey($"{store.Id}|{x.Barcode}") && state.Pushed.GetValueOrDefault($"{store.Id}|{x.Barcode}", -1) != x.Target).ToList();
        if (changes.Count == 0) return;
        var gateway = registry.Gates.GetOrAdd(store.Id, _ => new(1, 1));
        if (!await gateway.WaitAsync(0, ct)) { SetStore(store.Id, "busy", "Mağazada başka bir işlem sürdüğü için stok bu turda gönderilmedi."); return; }
        try
        {
            var runner = sp.GetRequiredService<TrendyolOperationRunner>(); var operation = TrendyolCatalog.Operations["inventory.updatePriceAndInventory"];
            foreach (var batch in changes.Chunk(1000))
            {
                var body = JsonSerializer.SerializeToElement(new { items = batch.Select(x => new { barcode = x.Barcode, quantity = x.Target }) });
                var result = await runner.RunAsync(operation, store.Settings, new(Body: body, Confirm: true), ct);
                if (!result.Sent) { SetStore(store.Id, "readonly", result.Reason ?? "Stok gönderilmedi."); return; }
                lock (gate)
                {
                    var batchId = result.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("batchRequestId", out var id) ? id.GetString() : null;
                    foreach (var x in batch) state.PendingPushes[$"{store.Id}|{x.Barcode}"] = new() { Quantity = x.Target, BatchId = batchId };
                    Event("info", $"{store.Settings.Name}: {batch.Length} stok isteği alındı; sonuç mağazadan okunarak doğrulanacak."); Save();
                }
            }
            SetStore(store.Id, "pending", $"{changes.Count} ürün için istek gönderildi; mağaza stoğunun doğrulanması bekleniyor.");
        }
        catch (Exception e) when (e is IntegrationException or HttpRequestException or OperationCanceledException or JsonException)
        {
            SetStore(store.Id, "error", "Stok gönderilemedi: " + (e is IntegrationException ie ? ie.Message : "bağlantıyı kontrol edin."));
        }
        finally { gateway.Release(); }
    }

    private async Task ConfirmPushesAsync(IServiceProvider sp, ConnectionState store, CancellationToken ct)
    {
        KeyValuePair<string, PendingStockPush>[] pending;
        lock (gate) pending = state.PendingPushes.Where(p => p.Key.StartsWith(store.Id + "|", StringComparison.Ordinal))
            .OrderBy(p => p.Value.RequestedAt).Take(25).ToArray();
        if (pending.Length == 0) return;
        var adapter = sp.GetServices<IMarketplaceAdapter>().First(a => a.Marketplace == store.Settings.Marketplace);
        foreach (var entry in pending)
        {
            var barcode = entry.Key[(store.Id.Length + 1)..];
            try
            {
                var result = await adapter.ProductsAsync(store.Settings, new(Size: 1, Barcode: barcode), ct);
                var actual = result.Items.SingleOrDefault(p => p.Barcode == barcode);
                lock (gate)
                {
                    if (actual?.Stock == entry.Value.Quantity)
                    {
                        state.Pushed[entry.Key] = entry.Value.Quantity; state.PendingPushes.Remove(entry.Key);
                        Event("info", $"{store.Settings.Name} · {barcode}: mağaza stoğu {actual.Stock} olarak doğrulandı.");
                    }
                    else if (DateTimeOffset.UtcNow - entry.Value.RequestedAt > TimeSpan.FromMinutes(5))
                    {
                        state.PendingPushes.Remove(entry.Key);
                        Event("warning", $"{store.Settings.Name} · {barcode}: istek stoğa yansımadı; sonraki gönderimde yeniden denenecek.");
                    }
                    Save();
                }
            }
            catch (Exception e) when (e is IntegrationException or HttpRequestException or JsonException)
            { SetStore(store.Id, "error", "Gönderilen stok henüz doğrulanamadı; başarılı sayılmadı."); break; }
        }
    }

    public async Task<MarketplaceOrder> DemoSaleAsync(DemoSaleRequest request, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IntegrationRegistry>();
        if (RealSource) throw new ArgumentException("Gerçek stok kaynağı seçiliyken demo satış oluşturulamaz.");
        if (!await run.WaitAsync(0, ct)) throw new ArgumentException("Senkronizasyon sürüyor. Satışı biraz sonra tekrar dene.");
        MarketplaceOrder order;
        try
        {
            if (!registry.Connections.TryGetValue(request.ConnectionId ?? "", out var store) || store.Settings.Mode != "demo") throw new ArgumentException("Demo mağaza bulunamadı.");
            // Establish the baseline before the first sale; otherwise that sale would be mistaken for history.
            if (!state.WatchedStores.Contains(store.Id)) throw new ArgumentException("Önce merkezi stok senkronizasyonunu çalıştır.");
            order = scope.ServiceProvider.GetRequiredService<DemoWorkspace>().SimulateSale(store.Settings, request.Barcode ?? "", request.Quantity);
        }
        finally { run.Release(); }
        await RunAsync(ct);
        return order;
    }

    public object View()
    {
        using var scope = services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IntegrationRegistry>(); var demo = scope.ServiceProvider.GetRequiredService<DemoWorkspace>();
        var stores = registry.Connections.Values.Where(s => s.Settings.Mode == sourceMode).OrderBy(s => s.Settings.Marketplace).ThenBy(s => s.Settings.Name).ToArray();
        var demoProducts = stores.Where(s => s.Settings.Mode == "demo").ToDictionary(s => s.Id, s => demo.Snapshot(s.Settings).Products);
        lock (gate)
        {
            var safety = state.Settings.SafetyStock;
            return new
            {
                source = new { mode = RealSource ? "real" : "demo", host = CentralStockSource.BaseUri(configuration)?.Host, writes = CentralStockSource.WritesAllowed(configuration) },
                settings = state.Settings, state.LastRunAt, state.LastSourceAt, state.LastAttemptAt, state.LastRunStatus, state.LastError,
                unmatchedSales = state.UnmatchedSales.Count, pendingPushes = state.PendingPushes.Count,
                pendingJobs = demoProducts.Count > 0 ? stores.Sum(s => demo.Snapshot(s.Settings).Jobs.Count(j => j.State == "pending")) : 0,
                excludedStores = registry.Connections.Count - stores.Length, running = run.CurrentCount == 0,
                stores = stores.Select(s => new { id = s.Id, name = s.Settings.Name, marketplace = s.Settings.Marketplace, mode = s.Settings.Mode, status = state.Stores.GetValueOrDefault(s.Id) }).ToArray(),
                items = state.Items.Values.OrderBy(i => i.Sku).Select(i => new
                {
                    i.Sku, i.Barcode, i.Name, quantity = i.Quantity, available = Math.Max(0, i.Quantity - safety),
                    unreported = state.Sales.Where(x => !x.Reported && Key(x.Sku, x.Barcode) == Key(i.Sku, i.Barcode)).Sum(x => x.Quantity),
                    channels = stores.Select(s => new
                    {
                        storeId = s.Id,
                        stock = demoProducts.TryGetValue(s.Id, out var products) ? products.FirstOrDefault(p => Matches(i, p.Sku, p.Barcode))?.Stock
                            : state.Pushed.TryGetValue($"{s.Id}|{i.Barcode}", out var pushed) ? pushed : (int?)null
                    }).ToArray()
                }).ToArray(),
                sales = state.Sales.Take(30).Select(s => new { s.Id, s.StoreId, s.StoreName, s.Marketplace, s.OrderNumber, s.Sku, s.Barcode, s.Quantity, s.At, s.Reported, s.ReportError }).ToArray(),
                events = state.Events.Take(40).ToArray()
            };
        }
    }

    private CentralStockItem? Find(string sku, string barcode)
    {
        var rows = state.Items.Values.Where(i => sku.Length > 0 ? i.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase) : barcode.Length > 0 && i.Barcode == barcode).Take(2).ToArray();
        if (rows.Length == 0 && barcode.Length > 0) rows = state.Items.Values.Where(i => i.Barcode == barcode).Take(2).ToArray();
        return rows.Length == 1 && (barcode.Length == 0 || rows[0].Barcode.Length == 0 || rows[0].Barcode == barcode) ? rows[0] : null;
    }
    private static bool Matches(CentralStockItem item, string sku, string barcode) => item.Sku.Length > 0 && item.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase) || item.Barcode.Length > 0 && item.Barcode == barcode;
    private static string Key(string sku, string barcode) => sku.Length > 0 ? "sku:" + sku.ToUpperInvariant() : "barcode:" + barcode;
    private void SetStore(string id, string status, string message, bool save = true) { lock (gate) { state.Stores[id] = new(status, message, DateTimeOffset.UtcNow); if (save) Save(); } }
    private void Event(string level, string message) { state.Events.Insert(0, new(DateTimeOffset.UtcNow, level, message)); if (state.Events.Count > 200) state.Events.RemoveRange(200, state.Events.Count - 200); }
    private void Save()
    {
        try { var candidate = JsonSerializer.Serialize(state, json); var tmp = file + ".tmp"; File.WriteAllText(tmp, candidate); File.Move(tmp, file, true); persistedSnapshot = candidate; Interlocked.Increment(ref revision); }
        catch { state = JsonSerializer.Deserialize<CentralStockState>(persistedSnapshot, json)!; throw; }
    }

    // Same SKUs/barcodes as the demo stores, so the sample stock application matches them out of the box.
    private static readonly (string Sku, string Barcode, string Name, int Quantity)[] DemoCatalog =
    [
        ("TSH-EKR-S", "8691000000001", "Pamuklu Oversize Tişört · Ekru / S", 40), ("TSH-EKR-M", "8691000000002", "Pamuklu Oversize Tişört · Ekru / M", 22),
        ("TSH-SYH-M", "8691000000003", "Pamuklu Oversize Tişört · Siyah / M", 3), ("GML-TS-L", "8691000000004", "Keten Gömlek · Taş / L", 15),
        ("PNT-BEJ-38", "8691000000005", "Dokuma Pantolon · Bej / 38", 9), ("SWT-GRI-M", "8691000000006", "Fermuarlı Sweatshirt · Gri / M", 30),
        ("HRK-KHV-ST", "8691000000007", "Triko Hırka · Kahve / Standart", 10), ("TSH-LCV-L", "8691000000008", "Çizgili Tişört · Lacivert / L", 18),
    ];
    private static Dictionary<string, int> DemoSourceSeed() => DemoCatalog.ToDictionary(x => x.Sku, x => x.Quantity);
}

public sealed class CentralStockWorker(CentralStockService service, ILogger<CentralStockWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                try { if (service.Due) await service.RunAsync(stoppingToken); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { logger.LogWarning("Central stock run failed."); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

public static class CentralStockEndpoints
{
    public static void AddCentralStock(this IServiceCollection services)
    {
        services.AddHttpClient("CentralStock", c => c.Timeout = TimeSpan.FromSeconds(30)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<CentralStockSource>();
        services.AddSingleton<CentralStockService>();
        services.AddHostedService<CentralStockWorker>();
    }

    public static void MapCentralStock(this RouteGroupBuilder integrations)
    {
        var group = integrations.MapGroup("/central-stock");
        group.MapGet("", (CentralStockService service) => Results.Ok(service.View()));
        group.MapPost("/run", async (CentralStockService service, CancellationToken ct) =>
            await service.RunAsync(ct) ? Results.Ok(service.View()) : Results.Json(new { error = "Merkezi stok senkronizasyonu zaten çalışıyor." }, statusCode: 409));
        group.MapPost("/settings", (CentralStockSettings settings, CentralStockService service) =>
        {
            try { service.UpdateSettings(settings); return Results.Ok(service.View()); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        });
        group.MapPost("/demo-sale", async (DemoSaleRequest request, CentralStockService service, CancellationToken ct) =>
        {
            try { var order = await service.DemoSaleAsync(request, ct); return Results.Ok(new { order = order.OrderNumber, view = service.View() }); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        });
    }
}
