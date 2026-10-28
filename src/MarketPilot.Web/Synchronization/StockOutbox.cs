using System.Text.Json;
using MarketPilot.Web.Catalog;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Amazon;
using MarketPilot.Web.Integrations.N11;
using MarketPilot.Web.Integrations.Demo;
using MarketPilot.Web.Integrations.Trendyol;

namespace MarketPilot.Web.Synchronization;

public sealed class StockWriteJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string StoreId { get; set; } = "";
    public string AccountIdentity { get; set; } = "";
    public long Sequence { get; set; }
    public string StoreName { get; set; } = "";
    public string Source { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Barcode { get; set; } = "";
    public string? NativeSku { get; set; }
    public string? ResolvedBarcode { get; set; }
    public string? RequestId { get; set; }
    public string? RequestFingerprint { get; set; }
    public int Quantity { get; set; }
    public decimal? Price { get; set; }
    public string Reason { get; set; } = "";
    public string State { get; set; } = "queued";
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ProviderJobId { get; set; }
    public string? Error { get; set; }
}
public sealed class StockOutbox
{
    private readonly object gate = new();
    private readonly SemaphoreSlim processing = new(1, 1);
    private readonly string file;
    private readonly IntegrationRegistry registry;
    private readonly DemoWorkspace demo;
    private readonly IServiceProvider services;
    private readonly IConfiguration config;
    private readonly TimeProvider clock;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private List<StockWriteJob> jobs;
    private string persisted;
    private long revision;
    public long Revision => Interlocked.Read(ref revision);
    private static bool SameProduct(StockWriteJob job,string barcode,string? sku) =>
        job.Barcode==barcode || job.ResolvedBarcode==barcode || !string.IsNullOrEmpty(sku)&&job.NativeSku==sku;
    private static MarketplaceProduct? DemoProduct(IEnumerable<MarketplaceProduct> products,StockWriteJob job)
    {
        var rows=products.Where(p=>string.IsNullOrWhiteSpace(job.NativeSku)?p.Barcode==job.Barcode:p.Sku==job.NativeSku).Take(2).ToArray();
        return rows.Length==1 && (job.Barcode.StartsWith("sku:",StringComparison.Ordinal)||rows[0].Barcode==job.Barcode)?rows[0]:null;
    }
    private static bool Active(string state) => state is "queued" or "dispatching" or "waiting" or "uncertain" or "blocked";

    public StockOutbox(IWebHostEnvironment env, IntegrationRegistry registry, DemoWorkspace demo, IServiceProvider services, IConfiguration config, TimeProvider clock)
    {
        this.registry = registry; this.demo = demo; this.services = services; this.config = config; this.clock = clock;
        var folder = Path.Combine(env.ContentRootPath, "Data"); Directory.CreateDirectory(folder);
        file = Path.Combine(folder, "marketplace-outbox.json");
        jobs = File.Exists(file) ? JsonSerializer.Deserialize<List<StockWriteJob>>(File.ReadAllText(file), json) ?? throw new InvalidOperationException("Outbox cannot be read.") : new();
        long sequence = jobs.Select(j => j.Sequence).DefaultIfEmpty().Max();
        foreach (var item in jobs.Where(j => j.Sequence == 0).OrderBy(j => j.CreatedAt)) item.Sequence = ++sequence;
        // A real dispatch interrupted after sending must be verified, never blindly resent.
        foreach (var job in jobs.Where(j => j.State == "dispatching"))
        {
            job.State = job.Mode == "demo" ? "queued" : "uncertain";
            if (job.Mode == "real") job.SentAt ??= clock.GetUtcNow();
            job.NextAttemptAt = clock.GetUtcNow();
        }
        persisted = JsonSerializer.Serialize(jobs, json);
    }
    private void Save()
    {
        try { var candidate = JsonSerializer.Serialize(jobs, json); File.WriteAllText(file + ".tmp", candidate); for (var commitAttempt = 0; ; commitAttempt++)
        {
            try { File.Move(file + ".tmp", file, true); break; }
            catch (Exception moveError) when (commitAttempt < 4 && (moveError is IOException or UnauthorizedAccessException)) { Thread.Sleep(10 * (1 << commitAttempt)); }
        } persisted = candidate; Interlocked.Increment(ref revision); }
        catch { jobs = JsonSerializer.Deserialize<List<StockWriteJob>>(persisted, json)!; throw; }
    }
    private StockWriteJob Copy(StockWriteJob job) => JsonSerializer.Deserialize<StockWriteJob>(JsonSerializer.Serialize(job, json), json)!;
    public static string? StockRestriction(ConnectionState store, string barcode)
    {
        if (store.Settings.Mode != "demo") return null;
        if (store.Settings.Marketplace == "amazon" && AmazonDemo.IsAmazonFulfilled(barcode)) return "Amazon deposu tarafından yönetilen stok değiştirilemez.";
        if (store.Settings.Marketplace == "n11" && N11Demo.Depot(barcode)) return "n11depom tarafından yönetilen stok değiştirilemez.";
        return null;
    }
    private string AccountIdentity(ConnectionState store)
    {
        if (store.Settings.Mode == "demo") return "";
        return CredentialProfiles.Identity(config,store.Settings);
    }
    public StockWriteJob Enqueue(ConnectionState store, string barcode, int quantity, decimal? price, string reason, DateTimeOffset? verifyExisting = null, string? nativeSku = null, string? requestId = null)
    {
        if (string.IsNullOrWhiteSpace(barcode) || barcode.Length > 120 || quantity is < 0 or > 1000000 || price is <= 0 or > 10000000)
            throw new ArgumentException("Stok gönderimi alanları geçersiz.");
        if (StockRestriction(store, barcode) is { } restriction) throw new ArgumentException(restriction);
        var source = CatalogHub.Source(store);
        var account = AccountIdentity(store);
        var requestFingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{source,account,barcode,nativeSku,quantity,price=price?.ToString("G29",System.Globalization.CultureInfo.InvariantCulture)}))));
        lock (gate)
        {
            if(requestId is not null)
            {
                var repeated=jobs.FirstOrDefault(j=>j.StoreId==store.Id&&j.RequestId==requestId);
                if(repeated!=null)
                {
                    if(repeated.RequestFingerprint is not null ? repeated.RequestFingerprint!=requestFingerprint : repeated.Source!=source || repeated.AccountIdentity!=account || repeated.Barcode!=barcode || repeated.Quantity!=quantity || repeated.Price!=price)
                        throw new IntegrationException("idempotency_conflict","İşlem kimliği farklı bir istek için kullanılmış.",409);
                    return Copy(repeated);
                }
            }
            var existing = jobs.FirstOrDefault(j => j.StoreId == store.Id && j.Source == source && j.AccountIdentity == account && SameProduct(j,barcode,nativeSku) && (requestId is null || j.RequestId==requestId) && j.Quantity == quantity && j.Price == price && (Active(j.State) || j.State == "failed"));
            if (existing is not null) return Copy(existing);
            if (jobs.Count(j => Active(j.State)) >= 5000) throw new IntegrationException("outbox_full", "Stok gönderim kuyruğu dolu.", 409);
            foreach (var old in jobs.Where(j => j.StoreId == store.Id && SameProduct(j,barcode,nativeSku) && j.SentAt is null && j.State is "queued" or "blocked" or "failed"))
            { old.State = "superseded"; old.FinishedAt = clock.GetUtcNow(); }
            var job = new StockWriteJob { StoreId = store.Id, StoreName = store.Settings.Name, Source = source, Mode = store.Settings.Mode, AccountIdentity = account, Sequence = jobs.Select(j => j.Sequence).DefaultIfEmpty().Max() + 1,
                Barcode = barcode, NativeSku = nativeSku, RequestId = requestId, RequestFingerprint = requestFingerprint, Quantity = quantity, Price = price, Reason = reason, CreatedAt = clock.GetUtcNow(),
                NextAttemptAt = clock.GetUtcNow(), SentAt = verifyExisting, State = verifyExisting is null ? "queued" : "uncertain" };
            jobs.Insert(0, job);
            var terminal = jobs.Where(j => !Active(j.State)).OrderByDescending(j => j.CreatedAt).Skip(2000).ToHashSet();
            jobs.RemoveAll(terminal.Contains);
            Save(); return Copy(job);
        }
    }
    public StockWriteJob? Find(string id) { lock(gate) return jobs.FirstOrDefault(j=>j.Id==id) is {} job?Copy(job):null; }
    public object Status()
    {
        lock (gate) return new { queued = jobs.Count(j => j.State == "queued"), waiting = jobs.Count(j => j.State is "dispatching" or "waiting" or "uncertain"),
            blocked = jobs.Count(j => j.State == "blocked"), failed = jobs.Count(j => j.State == "failed"), completed = jobs.Count(j => j.State == "completed"),
            recent = jobs.Take(100).Select(Copy).ToArray() };
    }
    public int PendingCount(string? mode = null) { lock (gate) return jobs.Count(j => (mode is null || j.Mode == mode) && Active(j.State)); }
    public bool HasDifferentPending(string storeId, string barcode, int target)
    {
        if (!registry.Connections.TryGetValue(storeId, out var store)) return false;
        var account = AccountIdentity(store); var source = CatalogHub.Source(store);
        lock (gate) return jobs.Any(j => j.StoreId == storeId && j.Source == source && j.AccountIdentity == account && SameProduct(j,barcode,null) && j.Quantity != target && Active(j.State));
    }
    public int? ConfirmedStock(string storeId, string barcode)
    {
        if (!registry.Connections.TryGetValue(storeId, out var store)) return null;
        var account = AccountIdentity(store); var source = CatalogHub.Source(store);
        lock (gate) return jobs.Where(j => j.StoreId == storeId && j.Source == source && j.AccountIdentity == account && SameProduct(j,barcode,null) && j.State == "completed").OrderByDescending(j => j.FinishedAt).Select(j => (int?)j.Quantity).FirstOrDefault();
    }
    public void Retry(string id)
    {
        lock (gate)
        {
            var job = jobs.FirstOrDefault(j => j.Id == id) ?? throw new IntegrationException("not_found", "Gönderim işi bulunamadı.", 404);
            if (job.State is not ("failed" or "blocked")) throw new IntegrationException("invalid_state", "Yalnızca hatalı veya engellenmiş iş tekrar denenebilir.", 409);
            if (jobs.Any(j => j.Id != id && j.StoreId == job.StoreId && j.Source == job.Source && j.AccountIdentity == job.AccountIdentity && SameProduct(j,job.Barcode,job.NativeSku) && Active(j.State))) throw new IntegrationException("newer_job", "Bu ürünün başka bir gönderimi sürüyor.", 409);
            job.State = job.SentAt is null ? "queued" : "uncertain"; job.Attempts = 0; job.Error = null; job.FinishedAt = null; job.NextAttemptAt = clock.GetUtcNow(); Save();
        }
    }
    private void Set(string id, Action<StockWriteJob> update)
    {
        lock (gate) { var job = jobs.Single(j => j.Id == id); update(job); Save(); }
    }
    private bool Claim(string id)
    {
        lock (gate)
        {
            var job = jobs.Single(j => j.Id == id);
            if (job.State != "queued") return false;
            job.State = "dispatching"; job.Attempts++; job.Error = null; Save(); return true;
        }
    }
    private void Finish(string id, string state, string? error = null) => Set(id, j => { j.State = state; j.Error = error; j.FinishedAt = clock.GetUtcNow(); });
    public async Task ProcessDue(CancellationToken ct)
    {
        if (!await processing.WaitAsync(0, ct)) return;
        try
        {
            StockWriteJob[] due;
            lock (gate)
            {
                due = jobs.Where(j => j.State is "queued" or "waiting" or "uncertain" && j.NextAttemptAt <= clock.GetUtcNow())
                    .OrderBy(j => j.Sequence).Where(j => !jobs.Any(old => old.Id != j.Id && old.StoreId == j.StoreId && old.Source == j.Source && old.AccountIdentity == j.AccountIdentity && SameProduct(old,j.Barcode,j.NativeSku) && old.Sequence < j.Sequence && Active(old.State)))
                    .GroupBy(j => j.StoreId).Select(g => Copy(g.First())).Take(5).ToArray();
            }
            foreach (var job in due)
            {
                ct.ThrowIfCancellationRequested();
                if (!registry.Connections.TryGetValue(job.StoreId, out var store) || CatalogHub.Source(store) != job.Source || AccountIdentity(store) != job.AccountIdentity)
                { Finish(job.Id, "blocked", "Mağaza bağlantısı değişti; yanlış mağazaya gönderim yapılmadı."); continue; }
                var storeGate = registry.Gates.GetOrAdd(store.Id, _ => new(1, 1));
                if (!await storeGate.WaitAsync(0, ct)) continue;
                try { await ProcessOne(store, job, ct); }
                finally { storeGate.Release(); }
            }
        }
        finally { processing.Release(); }
    }
    private async Task ProcessOne(ConnectionState store, StockWriteJob job, CancellationToken ct)
    {
        if (store.Settings.Mode == "real" && !config.GetValue<bool>("Synchronization:AllowRealWrites"))
        { Finish(job.Id, "blocked", "Gerçek gönderim izni veya bu kanalın yazma adaptörü hazır değil."); return; }
        if (job.State is "waiting" or "uncertain")
        {
            if (store.Settings.Mode == "demo")
            {
                var snapshot = demo.Snapshot(store.Settings);
                var providerJob = snapshot.Jobs.FirstOrDefault(j => j.Id == job.ProviderJobId);
                if (providerJob?.State == "failed") { Finish(job.Id, "failed", providerJob.Error); return; }
                if (providerJob?.State == "completed")
                {
                    var product = DemoProduct(snapshot.Products,job);
                    Finish(job.Id, product?.Stock == job.Quantity && (!job.Price.HasValue || product.SalePrice == job.Price) ? "completed" : "superseded", ""); return;
                }
                if (providerJob is null) { Finish(job.Id, "failed", "Kalıcı demo işi bulunamadı; kontrol edilmeden yeniden gönderilmedi."); return; }
                Set(job.Id, j => j.NextAttemptAt = clock.GetUtcNow().AddSeconds(1)); return;
            }
            try
            {
                var product = await services.GetRequiredService<MarketplaceStockWriter>().Read(store.Settings,job.Barcode,job.NativeSku,ct);
                if (product?.Stock == job.Quantity && (!job.Price.HasValue || product.SalePrice == job.Price))
                { Finish(job.Id, "completed"); return; }
                if (clock.GetUtcNow() - job.SentAt > TimeSpan.FromMinutes(5))
                { Finish(job.Id, "failed", "Gönderilen stok doğrulanamadı. İşlem kontrol edilerek yeniden denenmeli."); return; }
                Set(job.Id, j => j.NextAttemptAt = clock.GetUtcNow().AddSeconds(15));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { if(clock.GetUtcNow()-job.SentAt>TimeSpan.FromMinutes(5))Finish(job.Id,"failed","Mağaza sonucu doğrulanamadı; işlem kontrol edilerek yeniden denenmeli.");else Set(job.Id, j => { j.Error = "Mağaza sonucu okunamadı; gönderim başarılı sayılmadı."; j.NextAttemptAt = clock.GetUtcNow().AddSeconds(30); }); }
            return;
        }
        MarketplaceProduct? liveProduct = null;
        if (store.Settings.Mode == "real")
        {
            try
            {
                var writer = services.GetRequiredService<MarketplaceStockWriter>(); writer.Check(store.Settings);
                liveProduct = await writer.Read(store.Settings,job.Barcode,job.NativeSku,ct);
                // Persist the exact native identity before any mutation, including recovery after a crash.
                Set(job.Id,j=>{j.NativeSku=liveProduct.Sku;j.ResolvedBarcode=liveProduct.Barcode;}); job.NativeSku=liveProduct.Sku;job.ResolvedBarcode=liveProduct.Barcode;
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(IntegrationException e)when(e.Status is 400 or 401 or 403 or 404 or 409){Finish(job.Id,"blocked",e.Message);return;}
            catch{Set(job.Id,j=>{j.Error="Gönderim öncesi mağaza kaydı okunamadı; veri değiştirilmedi.";j.NextAttemptAt=clock.GetUtcNow().AddSeconds(30);});return;}
        }
        if (!Claim(job.Id)) return;
        try
        {
            if (store.Settings.Mode == "demo")
            {
                var snapshot = demo.Snapshot(store.Settings);
                var product = DemoProduct(snapshot.Products,job) ?? throw new ArgumentException("Ürün mağazada bulunamadı.");
                if(StockRestriction(store,product.Barcode) is {} restriction)throw new ArgumentException(restriction);
                Set(job.Id,j=>{j.NativeSku=product.Sku;j.ResolvedBarcode=product.Barcode;});job.NativeSku=product.Sku;job.ResolvedBarcode=product.Barcode;
                var price = job.Price ?? product.SalePrice ?? throw new ArgumentException("Ürünün fiyatı bilinmiyor.");
                var sent = demo.Enqueue(store.Settings, new("stock", "outbox-" + job.Id, product.Barcode, job.Quantity, price));
                Set(job.Id, j => { j.ProviderJobId = sent.Id; j.State = "waiting"; j.SentAt = clock.GetUtcNow(); j.NextAttemptAt = clock.GetUtcNow().AddSeconds(1); });
            }
            else
            {
                var batchId = await services.GetRequiredService<MarketplaceStockWriter>().Send(store.Settings,liveProduct!,job.Quantity,job.Price,ct,job.AccountIdentity);
                Set(job.Id, j => { j.ProviderJobId = batchId; j.State = "waiting"; j.SentAt = clock.GetUtcNow(); j.NextAttemptAt = clock.GetUtcNow().AddSeconds(15); });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ArgumentException) { Finish(job.Id, "failed", "Ürün, stok veya fiyat geçersiz."); }
        catch (IntegrationException e) when (e.Status is 400 or 401 or 403 or 404 or 409)
        { Finish(job.Id, "blocked", e.Message); }
        catch (IntegrationException e) when (e.Status == 429)
        {
            Set(job.Id, j => { j.State = j.Attempts >= 6 ? "failed" : "queued"; j.Error = "İstek sınırı; artan beklemeyle tekrar denenecek."; j.NextAttemptAt = clock.GetUtcNow().AddSeconds(Math.Min(900, 10 * Math.Pow(2, j.Attempts))); });
        }
        catch
        {
            Set(job.Id, j => { j.State = store.Settings.Mode == "demo" ? (j.Attempts >= 6 ? "failed" : "queued") : "uncertain"; j.SentAt = store.Settings.Mode == "real" ? clock.GetUtcNow() : null;
                j.Error = store.Settings.Mode == "demo" ? "Geçici işlem/disk hatası; aynı kalıcı işlem kimliğiyle tekrar denenecek." : "İşlem sonucu belirsiz; aynı gönderim körlemesine tekrarlanmayacak."; j.NextAttemptAt = clock.GetUtcNow().AddSeconds(store.Settings.Mode == "demo" ? Math.Min(30, Math.Pow(2, j.Attempts)) : 30); });
        }
    }
}
public sealed class StockOutboxWorker(StockOutbox outbox, ILogger<StockOutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try { do { try { await outbox.ProcessDue(stoppingToken); } catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested) { log.LogWarning("Stok kuyruğu işlenemedi: {Type}", e.GetType().Name); } } while (await timer.WaitForNextTickAsync(stoppingToken)); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
