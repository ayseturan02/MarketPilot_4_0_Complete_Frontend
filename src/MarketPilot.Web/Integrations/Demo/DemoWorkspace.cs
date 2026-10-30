using System.Text.Json;

namespace MarketPilot.Web.Integrations.Demo;

public sealed class DemoProductInfo
{
    public string Description { get; set; } = "";
    public string Symbol { get; set; } = "👕";
    public string Warehouse { get; set; } = "İstanbul / Ana depo";
    public int Reserved { get; set; }
    public Dictionary<string, string> Attributes { get; set; } = new();
}
public sealed record DemoEvent(string Title, DateTimeOffset Date, string? Reference = null);
public sealed class DemoClaim
{
    public string Id { get; set; } = "";
    public string OrderNumber { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string Barcode { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public string Reason { get; set; } = "";
    public string Status { get; set; } = "İnceleniyor";
    public DateTimeOffset ClaimDate { get; set; }
}
public sealed class DemoQuestion
{
    public string Id { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Text { get; set; } = "";
    public string Status { get; set; } = "WAITING_FOR_ANSWER";
    public DateTimeOffset CreationDate { get; set; }
    public DemoAnswer? Answer { get; set; }
}
public sealed record DemoAnswer(string Text);
public sealed record DemoFinancial(string Id, string OrderNumber, string TransactionType, decimal Amount, string Description, DateTimeOffset TransactionDate);
public sealed class DemoJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string State { get; set; } = "pending";
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset DueAt { get; set; } = DateTimeOffset.UtcNow.AddSeconds(2);
    public string Kind { get; set; } = "";
    public string Reference { get; set; } = "";
    public int? Quantity { get; set; }
    public decimal? Price { get; set; }
    public string? Text { get; set; }
    public string RequestId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
}
public sealed class DemoWorkspaceState
{
    public int Version { get; set; } = 1;
    public List<MarketplaceProduct> Products { get; set; } = new();
    public List<MarketplaceOrder> Orders { get; set; } = new();
    public Dictionary<string, DemoProductInfo> ProductInfo { get; set; } = new();
    public List<DemoClaim> Claims { get; set; } = new();
    public List<DemoQuestion> Questions { get; set; } = new();
    public List<DemoFinancial> Finance { get; set; } = new();
    public List<DemoJob> Jobs { get; set; } = new();
    public List<DemoEvent> Events { get; set; } = new();
    public DateTimeOffset LastSyncAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed record DemoQueueStatus(long Revision, int Pending, int Completed, int Failed, int Workspaces);
public sealed record DemoAction(string Type, string RequestId, string? Barcode = null, int? Quantity = null, decimal? Price = null, string? Reference = null, string? Text = null);

// Persistent, isolated sample workspace. No HTTP client or live marketplace credentials.
public sealed partial class DemoWorkspace
{
    private readonly object gate = new();
    private readonly string file;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private Dictionary<string, DemoWorkspaceState> states;
    private string persistedSnapshot = "{}";
    private long revision;
    public long Revision => Interlocked.Read(ref revision);
    public DemoQueueStatus QueueStatus()
    {
        lock (gate) return new(Revision, states.Values.Sum(s => s.Jobs.Count(j => j.State == "pending")),
            states.Values.Sum(s => s.Jobs.Count(j => j.State == "completed")), states.Values.Sum(s => s.Jobs.Count(j => j.State == "failed")), states.Count);
    }
    public DemoWorkspace(IWebHostEnvironment env)
    {
        var folder = Path.Combine(env.ContentRootPath, "Data"); Directory.CreateDirectory(folder);
        file = Path.Combine(folder, "integration-demo.json");
        // Never replace unreadable existing demo data with a seed silently.
        states = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, DemoWorkspaceState>>(File.ReadAllText(file), json) ?? throw new InvalidOperationException("Demo workspace cannot be read.") : new();
        persistedSnapshot=JsonSerializer.Serialize(states,json);
    }
    private static string Key(ConnectionSettings settings) => $"{settings.Marketplace}|{settings.Environment}|{settings.Name}";
    private void Save()
    {
        var candidate=JsonSerializer.Serialize(states,json);
        try{var temporary=file+".tmp";File.WriteAllText(temporary,candidate);for(var attempt=0;;attempt++){try{File.Move(temporary,file,true);break;}catch (Exception moveError) when (attempt < 4 && (moveError is IOException or UnauthorizedAccessException)){Thread.Sleep(10 * (1 << attempt));}}persistedSnapshot=candidate;Interlocked.Increment(ref revision);}
        catch{states=JsonSerializer.Deserialize<Dictionary<string,DemoWorkspaceState>>(persistedSnapshot,json)!;throw;}
    }
    public int ProcessPendingJobs()
    {
        lock(gate)
        {
            var count=0;
            foreach(var pair in states.ToArray())
            {
                var state=pair.Value;
                var due=state.Jobs.Where(j=>j.State=="pending"&&j.DueAt<=DateTimeOffset.UtcNow).OrderBy(j=>j.CreatedAt).ToList();
                foreach(var job in due){state=ApplyIsolated(pair.Key,state,job.Id);count++;}
                if(due.Count>0)state.LastSyncAt=DateTimeOffset.UtcNow;
            }
            if(count>0)Save();return count;
        }
    }
    private DemoWorkspaceState State(ConnectionSettings settings)
    {
        if (settings.Mode != "demo") throw new InvalidOperationException("Only demo stores can use this workspace.");
        if (!states.TryGetValue(Key(settings), out var state)) { state = Seed(settings); states[Key(settings)] = state; Save(); }
        if (settings.Marketplace == "hepsiburada")
        {
            var changed = false;
            for (var i = 0; i < state.Orders.Count; i++)
                if (state.Orders[i].Cargo.Provider == "Trendyol Express · Demo") { state.Orders[i] = state.Orders[i] with { Cargo = state.Orders[i].Cargo with { Provider = "HepsiJet · Demo" } }; changed = true; }
            if (changed) Save();
        }
        var due = state.Jobs.Where(j => j.State == "pending" && j.DueAt <= DateTimeOffset.UtcNow).OrderBy(j => j.CreatedAt).ToList();
        foreach (var job in due) state=ApplyIsolated(Key(settings),state,job.Id);
        if (due.Count > 0) { state.LastSyncAt = DateTimeOffset.UtcNow; Save(); }
        return state;
    }
    public DemoWorkspaceState Snapshot(ConnectionSettings settings)
    {
        lock (gate) return JsonSerializer.Deserialize<DemoWorkspaceState>(JsonSerializer.Serialize(State(settings), json), json)!;
    }
    public MarketplacePage<MarketplaceProduct> Products(ConnectionSettings settings, ProductQuery query)
    {
        var source = Snapshot(settings).Products.AsEnumerable();
        source = source.Where(p => query.Approval == "unapproved" ? p.ApprovalStatus != "approved" : p.ApprovalStatus == "approved")
            .Where(p => string.IsNullOrWhiteSpace(query.Barcode) || p.Barcode == query.Barcode)
            .Where(p => string.IsNullOrWhiteSpace(query.StockCode) || p.Sku == query.StockCode)
            .Where(p => string.IsNullOrWhiteSpace(query.Status) || p.ApprovalStatus == query.Status || query.Status == "onSale" && p.OnSale == true || query.Status == "notOnSale" && p.OnSale == false);
        var rows = source.ToList(); return new(rows.Skip(query.Page * query.Size).Take(query.Size).ToList(), query.Page, query.Size, rows.Count, (int)Math.Ceiling(rows.Count / (double)query.Size), null);
    }
    public MarketplacePage<MarketplaceOrder> Orders(ConnectionSettings settings, OrderQuery query)
    {
        var rows = Snapshot(settings).Orders.Where(o => string.IsNullOrWhiteSpace(query.Status) || o.Status == query.Status)
            .Where(o => string.IsNullOrWhiteSpace(query.OrderNumber) || o.OrderNumber == query.OrderNumber)
            .Where(o => string.IsNullOrWhiteSpace(query.Barcode) || o.Lines.Any(l => l.Barcode == query.Barcode))
            .Where(o => !query.StartDate.HasValue || o.OrderDate?.ToUnixTimeMilliseconds() >= query.StartDate.Value)
            .Where(o => !query.EndDate.HasValue || o.OrderDate?.ToUnixTimeMilliseconds() <= query.EndDate.Value).ToList();
        return new(rows.Skip(query.Page * query.Size).Take(query.Size).ToList(), query.Page, query.Size, rows.Count, (int)Math.Ceiling(rows.Count / (double)query.Size), null, query.Stream ? false : null);
    }
    public DemoJob Enqueue(ConnectionSettings settings, DemoAction action)
    {
        lock (gate)
        {
            var state = State(settings);
            if(action.Price.HasValue && (action.Type!="stock" || state.Products.Count(p=>p.Barcode==action.Barcode&&p.SalePrice==action.Price)!=1))throw new ArgumentException("Price changes require the ERP pricing approval flow.");
            if (string.IsNullOrWhiteSpace(action.RequestId) || action.RequestId.Length > 100) throw new ArgumentException("İşlem kimliği gerekli.");
            var fingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(action,json))));
            var existing = state.Jobs.FirstOrDefault(j => j.RequestId == action.RequestId); if (existing is not null) { if(existing.Fingerprint.Length>0&&existing.Fingerprint!=fingerprint)throw new ArgumentException("Aynı işlem kimliği farklı bir istek için kullanılamaz."); return existing; }
            if (state.Jobs.Count(j => j.State == "pending") >= 20) throw new ArgumentException("Bekleyen işlemlerin tamamlanmasını bekle.");
            var job = new DemoJob { Fingerprint=fingerprint, Kind = action.Type, RequestId = action.RequestId, Reference = action.Reference ?? action.Barcode ?? "", Quantity = action.Quantity, Price = action.Price, Text = action.Text };
            switch (action.Type)
            {
                case "stock":
                    if(settings.Marketplace=="n11"&&MarketPilot.Web.Integrations.N11.N11Demo.Depot(action.Barcode??""))throw new ArgumentException("n11depom stoğu satıcı düzenlemesiyle değiştirilemez.");
                    if (settings.Marketplace == "amazon" && MarketPilot.Web.Integrations.Amazon.AmazonDemo.IsAmazonFulfilled(action.Barcode ?? "")) throw new ArgumentException("Amazon FBA stoğu satıcı stok düzenlemesiyle değiştirilemez.");
                    if (!state.Products.Any(p => p.Barcode == action.Barcode)) throw new ArgumentException("Ürün bulunamadı.");
                    if (action.Quantity is null or < 0 or > 1000000 || action.Price is null or <= 0 or > 10000000) throw new ArgumentException("Stok ve fiyatı kontrol et.");
                    job.Title = "Stok ve fiyat güncellendi"; break;
                case "order-next":
                    var order = state.Orders.FirstOrDefault(o => o.PackageId == action.Reference) ?? throw new ArgumentException("Sipariş bulunamadı.");
                    if(settings.Marketplace=="n11"&&MarketPilot.Web.Integrations.N11.N11Demo.DepotOrder(order))throw new ArgumentException("Bu paketi n11depom gönderir; satıcı hazırlama adımı uygulanmaz.");
                    if (settings.Marketplace == "amazon" && MarketPilot.Web.Integrations.Amazon.AmazonDemo.Channel(order) == "AMAZON") throw new ArgumentException("Bu siparişi Amazon gönderir; satıcı hazırlama adımı uygulanmaz.");
                    if (!new[] { "Created", "Picking", "Invoiced", "Shipped" }.Contains(order.Status)) throw new ArgumentException("Sipariş için sonraki adım yok.");
                    if (state.Jobs.Any(j => j.State == "pending" && j.Kind == "order-next" && j.Reference == order.PackageId)) throw new ArgumentException("Sipariş işlemi zaten sürüyor.");
                    job.Text = order.Status; job.Title = "Sipariş durumu güncellendi"; break;
                case "answer":
                    if(state.Jobs.Any(j=>j.State=="pending"&&j.Kind=="answer"&&j.Reference==action.Reference))throw new ArgumentException("Bu sorunun yanıtı zaten işleniyor.");
                    if (!state.Questions.Any(q => q.Id == action.Reference && q.Status == "WAITING_FOR_ANSWER")) throw new ArgumentException("Yanıt bekleyen soru bulunamadı.");
                    if (string.IsNullOrWhiteSpace(action.Text) || action.Text.Trim().Length is < 10 or > 2000) throw new ArgumentException("Yanıt 10–2000 karakter olmalı.");
                    job.Text = action.Text.Trim(); job.Title = "Müşteri sorusu yanıtlandı"; break;
                case "claim-approve":
                    if(state.Jobs.Any(j=>j.State=="pending"&&j.Kind=="claim-approve"&&j.Reference==action.Reference))throw new ArgumentException("Bu iade zaten işleniyor.");
                    if (!state.Claims.Any(c => c.Id == action.Reference && c.Status == "İnceleniyor")) throw new ArgumentException("İncelenen iade bulunamadı.");
                    job.Title = "İade onaylandı ve stok güncellendi"; break;
                case "retry":
                    var failed = state.Jobs.FirstOrDefault(j => j.Id == action.Reference && j.State == "failed") ?? throw new ArgumentException("Tekrarlanacak işlem bulunamadı.");
                    if (failed.Kind != "stock") throw new ArgumentException("Bu işlem otomatik tekrarlanamaz.");
                    if (state.Jobs.Any(j => j.State == "pending" && j.Reference == failed.Reference)) throw new ArgumentException("Bu ürün için bir işlem zaten sürüyor.");
                    job.Kind = failed.Kind; job.Reference = failed.Reference; job.Quantity = failed.Quantity; job.Price = failed.Price; job.Title = failed.Title; failed.State = "retried"; break;
                default: throw new ArgumentException("Demo işlemi desteklenmiyor.");
            }
            state.Jobs.Insert(0, job); Save(); return job;
        }
    }
    private static void Apply(DemoWorkspaceState state, DemoJob job)
    {
        try
        {
            switch (job.Kind)
            {
                case "stock":
                    var index = state.Products.FindIndex(p => p.Barcode == job.Reference); if (index < 0) throw new ArgumentException("Ürün bulunamadı.");
                    var product = state.Products[index]; state.Products[index] = product with { Stock = job.Quantity, OnSale = job.Quantity > 0 && product.ApprovalStatus == "approved" }; break;
                case "order-next":
                    var oi = state.Orders.FindIndex(o => o.PackageId == job.Reference); if (oi < 0) throw new ArgumentException("Sipariş bulunamadı.");
                    var order = state.Orders[oi]; if (order.Status != job.Text) throw new ArgumentException("Sipariş durumu değişmiş; tekrar kontrol et.");
                    var next = order.Status switch { "Created" => "Picking", "Picking" => "Invoiced", "Invoiced" => "Shipped", "Shipped" => "Delivered", _ => throw new ArgumentException("Durum ilerletilemiyor.") };
                    var invoice = next == "Invoiced" ? order.Invoice with { Number = "DEMO-FTR-" + order.OrderNumber, Status = "Invoiced" } : order.Invoice;
                    var cargo = next == "Shipped" ? order.Cargo with { TrackingNumber = "DEMO-" + order.PackageId, ShipmentNumber = order.PackageId, TrackingLink = "" } : order.Cargo;
                    if (next == "Shipped") foreach (var line in order.Lines) if (state.ProductInfo.TryGetValue(line.Barcode, out var info)) info.Reserved = Math.Max(0, info.Reserved - line.Quantity);
                    state.Orders[oi] = order with { Status = next, Invoice = invoice, Cargo = cargo }; break;
                case "answer":
                    var question = state.Questions.First(q => q.Id == job.Reference); if (question.Status != "WAITING_FOR_ANSWER") throw new ArgumentException("Soru zaten yanıtlanmış.");
                    question.Status = "ANSWERED"; question.Answer = new(job.Text!); break;
                case "claim-approve":
                    var claim = state.Claims.First(c => c.Id == job.Reference); if (claim.Status != "İnceleniyor") throw new ArgumentException("İade zaten işlenmiş.");
                    var pi = state.Products.FindIndex(p => p.Barcode == claim.Barcode); if (pi < 0) throw new ArgumentException("İade ürünü bulunamadı.");
                    var p = state.Products[pi]; state.Products[pi] = p with { Stock = (p.Stock ?? 0) + claim.Quantity, OnSale = p.ApprovalStatus == "approved" }; claim.Status = "Onaylandı";
                    state.Finance.Insert(0, new("return-" + claim.Id, claim.OrderNumber, "Return", -(p.SalePrice ?? 0) * claim.Quantity, "Demo iade tutarı", DateTimeOffset.UtcNow)); break;
            }
            job.State = "completed"; state.Events.Insert(0, new(job.Title, DateTimeOffset.UtcNow, job.Reference));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { job.State = "failed"; job.Error = e.Message; }
    }
    private DemoWorkspaceState ApplyIsolated(string key,DemoWorkspaceState state,string jobId)
    {
        var before=JsonSerializer.Serialize(state,json);
        try {Apply(state,state.Jobs.Single(j=>j.Id==jobId));return state;}
        catch(Exception error) when(error is not OperationCanceledException && error is not OutOfMemoryException)
        {
            // Roll back partial mutations from this job, quarantine it, and continue other workspaces.
            var restored=JsonSerializer.Deserialize<DemoWorkspaceState>(before,json)!;
            var failed=restored.Jobs.Single(j=>j.Id==jobId);failed.State="failed";failed.Error="İşlem beklenmeyen veri hatası nedeniyle durduruldu; diğer işler devam ediyor.";
            states[key]=restored;return restored;
        }
    }
    private static DemoWorkspaceState Seed(ConnectionSettings settings)
    {
        var s = new DemoWorkspaceState(); var now = DateTimeOffset.UtcNow;
        var outlet = settings.Name.Contains("Outlet", StringComparison.OrdinalIgnoreCase);
        var samples = new[] {
            ("Pamuklu Oversize Tişört · Ekru / S", "TSH-EKR-S", "8691000000001", "TSH-EKR", 449m, 36, "Ekru", "S", "👕", "approved"),
            ("Pamuklu Oversize Tişört · Ekru / M", "TSH-EKR-M", "8691000000002", "TSH-EKR", 449m, 18, "Ekru", "M", "👕", "approved"),
            ("Pamuklu Oversize Tişört · Siyah / M", "TSH-SYH-M", "8691000000003", "TSH-SYH", 449m, 0, "Siyah", "M", "👕", "approved"),
            ("Keten Gömlek · Taş / L", "GML-TS-L", "8691000000004", "GML-TS", 899m, 12, "Taş", "L", "👔", "approved"),
            ("Dokuma Pantolon · Bej / 38", "PNT-BEJ-38", "8691000000005", "PNT-BEJ", 1099m, 7, "Bej", "38", "👖", "approved"),
            ("Fermuarlı Sweatshirt · Gri / M", "SWT-GRI-M", "8691000000006", "SWT-GRI", 799m, 24, "Gri", "M", "🧥", "approved"),
            ("Triko Hırka · Kahve / Standart", "HRK-KHV-ST", "8691000000007", "HRK-KHV", 1199m, 9, "Kahve", "Standart", "🧶", "pendingApproval"),
            ("Çizgili Tişört · Lacivert / L", "TSH-LCV-L", "8691000000008", "TSH-LCV", 499m, 16, "Lacivert", "L", "👕", "rejected")
        };
        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i]; var stock = outlet ? Math.Max(0, x.Item6 / 3) : settings.Marketplace == "trendyol" ? x.Item6 : Math.Max(0, x.Item6 - 4);
            s.Products.Add(new((i + 1).ToString(), "content-" + x.Item4, x.Item4, x.Item2, x.Item3, x.Item1, "textile", "Giyim / Günlük Koleksiyon", "İnternet Tekstil", x.Item5 + 150, x.Item5, x.Item5, stock, "TRY", stock > 0 && x.Item10 == "approved", "", [], x.Item10, x.Item10 == "rejected" ? ["Örnek red nedeni: ürün açıklamasında kumaş içeriği eksik."] : []));
            s.ProductInfo[x.Item3] = new() { Symbol = x.Item9, Description = "Günlük kullanıma uygun, yumuşak dokulu yeni sezon ürün. Bu kayıt, mağaza operasyonlarını denemek için oluşturulmuş örnek üründür.", Attributes = new() { ["Renk"] = x.Item7, ["Beden"] = x.Item8, ["Kumaş"] = i == 3 ? "Keten karışımı" : "%100 pamuk", ["Sezon"] = "Sonbahar / Kış", ["Üretim yeri"] = "Türkiye" } };
        }
        var statuses = new[] { "Created", "Picking", "Invoiced", "Shipped", "Delivered", "Returned" };
        var customers = new[] { "Örnek Müşteri A", "Örnek Müşteri B", "Örnek Müşteri C", "Örnek Müşteri D", "Örnek Müşteri E", "Örnek Müşteri F" };
        for (var i = 0; i < statuses.Length; i++)
        {
            var p = s.Products[i == 2 ? 3 : i]; var qty = i == 0 ? 2 : 1; var price = p.SalePrice!.Value;
            var invoice = new MarketplaceInvoice(i >= 2 ? "DEMO-FTR-" + (1001 + i) : "", i >= 2 ? "Invoiced" : "Bekliyor", "", false, "", "", "", false, []);
            s.Orders.Add(new("demo-pkg-" + (1001 + i), "DEMO-" + (1001 + i), statuses[i], now.AddHours(-i * 5 - 1), customers[i], "ornek@example.invalid", price * qty, (p.ListPrice ?? price) * qty, ((p.ListPrice ?? price) - price) * qty, "TRY", "Kredi kartı", "Shipment", new("address-" + i, "Shipment", "Türkiye", "İstanbul", "Kadıköy", "34710", "Örnek Mahallesi, Demo Sokak No: 1 — Gerçek teslimat adresi değildir.", false), [new("line-" + i, p.Sku, p.Barcode, p.Name, qty, price, price * qty, 20, "TRY", statuses[i], p.CategoryId)], new(settings.Marketplace == "hepsiburada" ? "HepsiJet · Demo" : "Trendyol Express · Demo", i >= 3 ? "DEMO-TRACK-" + (1001 + i) : "", "", "", "demo-pkg-" + (1001 + i), 1.5m, 1), invoice));
            if (i < 3) s.ProductInfo[p.Barcode].Reserved += qty;
            s.Events.Add(new("Sipariş oluşturuldu", now.AddHours(-i * 5 - 1), "demo-pkg-" + (1001 + i)));
            if (i >= 2) s.Finance.Add(new("sale-" + i, "DEMO-" + (1001 + i), "Sale", price * qty, "Demo satış tutarı", now.AddHours(-i * 5)));
        }
        s.Claims = [new() { Id = "DEMO-IADE-01", OrderNumber = "DEMO-1006", CustomerName = customers[5], Barcode = samples[5].Item3, Reason = "Beden uygun değil", ClaimDate = now.AddHours(-6) }, new() { Id = "DEMO-IADE-02", OrderNumber = "DEMO-0998", CustomerName = "Örnek Müşteri G", Barcode = samples[0].Item3, Reason = "Farklı renk talebi", Status = "Onaylandı", ClaimDate = now.AddDays(-2) }];
        s.Questions = [new() { Id = "demo-question-1", ProductName = samples[0].Item1, Text = "Kumaşı yüzde yüz pamuk mu? Kalıbı geniş mi?", CreationDate = now.AddMinutes(-35) }, new() { Id = "demo-question-2", ProductName = samples[3].Item1, Text = "Bu ürünün M bedeni tekrar gelir mi?", CreationDate = now.AddHours(-2) }, new() { Id = "demo-question-3", ProductName = samples[4].Item1, Text = "Pantolonun boy ölçüsü nedir?", Status = "ANSWERED", Answer = new("Merhaba, ürünün dış boy ölçüsü 104 cm'dir. İyi günler dileriz."), CreationDate = now.AddDays(-1) }];
        s.Finance.Add(new("commission-demo", "DEMO-1003", "Commission", -89.9m, "Örnek komisyon kesintisi", now.AddHours(-5)));
        s.Finance.Add(new("shipping-demo", "DEMO-1004", "Cargo", -54.9m, "Örnek kargo kesintisi", now.AddHours(-5)));
        s.Jobs = [new() { Title = "Stok ve fiyat güncellendi", State = "failed", Error = "Örnek geçici bağlantı hatası. Tekrar denemeyi test edebilirsin.", Kind = "stock", Reference = samples[5].Item3, Quantity = 24, Price = 799m, CreatedAt = now.AddMinutes(-18), RequestId = "seed-failed-stock" }, new() { Title = "Örnek ürünler ve siparişler yüklendi", State = "completed", Kind = "seed", CreatedAt = now.AddMinutes(-30), RequestId = "seed-completed" }];
        return s;
    }
}

public static class DemoWorkspaceEndpoints
{
    public static void MapDemoWorkspace(this RouteGroupBuilder group)
    {
        group.MapGet("/demo/connections/{id}/search/{kind}", (string id, string kind, [AsParameters] ListingQuery query, IntegrationRegistry registry, DemoWorkspace demo) =>
        {
            if (!registry.Connections.TryGetValue(id, out var store)) return Results.NotFound();
            if (store.Settings.Mode != "demo") return Results.Json(new { error = "Demo mağaza gerekli." }, statusCode: 403);
            if(query.Error() is { } error) return Results.BadRequest(new { error });
            try { return Results.Ok(demo.Search(store.Settings, kind, query)); } catch(ArgumentException) { return Results.NotFound(); }
        });
        group.MapGet("/demo/connections/{id}/workspace", (string id, IntegrationRegistry registry, DemoWorkspace demo) =>
        {
            if (!registry.Connections.TryGetValue(id, out var store)) return Results.NotFound();
            if (store.Settings.Mode != "demo") return Results.Json(new { error = "Bu işlem yalnızca demo mağazalar içindir." }, statusCode: 403);
            return Results.Ok(demo.Snapshot(store.Settings));
        });
        group.MapPost("/demo/connections/{id}/actions", (string id, DemoAction action, IntegrationRegistry registry, DemoWorkspace demo) =>
        {
            if (!registry.Connections.TryGetValue(id, out var store)) return Results.NotFound();
            if (store.Settings.Mode != "demo") return Results.Json(new { error = "Bu işlem yalnızca demo mağazalar içindir." }, statusCode: 403);
            try { return Results.Ok(demo.Enqueue(store.Settings, action)); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        });
    }
}
