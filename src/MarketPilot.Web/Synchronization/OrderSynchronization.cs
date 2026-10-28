using System.Text.Json;
using MarketPilot.Web.Catalog;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.CentralStock;
using MarketPilot.Web.Integrations.Demo;

namespace MarketPilot.Web.Synchronization;

public sealed record AutomationSettings(bool Enabled = false, int IntervalSeconds = 60, bool StockEnabled = true, int LookbackDays = 2);
public sealed class StorePullState
{
    public string StoreId { get; set; } = "";
    public string StoreName { get; set; } = "";
    public string Source { get; set; } = "";
    public string State { get; set; } = "idle";
    public DateTimeOffset NextRunAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int LastCount { get; set; }
    public string? Error { get; set; }
}
public sealed class AutomationState
{
    public AutomationSettings Settings { get; set; } = new();
    public Dictionary<string, StorePullState> Stores { get; set; } = new();
    public DateTimeOffset NextStockRunAt { get; set; }
    public DateTimeOffset? LastWorkerTick { get; set; }
    public string? StockError { get; set; }
}
public sealed class OrderSynchronization
{
    private readonly object gate = new();
    private readonly SemaphoreSlim running = new(1, 1);
    private readonly string file;
    private readonly IServiceProvider services;
    private readonly IConfiguration config;
    private readonly TimeProvider clock;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private AutomationState state;
    private string persisted;
    private long revision;
    public long Revision => Interlocked.Read(ref revision);
    public OrderSynchronization(IWebHostEnvironment env, IServiceProvider services, IConfiguration config, TimeProvider clock)
    {
        this.services = services; this.config = config; this.clock = clock;
        var folder = Path.Combine(env.ContentRootPath, "Data"); Directory.CreateDirectory(folder);
        file = Path.Combine(folder, "synchronization.json");
        state = File.Exists(file) ? JsonSerializer.Deserialize<AutomationState>(File.ReadAllText(file), json) ?? throw new InvalidOperationException("Synchronization state cannot be read.") : new();
        foreach (var item in state.Stores.Values.Where(s => s.State == "running")) { item.State = "retry"; item.NextRunAt = clock.GetUtcNow(); }
        persisted = JsonSerializer.Serialize(state, json);
    }
    private void Save()
    {
        try { var candidate = JsonSerializer.Serialize(state, json); File.WriteAllText(file + ".tmp", candidate); for (var commitAttempt = 0; ; commitAttempt++)
        {
            try { File.Move(file + ".tmp", file, true); break; }
            catch (Exception moveError) when (commitAttempt < 4 && (moveError is IOException or UnauthorizedAccessException)) { Thread.Sleep(10 * (1 << commitAttempt)); }
        } persisted = candidate; Interlocked.Increment(ref revision); }
        catch { state = JsonSerializer.Deserialize<AutomationState>(persisted, json)!; throw; }
    }
    public AutomationSettings Settings { get { lock (gate) return state.Settings; } }
    public void Configure(AutomationSettings settings)
    {
        if (settings.IntervalSeconds is < 30 or > 3600 || settings.LookbackDays is < 1 or > 14) throw new ArgumentException("Aralık 30–3600 saniye, sipariş taraması 1–14 gün olmalı.");
        lock (gate)
        {
            if (!state.Settings.Enabled && settings.Enabled) { foreach (var store in state.Stores.Values) store.NextRunAt = clock.GetUtcNow(); state.NextStockRunAt = clock.GetUtcNow(); }
            state.Settings = settings; Save();
        }
    }
    public object Status()
    {
        lock (gate) return new { settings = state.Settings, state.LastWorkerTick, state.NextStockRunAt, state.StockError, running = running.CurrentCount == 0,
            realStoresEnabled = config.GetValue<bool>("Synchronization:IncludeRealStores"),
            stores = state.Stores.Values.Select(s => new { s.StoreId, s.StoreName, s.State, s.NextRunAt, s.LastAttemptAt, s.LastSuccessAt, s.LastCount, s.ConsecutiveFailures, s.Error }).ToArray() };
    }
    public async Task Tick(CancellationToken ct)
    {
        if (!await running.WaitAsync(0, ct)) return;
        try
        {
            var settings = Settings;
            if (!settings.Enabled) return;
            using var scope = services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IntegrationRegistry>();
            var realEnabled = config.GetValue<bool>("Synchronization:IncludeRealStores");
            var stores = registry.Connections.Values.Where(s => s.Settings.Mode == "demo" || realEnabled)
                .OrderBy(s => s.Id).GroupBy(CatalogHub.Source).Select(g => g.First()).ToArray();
            foreach (var store in stores)
            {
                ct.ThrowIfCancellationRequested();
                var source = CatalogHub.Source(store);
                StorePullState pull;
                lock (gate)
                {
                    if (!state.Stores.TryGetValue(source, out pull!))
                        state.Stores[source] = pull = new() { StoreId = store.Id, StoreName = store.Settings.Name, Source = source, NextRunAt = clock.GetUtcNow() };
                    if (pull.NextRunAt > clock.GetUtcNow()) continue;
                }
                var storeGate = registry.Gates.GetOrAdd(store.Id, _ => new(1, 1));
                if (!await storeGate.WaitAsync(0, ct)) continue;
                try
                {
                    lock (gate) { pull.State = "running"; pull.LastAttemptAt = clock.GetUtcNow(); Save(); }
                    var until = clock.GetUtcNow();
                    IReadOnlyList<MarketplaceOrder> orders;
                    if (store.Settings.Mode == "demo") orders = scope.ServiceProvider.GetRequiredService<DemoWorkspace>().Snapshot(store.Settings).Orders;
                    else
                    {
                        var adapter = MarketplaceClients.Resolve(scope.ServiceProvider, store.Settings)
                            ?? throw new IntegrationException("adapter_missing", "Sipariş okuma adaptörü hazır değil.", 409);
                        var from = pull.LastSuccessAt?.AddMinutes(-5) ?? until.AddDays(-settings.LookbackDays);
                        // Keep a lookback window for late order updates as well as a durable recovery watermark.
                        if (from > until.AddDays(-settings.LookbackDays)) from = until.AddDays(-settings.LookbackDays);
                        orders = await MarketplaceOrderReader.ReadAllAsync(adapter, store.Settings, from, until, ct);
                        scope.ServiceProvider.GetRequiredService<OperationsHub>().Observe(store, new MarketplacePage<MarketplaceOrder>(orders, 0, orders.Count, orders.Count, 1, null));
                    }
                    lock (gate) { pull.State = "completed"; pull.LastSuccessAt = until; pull.LastCount = orders.Count; pull.ConsecutiveFailures = 0; pull.Error = null; pull.NextRunAt = clock.GetUtcNow().AddSeconds(settings.IntervalSeconds); Save(); }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch
                {
                    lock (gate) { pull.State = "retry"; pull.ConsecutiveFailures++; pull.Error = "Siparişler tamamen okunamadı; önceki kayıtlar korunarak tekrar denenecek.";
                        pull.NextRunAt = clock.GetUtcNow().AddSeconds(Math.Min(1800, settings.IntervalSeconds * Math.Pow(2, Math.Min(5, pull.ConsecutiveFailures - 1)))); Save(); }
                }
                finally { storeGate.Release(); }
            }
            bool stockDue;
            lock (gate) { state.LastWorkerTick = clock.GetUtcNow(); stockDue = settings.StockEnabled && state.NextStockRunAt <= clock.GetUtcNow(); if (stockDue) { state.NextStockRunAt = clock.GetUtcNow().AddSeconds(settings.IntervalSeconds); Save(); } }
            if (stockDue)
            {
                var stock = scope.ServiceProvider.GetRequiredService<CentralStockService>();
                try
                {
                    if (stock.RealSource && !realEnabled) { lock (gate) { state.StockError = "Gerçek kaynak otomasyonu henüz etkinleştirilmedi."; Save(); } }
                    else
                    {
                        var started = await stock.RunAsync(ct);
                        lock (gate) { state.StockError = !started ? "Başka bir stok taraması sürüyor; sonraki aralıkta denenecek." : stock.LastRunSucceeded ? null : "Stok dağıtımı durduruldu; merkezi stok uyarılarını kontrol et."; Save(); }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { lock (gate) { state.StockError = "Stok taraması tamamlanamadı; kalıcı kuyruk ve önceki stok korundu."; Save(); } }
            }
        }
        finally { running.Release(); }
    }
}
public sealed class OrderSynchronizationWorker(OrderSynchronization service, ILogger<OrderSynchronizationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try { do { try { await service.Tick(stoppingToken); } catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested) { log.LogWarning("Otomatik senkronizasyon çalıştırılamadı: {Type}", e.GetType().Name); } } while (await timer.WaitForNextTickAsync(stoppingToken)); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
