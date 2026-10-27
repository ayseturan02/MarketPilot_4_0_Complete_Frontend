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

    public CentralStockService(IWebHostEnvironment env, IServiceProvider services, IConfiguration configuration)
    {
        this.services = services; this.configuration = configuration;
        var folder = Path.Combine(env.ContentRootPath, "Data"); Directory.CreateDirectory(folder); file = Path.Combine(folder, "central-stock.json");
        state = File.Exists(file) ? JsonSerializer.Deserialize<CentralStockState>(File.ReadAllText(file), json) ?? throw new InvalidOperationException("Central stock file cannot be read.") : new();
        if (state.DemoSource.Count == 0) state.DemoSource = DemoSourceSeed();
    }

    public bool RealSource => CentralStockSource.IsConfigured(configuration);
    public CentralStockSettings Settings { get { lock (gate) return state.Settings; } }
    public bool Due { get { lock (gate) return state.Settings.AutoSync && (state.LastRunAt is null || DateTimeOffset.UtcNow - state.LastRunAt >= TimeSpan.FromSeconds(state.Settings.IntervalSeconds)); } }

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
            using var scope = services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IntegrationRegistry>();
            var demo = scope.ServiceProvider.GetRequiredService<DemoWorkspace>();
            var stores = registry.Connections.Values.OrderBy(s => s.Settings.Marketplace).ThenBy(s => s.Settings.Name).ToArray();
            if (!await PullSourceAsync(scope.ServiceProvider, ct)) return true;
            foreach (var store in stores) await CollectSalesAsync(scope.ServiceProvider, registry, demo, store, ct);
            await ReportSalesAsync(scope.ServiceProvider, ct);
            foreach (var store in stores) await DistributeAsync(scope.ServiceProvider, registry, demo, store, ct);
            lock (gate) { state.LastRunAt = DateTimeOffset.UtcNow; Save(); }
            return true;
        }
        finally { run.Release(); }
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
                // Without a fresh source the last known numbers are kept, but nothing is distributed from an empty catalog.
                var usable = state.Items.Count > 0; Event("error", state.LastError + (usable ? " Son bilinen stokla devam edildi." : "")); Save(); return usable;
            }
        }
        lock (gate)
        {
            var unreported = state.Sales.Where(s => !s.Reported).GroupBy(s => Key(s.Sku, s.Barcode)).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
            var items = new Dictionary<string, CentralStockItem>();
            foreach (var row in rows)
            {
                var key = Key(row.Sku, row.Barcode);
                items[key] = new() { Sku = row.Sku, Barcode = row.Barcode, Name = row.Name, Quantity = row.Quantity - unreported.GetValueOrDefault(key) };
            }
            state.Items = items; state.LastSourceAt = DateTimeOffset.UtcNow; state.LastError = null; Save();
        }
        return true;
    }

    private async Task CollectSalesAsync(IServiceProvider sp, IntegrationRegistry registry, DemoWorkspace demo, ConnectionState store, CancellationToken ct)
    {
        IReadOnlyList<MarketplaceOrder> orders;
        try
        {
            if (store.Settings.Mode == "demo") orders = demo.Snapshot(store.Settings).Orders;
            else
            {
                var adapter = sp.GetServices<IMarketplaceAdapter>().FirstOrDefault(a => a.Marketplace == store.Settings.Marketplace);
                if (adapter is null) return;
                var gateway = registry.Gates.GetOrAdd(store.Id, _ => new(1, 1));
                if (!await gateway.WaitAsync(0, ct)) { SetStore(store.Id, "busy", "Mağazada başka bir işlem sürdüğü için satışlar bu turda okunmadı."); return; }
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    orders = (await adapter.OrdersAsync(store.Settings, new(Size: 50, StartDate: now.AddDays(-2).ToUnixTimeMilliseconds(), EndDate: now.ToUnixTimeMilliseconds()), ct)).Items;
                }
                finally { gateway.Release(); }
            }
        }
        catch (Exception e) when (e is IntegrationException or HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            SetStore(store.Id, "error", "Siparişler okunamadı: " + (e is IntegrationException ie ? ie.Message : "bağlantıyı kontrol edin.")); return;
        }
        lock (gate)
        {
            var lines = orders.Where(o => !NotSold.Contains(o.Status)).SelectMany(o => o.Lines.Select(l => (Order: o, Line: l, Key: $"{store.Id}|{(o.PackageId.Length > 0 ? o.PackageId : o.OrderNumber)}|{l.Id}|{l.Barcode}"))).ToArray();
            if (state.WatchedStores.Add(store.Id))
            {
                // Orders that existed before the store was watched are already included in the stock application's numbers.
                foreach (var x in lines) state.SeenOrderLines.Add(x.Key);
                Event("info", $"{store.Settings.Name} izlemeye alındı; mevcut {lines.Length} sipariş satırı geçmiş kabul edildi."); Save(); return;
            }
            var added = 0;
            foreach (var (order, line, key) in lines)
            {
                if (!state.SeenOrderLines.Add(key)) continue;
                var item = Find(line.Sku, line.Barcode);
                if (item is null) { Event("warning", $"{store.Settings.Name} · {order.OrderNumber}: {line.Sku}/{line.Barcode} stok uygulamasında eşleşmedi."); continue; }
                item.Quantity -= line.Quantity; added++;
                state.Sales.Insert(0, new() { Id = "sale-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant(), StoreId = store.Id, StoreName = store.Settings.Name, Marketplace = store.Settings.Marketplace, OrderNumber = order.OrderNumber, Sku = item.Sku, Barcode = item.Barcode, Quantity = line.Quantity });
                Event(item.Quantity < 0 ? "error" : "info", $"{store.Settings.Name} satışı: {order.OrderNumber} · {item.Sku} × {line.Quantity} → merkezi stok {item.Quantity}{(item.Quantity < 0 ? " (fazla satış!)" : "")}.");
            }
            SetStore(store.Id, store.Settings.Mode == "demo" ? "demo" : "connected", added > 0 ? $"{added} yeni satış satırı işlendi." : "Yeni satış yok.", save: false);
            Save();
        }
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
        lock (gate) changes = state.Items.Values.Where(i => i.Barcode.Length > 0).Select(i => (i.Barcode, Target: Math.Max(0, i.Quantity - safety))).Where(x => state.Pushed.GetValueOrDefault($"{store.Id}|{x.Barcode}", -1) != x.Target).ToList();
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
                lock (gate) { foreach (var x in batch) state.Pushed[$"{store.Id}|{x.Barcode}"] = x.Target; Event("info", $"{store.Settings.Name}: {batch.Length} ürünün stoğu Trendyol'a gönderildi (HTTP {result.Status})."); Save(); }
            }
            SetStore(store.Id, "connected", $"{changes.Count} ürünün stoğu gönderildi.");
        }
        catch (Exception e) when (e is IntegrationException or HttpRequestException or OperationCanceledException or JsonException)
        {
            SetStore(store.Id, "error", "Stok gönderilemedi: " + (e is IntegrationException ie ? ie.Message : "bağlantıyı kontrol edin."));
        }
        finally { gateway.Release(); }
    }

    public async Task<MarketplaceOrder> DemoSaleAsync(DemoSaleRequest request, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IntegrationRegistry>();
        if (!registry.Connections.TryGetValue(request.ConnectionId ?? "", out var store) || store.Settings.Mode != "demo") throw new ArgumentException("Demo mağaza bulunamadı.");
        var order = scope.ServiceProvider.GetRequiredService<DemoWorkspace>().SimulateSale(store.Settings, request.Barcode ?? "", request.Quantity);
        await RunAsync(ct);
        return order;
    }

    public object View()
    {
        using var scope = services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IntegrationRegistry>(); var demo = scope.ServiceProvider.GetRequiredService<DemoWorkspace>();
        var stores = registry.Connections.Values.OrderBy(s => s.Settings.Marketplace).ThenBy(s => s.Settings.Name).ToArray();
        var demoProducts = stores.Where(s => s.Settings.Mode == "demo").ToDictionary(s => s.Id, s => demo.Snapshot(s.Settings).Products);
        lock (gate)
        {
            var safety = state.Settings.SafetyStock;
            return new
            {
                source = new { mode = RealSource ? "real" : "demo", host = CentralStockSource.BaseUri(configuration)?.Host, writes = CentralStockSource.WritesAllowed(configuration) },
                settings = state.Settings, state.LastRunAt, state.LastSourceAt, state.LastError, running = run.CurrentCount == 0,
                stores = stores.Select(s => new { id = s.Id, name = s.Settings.Name, marketplace = s.Settings.Marketplace, mode = s.Settings.Mode, status = state.Stores.GetValueOrDefault(s.Id) }),
                items = state.Items.Values.OrderBy(i => i.Sku).Select(i => new
                {
                    i.Sku, i.Barcode, i.Name, quantity = i.Quantity, available = Math.Max(0, i.Quantity - safety),
                    unreported = state.Sales.Where(x => !x.Reported && Key(x.Sku, x.Barcode) == Key(i.Sku, i.Barcode)).Sum(x => x.Quantity),
                    channels = stores.Select(s => new
                    {
                        storeId = s.Id,
                        stock = demoProducts.TryGetValue(s.Id, out var products) ? products.FirstOrDefault(p => Matches(i, p.Sku, p.Barcode))?.Stock
                            : state.Pushed.TryGetValue($"{s.Id}|{i.Barcode}", out var pushed) ? pushed : (int?)null
                    })
                }),
                sales = state.Sales.Take(30), events = state.Events.Take(40)
            };
        }
    }

    private CentralStockItem? Find(string sku, string barcode) => state.Items.Values.FirstOrDefault(i => Matches(i, sku, barcode));
    private static bool Matches(CentralStockItem item, string sku, string barcode) => item.Sku.Length > 0 && item.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase) || item.Barcode.Length > 0 && item.Barcode == barcode;
    private static string Key(string sku, string barcode) => sku.Length > 0 ? "sku:" + sku.ToUpperInvariant() : "barcode:" + barcode;
    private void SetStore(string id, string status, string message, bool save = true) { lock (gate) { state.Stores[id] = new(status, message, DateTimeOffset.UtcNow); if (save) Save(); } }
    private void Event(string level, string message) { state.Events.Insert(0, new(DateTimeOffset.UtcNow, level, message)); if (state.Events.Count > 200) state.Events.RemoveRange(200, state.Events.Count - 200); }
    private void Save() { var tmp = file + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(state, json)); File.Move(tmp, file, true); }

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
