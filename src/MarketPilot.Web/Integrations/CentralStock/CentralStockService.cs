using System.Text.Json;
using MarketPilot.Web.Integrations.Demo;
using MarketPilot.Web.Integrations.Trendyol;
using MarketPilot.Web.Synchronization;

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
    /// <summary>When each source was first watched; orders placed before it are already in the stock application's numbers.</summary>
    public Dictionary<string, DateTimeOffset> WatchedSince { get; set; } = new();
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
    private static readonly HashSet<string> NotSold = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cancelled",
        "UnSupplied",
        "Returned",
        "UnDelivered",
        "İptal",
        // A split package keeps its old id with this status while its lines move to new packages that are counted instead.
        "UnPacked",
        "Unpackaged"
    };
    // Only Trendyol's order stream filters by last update. Other adapters filter by order date, so a rolling two-day
    // window keeps cancellations of recent orders visible after the incremental scan has moved past them.
    private static readonly HashSet<string> LastModifiedScans = new(StringComparer.OrdinalIgnoreCase)
    {
        "trendyol"
    };
    private readonly object gate = new();
    private readonly SemaphoreSlim run = new(1, 1);
    private readonly string file;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly IServiceProvider services;
    private readonly IConfiguration configuration;
    private CentralStockState state;
    private string persistedSnapshot;
    private long revision;
    public long Revision => Interlocked.Read(ref revision);

    private readonly string sourceMode;
    private static string StoreSource(ConnectionState store) => store.Settings.Mode == "real" ? store.Settings.Marketplace + "|" + store.Settings.Environment + (store.Settings.CredentialProfile is null ? "" : "|" + store.Settings.CredentialProfile) : store.Id;
    public CentralStockService(IWebHostEnvironment env, IServiceProvider services, IConfiguration configuration)
    {
        this.services = services;
        this.configuration = configuration;
        var folder = Path.Combine(env.ContentRootPath, "Data");
        Directory.CreateDirectory(folder);
        sourceMode = CentralStockSource.IsConfigured(configuration) ? "real" : "demo";
        file = Path.Combine(folder, sourceMode == "real" ? "central-stock-real.json" : "central-stock.json");
        state = File.Exists(file) ? JsonSerializer.Deserialize<CentralStockState>(File.ReadAllText(file), json) ?? throw new InvalidOperationException("Central stock file cannot be read.") : new();
        if (state.DemoSource.Count == 0)
            state.DemoSource = DemoSourceSeed();
        if (state.LastRunStatus == "running")
        {
            state.LastRunStatus = "blocked";
            state.LastError = "Önceki senkronizasyon yarıda kaldı. Yeniden çalıştırılabilir.";
        }

        persistedSnapshot = JsonSerializer.Serialize(state, json);
        var legacy = Path.Combine(folder, "central-stock.json");
        if (sourceMode == "real" && !File.Exists(file) && File.Exists(legacy) && MigrateLegacy(legacy))
            Save();
    }

    // Before real and demo sources had separate files, real-source state lived in central-stock.json. Carry over only what
    // provably belongs to a real store: no demo sale may reach the real stock application, and no real sale may be lost.
    private bool MigrateLegacy(string path)
    {
        CentralStockState? old;
        try
        {
            old = JsonSerializer.Deserialize<CentralStockState>(File.ReadAllText(path), json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (old is null)
            return false;
        // A demo source only ever holds the sample catalogue and reports each sale in the same run.
        var demoItems = DemoCatalog.Select(x => Key(x.Sku, x.Barcode)).ToHashSet();
        if (!old.Items.Keys.Any(k => !demoItems.Contains(k)) && !old.Sales.Any(s => !s.Reported && s.ReportError is not null))
            return false;
        var real = services.GetRequiredService<IntegrationRegistry>().Connections.Values.Where(c => c.Settings.Mode == "real").ToDictionary(c => c.Id);
        // Legacy keys start with the store id; the current ones with the shared account source.
        string? Translate(string key)
        {
            var cut = key.IndexOf('|');
            var id = cut < 0 ? key : key[..cut];
            return real.TryGetValue(id, out var store) ? StoreSource(store) + (cut < 0 ? "" : key[cut..]) : null;
        }

        foreach (var id in old.WatchedStores)
            if (Translate(id)is { } source)
                state.WatchedStores.Add(source);
        foreach (var line in old.SeenOrderLines)
            if (Translate(line)is { } key)
                state.SeenOrderLines.Add(key);
        foreach (var(key, quantity)in old.Pushed)
            if (real.ContainsKey(key.Split('|')[0]))
                state.Pushed[key] = quantity;
        var sales = old.Sales.Where(s => !s.Reported && real.ContainsKey(s.StoreId)).ToList();
        state.Sales.AddRange(sales);
        state.Settings = old.Settings with
        {
            AutoSync = false
        };
        Event("warning", $"Önceki stok dosyasından {sales.Count} bildirilmemiş satış ve izlenen sipariş kayıtları taşındı. Otomatik senkronizasyon kapalı; kontrol edip yeniden aç.");
        return true;
    }

    public bool RealSource => sourceMode == "real";

    public bool LastRunSucceeded
    {
        get
        {
            lock (gate)
                return state.LastRunStatus == "completed";
        }
    }

    public CentralStockSettings Settings
    {
        get
        {
            lock (gate)
                return state.Settings;
        }
    }

    public bool Due
    {
        get
        {
            lock (gate)
                return state.Settings.AutoSync && (state.LastAttemptAt is null || DateTimeOffset.UtcNow - state.LastAttemptAt >= TimeSpan.FromSeconds(state.Settings.IntervalSeconds));
        }
    }

    public void UpdateSettings(CentralStockSettings settings)
    {
        if (settings.IntervalSeconds is < 30 or > 3600 || settings.SafetyStock is < 0 or > 100000)
            throw new ArgumentException("Aralık 30–3600 saniye, güvenlik stoğu 0–100.000 olmalı.");
        lock (gate)
        {
            state.Settings = settings;
            Event("info", $"Ayarlar güncellendi: otomatik {(settings.AutoSync ? "açık" : "kapalı")}, {settings.IntervalSeconds} sn, güvenlik stoğu {settings.SafetyStock}.");
            Save();
        }
    }

    public async Task<bool> RunAsync(CancellationToken ct)
    {
        if (!await run.WaitAsync(0, ct))
            return false;
        try
        {
            lock (gate)
            {
                state.LastAttemptAt = DateTimeOffset.UtcNow;
                state.LastRunStatus = "running";
                Save();
            }

            using var scope = services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IntegrationRegistry>();
            var demo = scope.ServiceProvider.GetRequiredService<DemoWorkspace>();
            // A demo sale can never reach the real ERP; aliases of one real account are counted once.
            var stores = registry.Connections.Values.Where(s => s.Settings.Mode == sourceMode).OrderBy(s => s.Id).GroupBy(StoreSource).Select(g => g.First()).ToArray();
            if (!await PullSourceAsync(scope.ServiceProvider, ct))
            {
                Finish(false);
                return true;
            }

            var complete = true;
            foreach (var store in stores)
                complete &= await CollectSalesAsync(scope.ServiceProvider, registry, demo, store, ct);
            await ReportSalesAsync(scope.ServiceProvider, ct);
            if (complete)
                foreach (var store in stores)
                    await DistributeAsync(scope.ServiceProvider, registry, demo, store, ct);
            else
                lock (gate)
                    state.LastError = "Bazı satışlar okunamadı veya eşleşmedi. Eksik stokla kanallara gönderim durduruldu.";
            Finish(complete);
            return true;
        }
        catch (OperationCanceledException)when (ct.IsCancellationRequested)
        {
            Finish(false);
            throw;
        }
        catch
        {
            lock (gate)
            {
                state.LastRunStatus = "error";
                state.LastError = "Senkronizasyon tamamlanamadı. Yeniden deneyin.";
            }

            throw;
        }
        finally
        {
            run.Release();
        }
    }

    private void Finish(bool complete)
    {
        lock (gate)
        {
            state.LastRunStatus = complete ? "completed" : "blocked";
            if (complete)
                state.LastRunAt = DateTimeOffset.UtcNow;
            Save();
        }
    }

    public object Status()
    {
        lock (gate)
            return new
            {
                sourceMode,
                state.LastAttemptAt,
                state.LastRunAt,
                state.LastRunStatus,
                state.LastError,
                pendingSales = state.Sales.Count(s => !s.Reported),
                unmatchedSales = state.UnmatchedSales.Count,
                pendingPushes = state.PendingPushes.Count + services.GetRequiredService<StockOutbox>().PendingCount(sourceMode),
                autoSync = state.Settings.AutoSync,
                running = run.CurrentCount == 0
            };
    }

    private async Task<bool> PullSourceAsync(IServiceProvider sp, CancellationToken ct)
    {
        List<SourceStockItem> rows;
        try
        {
            if (RealSource)
                rows = await sp.GetRequiredService<CentralStockSource>().ReadAsync(ct);
            else
                lock (gate)
                    rows = DemoCatalog.Select(x => new SourceStockItem(x.Sku, x.Barcode, x.Name, state.DemoSource.GetValueOrDefault(x.Sku))).ToList();
        }
        catch (Exception e)when (e is IntegrationException or HttpRequestException or JsonException or OperationCanceledException)
        {
            lock (gate)
            {
                state.LastError = e is IntegrationException ie ? ie.Message : e is OperationCanceledException ? "Stok uygulaması zaman aşımına uğradı." : "Stok uygulamasına ulaşılamadı veya yanıtı okunamadı.";
                // Keep the displayable snapshot, but never send stale stock to any store.
                Event("error", state.LastError + " Güncel stok okunamadığı için dağıtım durduruldu.");
                Save();
                return false;
            }
        }

        lock (gate)
        {
            var unreported = state.Sales.Where(s => !s.Reported).GroupBy(s => Key(s.Sku, s.Barcode)).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
            var items = new Dictionary<string, CentralStockItem>();
            var barcodes = new HashSet<string>();
            foreach (var row in rows)
            {
                var key = Key(row.Sku, row.Barcode);
                if (items.ContainsKey(key) || row.Barcode.Length > 0 && !barcodes.Add(row.Barcode))
                {
                    // Ambiguous source data blocks distribution like a failed read, without failing the background run.
                    state.LastError = $"Stok kaynağında tekrarlanan SKU veya barkod var ({(row.Sku.Length > 0 ? row.Sku : row.Barcode)}).";
                    Event("error", state.LastError + " Dağıtım durduruldu.");
                    Save();
                    return false;
                }

                items[key] = new()
                {
                    Sku = row.Sku,
                    Barcode = row.Barcode,
                    Name = row.Name,
                    Quantity = row.Quantity - unreported.GetValueOrDefault(key)
                };
            }

            state.Items = items;
            state.LastSourceAt = DateTimeOffset.UtcNow;
            state.LastError = null;
            Save();
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
            if (store.Settings.Mode == "demo")
            {
                snapshot = demo.Snapshot(store.Settings);
                orders = snapshot.Orders;
            }
            else
            {
                var adapter = MarketplaceClients.Resolve(sp, store.Settings);
                if (adapter is null)
                {
                    SetStore(store.Id, "error", "Sipariş adaptörü bulunamadı.");
                    return false;
                }

                // The stock outbox holds this gate for one marketplace call, so wait for it instead of skipping the whole run.
                var gateway = registry.Gates.GetOrAdd(store.Id, _ => new(1, 1));
                if (!await gateway.WaitAsync(TimeSpan.FromSeconds(90), ct))
                {
                    SetStore(store.Id, "busy", "Mağazada başka bir işlem sürüyor; bu tur stok dağıtılmayacak.");
                    return false;
                }

                try
                {
                    DateTimeOffset from;
                    // Line keys deduplicate the overlap, so a generous one costs nothing and absorbs clock drift.
                    lock (gate)
                        from = state.ScannedThrough.TryGetValue(StoreSource(store), out var previousScan) ? previousScan.AddMinutes(-60) : scannedUntil.AddDays(-2);
                    if (!LastModifiedScans.Contains(store.Settings.Marketplace) && from > scannedUntil.AddDays(-2))
                        from = scannedUntil.AddDays(-2);
                    orders = await MarketplaceOrderReader.ReadAllAsync(adapter, store.Settings, from, scannedUntil, ct);
                }
                finally
                {
                    gateway.Release();
                }
            }
        }
        catch (Exception e)when (e is IntegrationException or HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            SetStore(store.Id, "error", "Siparişler tamamen okunamadı: " + (e is IntegrationException ie ? ie.Message : "bağlantıyı kontrol edin."));
            return false;
        }

        lock (gate)
        {
            var lines = orders.SelectMany(o => o.Lines.Where(l => !ChannelFulfilled(store, o, l)).Select(l => (Order: o, Line: l, Key: $"{StoreSource(store)}|{(o.PackageId.Length > 0 ? o.PackageId : o.OrderNumber)}|{l.Id}|{l.Barcode}"))).ToArray();
            if (state.WatchedStores.Add(StoreSource(store)))
            {
                state.WatchedSince[StoreSource(store)] = scannedUntil;
                foreach (var x in lines)
                {
                    state.SeenOrderLines.Add(x.Key);
                    state.AppliedQuantities[x.Key] = NotSold.Contains(x.Order.Status) ? 0 : x.Line.Quantity;
                }

                if (snapshot is not null)
                    foreach (var claim in snapshot.Claims.Where(c => c.Status == "Onaylandı"))
                        state.ApprovedReturns.Add(store.Id + "|" + claim.Id);
                state.ScannedThrough[StoreSource(store)] = scannedUntil;
                Event("info", $"{store.Settings.Name} izlemeye alındı; mevcut {lines.Length} sipariş satırı geçmiş kabul edildi.");
                Save();
                return true;
            }

            // Sources watched by an older version have no start time; every order before their last complete scan was already seen.
            if (!state.WatchedSince.ContainsKey(StoreSource(store)))
                state.WatchedSince[StoreSource(store)] = state.ScannedThrough.GetValueOrDefault(StoreSource(store), scannedUntil);
            var watchedSince = state.WatchedSince[StoreSource(store)];
            var added = 0;
            var complete = true;
            foreach (var(order, line, key)in lines)
            {
                // Return restocking follows an approved claim, rather than an unreviewed order status.
                if (order.Status.Equals("Returned", StringComparison.OrdinalIgnoreCase))
                    continue;
                var desired = NotSold.Contains(order.Status) ? 0 : line.Quantity;
                // Trendyol scans by last update, so an order placed before watching started can show up later (e.g. on delivery).
                // It is already in the stock application's numbers: record it without counting it as a new sale.
                var known = state.AppliedQuantities.ContainsKey(key) || state.SeenOrderLines.Contains(key);
                if (!known && order.OrderDate is { } placed && placed < watchedSince)
                {
                    state.SeenOrderLines.Add(key);
                    state.AppliedQuantities[key] = desired;
                    state.UnmatchedSales.Remove(key);
                    continue;
                }
                var previous = state.AppliedQuantities.GetValueOrDefault(key, state.SeenOrderLines.Contains(key) ? desired : 0);
                var delta = desired - previous;
                if (delta == 0)
                {
                    state.UnmatchedSales.Remove(key);
                    continue;
                }

                var item = Find(line.Sku, line.Barcode);
                if (item is null)
                {
                    complete = false;
                    if (!state.UnmatchedSales.ContainsKey(key))
                        Event("warning", $"{store.Settings.Name} · {order.OrderNumber}: {line.Sku}/{line.Barcode} eşleşmedi; satış tekrar denenecek.");
                    state.UnmatchedSales[key] = line.Sku + "/" + line.Barcode;
                    continue;
                }

                var version = state.AdjustmentVersions.GetValueOrDefault(key) + 1;
                var id = "sale-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key + "|" + version)))[..24].ToLowerInvariant();
                AddSale(store, item, order.OrderNumber, delta, id);
                state.AppliedQuantities[key] = desired;
                state.AdjustmentVersions[key] = version;
                state.SeenOrderLines.Add(key);
                state.UnmatchedSales.Remove(key);
                added++;
            }

            if (snapshot is not null)
            {
                foreach (var claim in snapshot.Claims.Where(c => c.Status == "Onaylandı"))
                {
                    var key = store.Id + "|" + claim.Id;
                    if (state.ApprovedReturns.Contains(key))
                        continue;
                    var item = Find("", claim.Barcode);
                    if (item is null)
                    {
                        complete = false;
                        state.UnmatchedSales[key] = claim.Barcode;
                        continue;
                    }

                    AddSale(store, item, claim.OrderNumber, -claim.Quantity, "return-" + key);
                    state.ApprovedReturns.Add(key);
                    state.UnmatchedSales.Remove(key);
                    added++;
                }
            }

            if (complete)
                state.ScannedThrough[StoreSource(store)] = scannedUntil;
            SetStore(store.Id, complete ? (store.Settings.Mode == "demo" ? "demo" : "connected") : "error", complete ? $"{added} stok hareketi işlendi." : "Eşleşmeyen satışlar var; stok dağıtımı bekliyor.", save: false);
            Save();
            return complete;
        }
    }

    private void AddSale(ConnectionState store, CentralStockItem item, string order, int quantity, string id)
    {
        item.Quantity -= quantity;
        state.Sales.Insert(0, new() { Id = id, StoreId = store.Id, StoreName = store.Settings.Name, Marketplace = store.Settings.Marketplace, OrderNumber = order, Sku = item.Sku, Barcode = item.Barcode, Quantity = quantity });
        Event(item.Quantity < 0 ? "error" : "info", $"{store.Settings.Name} · {order} · {item.Sku}: {(quantity < 0 ? "Stok iadesi" : "Satış")} {Math.Abs(quantity)} → merkez {item.Quantity}.");
    }

    private async Task ReportSalesAsync(IServiceProvider sp, CancellationToken ct)
    {
        CentralSale[] pending;
        lock (gate)
            pending = state.Sales.Where(s => !s.Reported).Reverse().ToArray();
        if (pending.Length == 0)
            return;
        if (RealSource && !CentralStockSource.WritesAllowed(configuration))
        {
            lock (gate)
            {
                foreach (var s in pending)
                    s.ReportError = $"Satış bildirimi kapalı ({CentralStockSource.Section}:AllowWrites). Kaynak güncellenene kadar yerelde düşülmeye devam eder.";
                Save();
            }

            return;
        }

        var source = sp.GetRequiredService<CentralStockSource>();
        foreach (var sale in pending)
        {
            try
            {
                if (RealSource)
                    await source.ReportSaleAsync(sale, ct);
                lock (gate)
                {
                    if (!RealSource)
                        state.DemoSource[sale.Sku] = state.DemoSource.GetValueOrDefault(sale.Sku) - sale.Quantity;
                    sale.Reported = true;
                    sale.ReportError = null;
                    Save();
                }
            }
            catch (Exception e)when (e is IntegrationException or HttpRequestException or OperationCanceledException)
            {
                lock (gate)
                {
                    sale.ReportError = e is IntegrationException ie ? ie.Message : "Stok uygulamasına ulaşılamadı; sonraki çalıştırmada tekrar denenecek.";
                    Save();
                }

                return; // keep order: later sales wait for the earlier one
            }
        }

        lock (gate)
        {
            var keep = state.Sales.Where(s => !s.Reported).Concat(state.Sales.Where(s => s.Reported).Take(500)).ToHashSet();
            state.Sales = state.Sales.Where(keep.Contains).ToList();
            Save();
        }
    }

    private async Task DistributeAsync(IServiceProvider sp, IntegrationRegistry registry, DemoWorkspace demo, ConnectionState store, CancellationToken ct)
    {
        var queue = sp.GetRequiredService<StockOutbox>();
        var safety = Settings.SafetyStock;
        var queued = 0;
        var skipped = 0;
        if (store.Settings.Mode == "demo")
        {
            var snapshot = demo.Snapshot(store.Settings);
            foreach (var product in snapshot.Products)
            {
                int target;
                lock (gate)
                {
                    var item = Find(product.Sku, product.Barcode);
                    if (item is null)
                        continue;
                    target = Math.Max(0, item.Quantity - safety);
                }

                if (product.Stock == target && !queue.HasDifferentPending(store.Id, product.Barcode, target) || snapshot.Jobs.Any(j => j.State == "pending" && j.Kind == "stock" && j.Reference == product.Barcode))
                    continue;
                if (StockOutbox.StockRestriction(store, product.Barcode)is not null || product.SalePrice is not > 0)
                {
                    skipped++;
                    continue;
                }

                if (!TryEnqueue(() => queue.Enqueue(store, product.Barcode, target, product.SalePrice, "Merkezi stok otomasyonu"), store, product.Barcode, ref skipped, out var full))
                {
                    if (full) break;
                    continue;
                }
                queued++;
            }

            SetStore(store.Id, "demo", $"{queued} kanal güncellemesi kalıcı gönderim kuyruğunda; {skipped} kanal tarafından yönetilen/geçersiz ürün atlandı.");
            return;
        }

        // Queuing writes that the outbox will only block again churns its history; report the store as read-only instead.
        if (!RealWritesAllowed(store))
        {
            SetStore(store.Id, "readonly", "Bu mağazada gerçek stok gönderimi kapalı (Synchronization:AllowRealWrites / hesap profili AllowWrites); stok kanala gönderilmedi.");
            return;
        }

        KeyValuePair<string, PendingStockPush>[] previous;
        List<(string Barcode, string Sku, int Target)> targets;
        lock (gate)
        {
            previous = state.PendingPushes.Where(p => p.Key.StartsWith(store.Id + "|", StringComparison.Ordinal)).ToArray();
            targets = state.Items.Values.Where(i => i.Barcode.Length > 0).Select(i => (i.Barcode, i.Sku, Math.Max(0, i.Quantity - safety))).ToList();
        }

        // Recover accepted requests from older versions as verification-only queue entries.
        foreach (var entry in previous)
        {
            queue.Enqueue(store, entry.Key[(store.Id.Length + 1)..], entry.Value.Quantity, null, "Önceki sürüm gönderimini doğrula", entry.Value.RequestedAt);
            lock (gate)
            {
                state.PendingPushes.Remove(entry.Key);
                Save();
            }
        }

        // Prefer explicit catalogue mappings when the channel uses a different seller SKU.
        var mappings = sp.GetService<DemoStore>()?.CatalogRead(db => db.Catalog.Listings.Values.Where(l => l.StoreId == store.Id && l.Source == MarketPilot.Web.Catalog.CatalogHub.Source(store)).Select(l => new { Listing = l, Master = db.Products.FirstOrDefault(p => p.Id == l.ProductId) }).Where(x => x.Master != null).ToArray()) ?? [];
        foreach (var target in targets)
        {
            if (queue.ConfirmedStock(store.Id, target.Barcode) == target.Target && !queue.HasDifferentPending(store.Id, target.Barcode, target.Target))
                continue;
            var linked = mappings.Where(x => x.Master!.Barcode == target.Barcode || target.Sku.Length > 0 && x.Master!.Sku.Equals(target.Sku, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (linked.Length > 1)
            {
                skipped++;
                continue;
            }

            var nativeSku = linked.SingleOrDefault()?.Listing.Sku;
            if (!TryEnqueue(() => queue.Enqueue(store, target.Barcode, target.Target, null, "Merkezi stok otomasyonu", nativeSku: !string.IsNullOrWhiteSpace(nativeSku) ? nativeSku : target.Sku.Length > 0 ? target.Sku : null), store, target.Barcode, ref skipped, out var full))
            {
                if (full) break;
                continue;
            }
            queued++;
        }

        SetStore(store.Id, "pending", $"{queued} stok hedefi kalıcı kuyrukta; {skipped} belirsiz eşleştirme atlandı. Gönderim ve mağaza doğrulaması arka planda yapılacak.");
        await Task.CompletedTask;
    }

    public async Task<MarketplaceOrder> DemoSaleAsync(DemoSaleRequest request, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IntegrationRegistry>();
        if (RealSource)
            throw new ArgumentException("Gerçek stok kaynağı seçiliyken demo satış oluşturulamaz.");
        if (!await run.WaitAsync(0, ct))
            throw new ArgumentException("Senkronizasyon sürüyor. Satışı biraz sonra tekrar dene.");
        MarketplaceOrder order;
        try
        {
            if (!registry.Connections.TryGetValue(request.ConnectionId ?? "", out var store) || store.Settings.Mode != "demo")
                throw new ArgumentException("Demo mağaza bulunamadı.");
            // Establish the baseline before the first sale; otherwise that sale would be mistaken for history.
            if (!state.WatchedStores.Contains(store.Id))
                throw new ArgumentException("Önce merkezi stok senkronizasyonunu çalıştır.");
            order = scope.ServiceProvider.GetRequiredService<DemoWorkspace>().SimulateSale(store.Settings, request.Barcode ?? "", request.Quantity);
        }
        finally
        {
            run.Release();
        }

        await RunAsync(ct);
        return order;
    }

    public object View()
    {
        using var scope = services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IntegrationRegistry>();
        var demo = scope.ServiceProvider.GetRequiredService<DemoWorkspace>();
        var stores = registry.Connections.Values.Where(s => s.Settings.Mode == sourceMode).OrderBy(s => s.Settings.Marketplace).ThenBy(s => s.Settings.Name).ToArray();
        var demoProducts = stores.Where(s => s.Settings.Mode == "demo").ToDictionary(s => s.Id, s => demo.Snapshot(s.Settings).Products);
        lock (gate)
        {
            var safety = state.Settings.SafetyStock;
            return new
            {
                source = new
                {
                    mode = RealSource ? "real" : "demo",
                    host = CentralStockSource.BaseUri(configuration)?.Host,
                    writes = CentralStockSource.WritesAllowed(configuration)
                },
                settings = state.Settings,
                state.LastRunAt,
                state.LastSourceAt,
                state.LastAttemptAt,
                state.LastRunStatus,
                state.LastError,
                unmatchedSales = state.UnmatchedSales.Count,
                pendingPushes = state.PendingPushes.Count + services.GetRequiredService<StockOutbox>().PendingCount(sourceMode),
                pendingJobs = services.GetRequiredService<StockOutbox>().PendingCount(sourceMode) + (demoProducts.Count > 0 ? stores.Sum(s => demo.Snapshot(s.Settings).Jobs.Count(j => j.State == "pending")) : 0),
                excludedStores = registry.Connections.Count - stores.Length,
                running = run.CurrentCount == 0,
                stores = stores.Select(s => new { id = s.Id, name = s.Settings.Name, marketplace = s.Settings.Marketplace, mode = s.Settings.Mode, status = state.Stores.GetValueOrDefault(s.Id) }).ToArray(),
                items = state.Items.Values.OrderBy(i => i.Sku).Select(i => new { i.Sku, i.Barcode, i.Name, quantity = i.Quantity, available = Math.Max(0, i.Quantity - safety), unreported = state.Sales.Where(x => !x.Reported && Key(x.Sku, x.Barcode) == Key(i.Sku, i.Barcode)).Sum(x => x.Quantity), channels = stores.Select(s => new { storeId = s.Id, stock = demoProducts.TryGetValue(s.Id, out var products) ? products.FirstOrDefault(p => Matches(i, p.Sku, p.Barcode))?.Stock : services.GetRequiredService<StockOutbox>().ConfirmedStock(s.Id, i.Barcode) ?? (state.Pushed.TryGetValue($"{s.Id}|{i.Barcode}", out var pushed) ? pushed : (int? )null) }).ToArray() }).ToArray(),
                sales = state.Sales.Take(30).Select(s => new { s.Id, s.StoreId, s.StoreName, s.Marketplace, s.OrderNumber, s.Sku, s.Barcode, s.Quantity, s.At, s.Reported, s.ReportError }).ToArray(),
                events = state.Events.Take(40).ToArray()
            };
        }
    }

    private bool RealWritesAllowed(ConnectionState store)
    {
        try { return configuration.GetValue<bool>("Synchronization:AllowRealWrites") && CredentialProfiles.Writes(CredentialProfiles.For(configuration, store.Settings), store.Settings); }
        catch (Exception e) when (e is IntegrationException or InvalidOperationException) { return false; }
    }

    // One invalid item (e.g. a quantity the outbox refuses) must not stop distribution of every other item and store.
    private bool TryEnqueue(Action enqueue, ConnectionState store, string barcode, ref int skipped, out bool full)
    {
        full = false;
        try { enqueue(); return true; }
        catch (Exception e) when (e is ArgumentException or IntegrationException)
        {
            skipped++;
            full = e is IntegrationException { Code: "outbox_full" };
            lock (gate) Event("warning", $"{store.Settings.Name} · {barcode}: stok gönderimi kuyruğa alınamadı ({e.Message}).");
            return false;
        }
    }

    // Stock held by the marketplace itself (Amazon FBA, n11depom) is not in the stock application, so its sales are not deducted.
    private static bool ChannelFulfilled(ConnectionState store, MarketplaceOrder order, MarketplaceOrderLine line) => store.Settings.Marketplace switch
    {
        "amazon" => store.Settings.Mode == "demo" ? Amazon.AmazonDemo.IsAmazonFulfilled(line.Barcode) : string.Equals(order.FulfilledBy, "AMAZON", StringComparison.OrdinalIgnoreCase),
        "n11" => store.Settings.Mode == "demo" ? N11.N11Demo.Depot(line.Barcode) : string.Equals(order.FulfilledBy, "N11", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private CentralStockItem? Find(string sku, string barcode)
    {
        var rows = state.Items.Values.Where(i => sku.Length > 0 ? i.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase) : barcode.Length > 0 && i.Barcode == barcode).Take(2).ToArray();
        if (rows.Length == 0 && barcode.Length > 0)
            rows = state.Items.Values.Where(i => i.Barcode == barcode).Take(2).ToArray();
        return rows.Length == 1 && (barcode.Length == 0 || rows[0].Barcode.Length == 0 || rows[0].Barcode == barcode) ? rows[0] : null;
    }

    private static bool Matches(CentralStockItem item, string sku, string barcode) => item.Sku.Length > 0 && item.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase) || item.Barcode.Length > 0 && item.Barcode == barcode;
    private static string Key(string sku, string barcode) => sku.Length > 0 ? "sku:" + sku.ToUpperInvariant() : "barcode:" + barcode;
    private void SetStore(string id, string status, string message, bool save = true)
    {
        lock (gate)
        {
            state.Stores[id] = new(status, message, DateTimeOffset.UtcNow);
            if (save)
                Save();
        }
    }

    private void Event(string level, string message)
    {
        state.Events.Insert(0, new(DateTimeOffset.UtcNow, level, message));
        if (state.Events.Count > 200)
            state.Events.RemoveRange(200, state.Events.Count - 200);
    }

    private void Save()
    {
        try
        {
            var candidate = JsonSerializer.Serialize(state, json);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, candidate);
            for (var attempt = 0;; attempt++)
            {
                try
                {
                    File.Move(tmp, file, true);
                    break;
                }
                catch (Exception moveError) when (attempt < 4 && (moveError is IOException or UnauthorizedAccessException))
                {
                    Thread.Sleep(10 * (1 << attempt));
                }
            }

            persistedSnapshot = candidate;
            Interlocked.Increment(ref revision);
        }
        catch
        {
            state = JsonSerializer.Deserialize<CentralStockState>(persistedSnapshot, json)!;
            throw;
        }
    }

    // Same SKUs/barcodes as the demo stores, so the sample stock application matches them out of the box.
    private static readonly (string Sku, string Barcode, string Name, int Quantity)[] DemoCatalog = [("TSH-EKR-S", "8691000000001", "Pamuklu Oversize Tişört · Ekru / S", 40), ("TSH-EKR-M", "8691000000002", "Pamuklu Oversize Tişört · Ekru / M", 22), ("TSH-SYH-M", "8691000000003", "Pamuklu Oversize Tişört · Siyah / M", 3), ("GML-TS-L", "8691000000004", "Keten Gömlek · Taş / L", 15), ("PNT-BEJ-38", "8691000000005", "Dokuma Pantolon · Bej / 38", 9), ("SWT-GRI-M", "8691000000006", "Fermuarlı Sweatshirt · Gri / M", 30), ("HRK-KHV-ST", "8691000000007", "Triko Hırka · Kahve / Standart", 10), ("TSH-LCV-L", "8691000000008", "Çizgili Tişört · Lacivert / L", 18), ];
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
                try
                {
                    if (service.Due)
                        await service.RunAsync(stoppingToken);
                }
                // An unhandled exception here would stop the whole host; only shutdown cancellation may leave the loop.
                catch (Exception e)when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Central stock run failed: {Type}", e.GetType().Name);
                }
        }
        catch (OperationCanceledException)when (stoppingToken.IsCancellationRequested)
        {
        }
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
        group.MapPost("/run", async (CentralStockService service, CancellationToken ct) => await service.RunAsync(ct) ? Results.Ok(service.View()) : Results.Json(new { error = "Merkezi stok senkronizasyonu zaten çalışıyor." }, statusCode: 409));
        group.MapPost("/settings", (CentralStockSettings settings, CentralStockService service, OrderSynchronization automation) =>
        {
            if (settings.AutoSync && automation.Settings.Enabled)
                return Results.Conflict(new { error = "Yeni otomatik senkronizasyon açık; aynı anda ikinci zamanlayıcı açılamaz." });
            try
            {
                service.UpdateSettings(settings);
                return Results.Ok(service.View());
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });
        group.MapPost("/demo-sale", async (DemoSaleRequest request, CentralStockService service, CancellationToken ct) =>
        {
            try
            {
                var order = await service.DemoSaleAsync(request, ct);
                return Results.Ok(new { order = order.OrderNumber, view = service.View() });
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });
    }
}
