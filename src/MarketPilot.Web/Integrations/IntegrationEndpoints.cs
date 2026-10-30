using System.Collections.Concurrent;
using MarketPilot.Web.Integrations.Amazon;
using MarketPilot.Web.Integrations.Boyner;
using MarketPilot.Web.Integrations.CentralStock;
using MarketPilot.Web.Integrations.Ticimax;
using MarketPilot.Web.Integrations.Demo;
using MarketPilot.Web.Integrations.Hepsiburada;
using MarketPilot.Web.Integrations.N11;
using MarketPilot.Web.Integrations.Trendyol;
using MarketPilot.Web.Local;

namespace MarketPilot.Web.Integrations;

public sealed class IntegrationRegistry
{
    private long revision;
    public long Revision => Interlocked.Read(ref revision);
    public ConcurrentDictionary<string, ConnectionState> Connections { get; } = new();
    public ConcurrentDictionary<string, SemaphoreSlim> Gates { get; } = new();
    public ConcurrentDictionary<string, CachedAccount> Accounts { get; } = new();
    private readonly string file;
    private readonly object saveGate = new();
    public void Save() { lock(saveGate) { var temp=file+".tmp"; System.IO.File.WriteAllText(temp,System.Text.Json.JsonSerializer.Serialize(Connections.Values.ToArray(),new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))); System.IO.File.Move(temp,file,true);Interlocked.Increment(ref revision); } }
    public IntegrationRegistry(IWebHostEnvironment env)
    {
        var folder=System.IO.Path.Combine(env.ContentRootPath,"Data");System.IO.Directory.CreateDirectory(folder);file=System.IO.Path.Combine(folder,"integration-connections.json");
        if(System.IO.File.Exists(file))foreach(var store in System.Text.Json.JsonSerializer.Deserialize<ConnectionState[]>(System.IO.File.ReadAllText(file),new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? [])Connections[store.Id]=store;
        foreach (var marketplace in new[] { "trendyol", "hepsiburada", "n11", "amazon", "boyner", "ticimax" })
            if(!Connections.ContainsKey(marketplace)) Connections[marketplace] = new(marketplace, new(marketplace == "trendyol" ? "Trendyol API Sözleşme Demosu" : marketplace, marketplace, "demo", "stage"), "demo", null, null, null,
                marketplace == "trendyol" ? "Resmî Product V2 ve Order V2 cevap yapılarıyla yerel demo; dış API çağrısı yapılmadı." : "Demo · Dış API çağrısı yapılmadı.", null);
        Connections.TryAdd("trendyol-outlet",new("trendyol-outlet",new("İnternet Tekstil Outlet · Demo","trendyol","demo","stage"),"demo",null,null,null,"Ayrı örnek stokları olan demo mağaza.",null));
        Save();
    }
}
public record CachedAccount(MarketplaceAccount Account, DateTimeOffset ExpiresAt);

public static class IntegrationEndpoints
{
    public static void AddMarketplaceIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IntegrationRegistry>();
        services.AddHttpClient("MarketplaceScoped", c => c.Timeout = TimeSpan.FromSeconds(60)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<MarketplaceClients>();
        services.AddSingleton<MarketPilot.Web.Synchronization.MarketplaceStockWriter>();
        services.AddSingleton<DemoWorkspace>();
        services.AddSingleton<DemoMarketplaceAdapter>();
        services.AddTrendyolIntegration();
        services.AddHepsiburadaIntegration();
        services.AddN11Integration();
        services.AddAmazonIntegration();
        services.AddBoynerIntegration();
        services.AddTicimaxIntegration();
        services.AddCentralStock();
    }

    public static void MapMarketplaceIntegrations(this WebApplication app)
    {
        var group = app.MapGroup("/api/integrations");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            if (!LocalAccess.IsLoopback(http) &&
                !(LocalAccess.IsPublicDeployment(http.RequestServices.GetRequiredService<IConfiguration>()) &&
                  http.User.Identity?.IsAuthenticated == true))
                return Results.Json(new { error = "Entegrasyon paneli yalnızca yerel erişime açıktır." }, statusCode: 403);
            if (http.Request.Headers.TryGetValue("Origin", out var origin) &&
                origin.ToString() != $"{http.Request.Scheme}://{http.Request.Host}")
                return Results.Json(new { error = "Farklı kaynaktan entegrasyon isteği reddedildi." }, statusCode: 403);
            return await next(context);
        });
        group.MapDemoWorkspace();
        group.MapHepsiburada();
        group.MapAmazon();
        group.MapN11();
        group.MapBoyner();
        group.MapTicimax();
        group.MapCentralStock();
        group.MapGet("/readiness", (IConfiguration configuration, IntegrationRegistry registry) => Results.Ok(new
        {
            profiles = CredentialProfiles.List(configuration),
            stores = registry.Connections.Values.Select(s=>CredentialProfiles.Readiness(configuration,s)).ToArray(),
            stockPriceSupported = CredentialProfiles.Markets,
            ticimax = new { stage = false, production = TicimaxSoap.IsConfigured(configuration,"production"), writes = new { stage = CredentialProfiles.Writes(configuration,new("", "ticimax", "real", "stage")), production = CredentialProfiles.Writes(configuration,new("", "ticimax", "real", "production")) } },
            boyner = new { stage = false, production = BoynerAdapter.IsConfigured(configuration,"production"), writes = new { stage = CredentialProfiles.Writes(configuration,new("", "boyner", "real", "stage")), production = CredentialProfiles.Writes(configuration,new("", "boyner", "real", "production")) } },
            n11 = new { stage = false, production = N11Adapter.IsConfigured(configuration,"production"), writes = new { stage = CredentialProfiles.Writes(configuration,new("", "n11", "real", "stage")), production = CredentialProfiles.Writes(configuration,new("", "n11", "real", "production")) } },
            amazon = new { stage = AmazonApiClient.IsConfigured(configuration,"stage"), production = AmazonApiClient.IsConfigured(configuration,"production"), writes = new { stage = CredentialProfiles.Writes(configuration,new("", "amazon", "real", "stage")), production = CredentialProfiles.Writes(configuration,new("", "amazon", "real", "production")) } },
            hepsiburada = new { stage = HepsiburadaAdapter.IsConfigured(configuration,"stage"), production = HepsiburadaAdapter.IsConfigured(configuration,"production"), writes = new { stage = CredentialProfiles.Writes(configuration,new("", "hepsiburada", "real", "stage")), production = CredentialProfiles.Writes(configuration,new("", "hepsiburada", "real", "production")) } },
            trendyol = new
            {
                stage = TrendyolApiClient.IsConfigured(configuration, "stage"), production = TrendyolApiClient.IsConfigured(configuration, "production"),
                writes = new { stage = TrendyolApiClient.WritesAllowed(configuration, "stage"), production = TrendyolApiClient.WritesAllowed(configuration, "production") }
            }
        }));
        group.MapGet("/credential-profiles", (IConfiguration configuration) => Results.Ok(CredentialProfiles.List(configuration)));
        group.MapGet("/connections", (IntegrationRegistry registry) => Results.Ok(registry.Connections.Values.OrderBy(x => x.Settings.Marketplace).ThenBy(x => x.Settings.Name)));
        group.MapPost("/connections", (ConnectionSettings settings, IntegrationRegistry registry, IConfiguration configuration) =>
        {
            if (string.IsNullOrWhiteSpace(settings.Name) || settings.Name.Length > 80 ||
                !new[] { "trendyol", "hepsiburada", "n11", "amazon", "boyner", "ticimax" }.Contains(settings.Marketplace) ||
                !new[] { "demo", "real" }.Contains(settings.Mode) || !new[] { "stage", "production" }.Contains(settings.Environment))
                return Results.BadRequest(new { error = "Mağaza adı, pazaryeri, mod ve ortam değerlerini kontrol edin." });
            if (registry.Connections.Count >= 50) return Results.BadRequest(new { error = "Bu geçici çalışma alanında en fazla 50 mağaza eklenebilir." });
            if (settings.Mode == "real")
            {
                settings = settings with { CredentialProfile = settings.CredentialProfile ?? CredentialProfiles.Unassigned };
                if (settings.CredentialProfile != CredentialProfiles.Unassigned)
                    try { CredentialProfiles.For(configuration, settings); } catch (IntegrationException e) { return Results.Json(new { error = e.Message, code = e.Code }, statusCode: e.Status); }
            }
            else settings = settings with { CredentialProfile = null };
            lock(registry.Connections)
            {
            if (registry.Connections.Count >= 50) return Results.BadRequest(new { error = "En fazla 50 mağaza eklenebilir." });
            if(settings.Mode=="real" && settings.CredentialProfile!=CredentialProfiles.Unassigned && registry.Connections.Values.Any(x=>x.Settings.Mode=="real"&&x.Settings.Marketplace==settings.Marketplace&&x.Settings.Environment==settings.Environment&&x.Settings.CredentialProfile==settings.CredentialProfile))
                return Results.Conflict(new{error="Bu kimlik profiline bağlı mağaza zaten var. Farklı satıcı için ayrı profil seçin."});
            var id = Guid.NewGuid().ToString("N");
            var state = new ConnectionState(id, settings with { Name = settings.Name.Trim() }, settings.Mode == "demo" ? "demo" : "untested", null, null, null, "Henüz bağlantı testi yapılmadı.", null);
            registry.Connections[id] = state;
            try { registry.Save(); } catch { registry.Connections.TryRemove(id,out _); throw; }
            return Results.Ok(state);
            }
        });
        group.MapPost("/connections/{id}/{operation}", async (
            string id, string operation, int? page, int? size, string? barcode, string? stockCode, string? status, string? approval,
            string? nextPageToken, string? orderNumber, long? startDate, long? endDate, string? orderByDirection, bool? stream, string? nextCursor,
            IntegrationRegistry registry, DemoMarketplaceAdapter demo, MarketplaceClients clients, IConfiguration configuration, MarketPilot.Web.Catalog.OperationsHub operations, CancellationToken ct) =>
        {
            if (!registry.Connections.TryGetValue(id, out var state)) return Results.NotFound(new { error = "Mağaza bulunamadı." });
            if (!new[] { "test", "account", "products", "orders" }.Contains(operation)) return Results.NotFound();
            var p = page ?? 0; var n = size ?? 50;
            var maxSize = operation == "orders" ? 200 : 100;
            var cursorRequest = operation == "products" && !string.IsNullOrWhiteSpace(nextPageToken) || operation == "orders" && stream == true;
            if (p < 0 || n < 1 || n > maxSize || (!cursorRequest && ((long)p + 1) * n > 10000))
                return Results.BadRequest(new { error = $"Sayfa sıfırdan başlar; boyut 1–{maxSize} ve sorgu penceresi en fazla 10.000 olmalıdır." });
            if (new[] { barcode, stockCode, orderNumber }.Any(x => x?.Length > 100) || nextPageToken?.Length > 2048 || nextCursor?.Length > 2048)
                return Results.BadRequest(new { error = "Filtre değeri izin verilen uzunluğu aşıyor." });
            var productApproval = approval == "unapproved" ? "unapproved" : "approved";
            if (state.Settings.Marketplace != "amazon" && operation == "products" && !string.IsNullOrWhiteSpace(status) && !(productApproval == "approved" ? new[] { "archived", "blacklisted", "locked", "onSale", "notOnSale" } : new[] { "rejected", "pendingApproval" }).Contains(status))
                return Results.BadRequest(new { error = "Geçersiz ürün durumu filtresi." });
            if (state.Settings.Marketplace != "amazon" && operation == "orders" && !string.IsNullOrWhiteSpace(status) && !new[] { "Created", "Picking", "Invoiced", "Shipped", "Cancelled", "Delivered", "UnDelivered", "Returned", "AtCollectionPoint", "UnPacked", "UnSupplied", "Awaiting", "Verified" }.Contains(status))
                return Results.BadRequest(new { error = "Geçersiz sipariş durumu filtresi." });
            if (operation == "orders" && stream == true && (!string.IsNullOrWhiteSpace(orderNumber) || !string.IsNullOrWhiteSpace(barcode) || status == "Verified"))
                return Results.BadRequest(new { error = "Büyük veri akışında sipariş no, barkod ve Verified filtresi desteklenmez." });
            if ((startDate.HasValue != endDate.HasValue) || (startDate.HasValue && (startDate.Value > endDate!.Value || endDate.Value - startDate.Value > TimeSpan.FromDays(14).TotalMilliseconds)))
                return Results.BadRequest(new { error = "Başlangıç ve bitiş birlikte verilmeli; tarih aralığı en fazla 14 gün olmalıdır." });
            var gate = registry.Gates.GetOrAdd(id, _ => new(1, 1));
            if (!await gate.WaitAsync(0, ct)) return Results.Json(new { error = "Bu mağazada bir işlem zaten sürüyor." }, statusCode: 409);
            try
            {
                var adapter = state.Settings.Mode == "demo" ? demo : clients.Adapter(state.Settings);
                object? result = null; int? count = null; MarketplaceAccount? account = null;
                if (operation == "test") { account = await adapter.TestAsync(state.Settings, ct); result = account; }
                else if (operation == "account")
                {
                    var accountCacheKey = $"{state.Settings.Mode}:{state.Settings.Marketplace}:{state.Settings.Environment}:{CredentialProfiles.Identity(configuration, state.Settings)}";
                    if (registry.Accounts.TryGetValue(accountCacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow) account = cached.Account;
                    else
                    {
                        account = await adapter.AccountAsync(state.Settings, ct);
                        registry.Accounts[accountCacheKey] = new(account, DateTimeOffset.UtcNow.AddHours(1));
                    }
                    result = account; count = account.Addresses.Count;
                }
                else if (operation == "products")
                {
                    var r = await adapter.ProductsAsync(state.Settings, new(p, n, barcode, stockCode, status, nextPageToken, productApproval), ct);
                    result = r; count = r.Items.Count;
                }
                else
                {
                    var r = await adapter.OrdersAsync(state.Settings, new(p, n, status, orderNumber, barcode, startDate, endDate, orderByDirection == "ASC" ? "ASC" : "DESC", stream == true, nextCursor), ct);
                    result = r; count = r.Items.Count; operations.Observe(state,r);
                }
                var updated = state with {
                    Status = state.Settings.Mode == "demo" ? "demo" : "connected", LastOperation = DateTimeOffset.UtcNow,
                    LastAction = operation, ErrorCode = null,
                    Message = operation == "test" ? "Mağaza bağlantı bilgileri doğrulandı." : operation == "account" ? "Satıcı hesabı okundu; sonuç bir saat önbelleğe alındı." : "Sayfa okundu; yerel kayıtlara aktarılmadı.",
                    LastCount = count, SellerId = account?.SellerId ?? state.SellerId
                };
                registry.Connections[id] = updated; registry.Save();
                return Results.Ok(new { connection = updated, result });
            }
            catch (Exception ex) when (ex is IntegrationException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException or OverflowException)
            {
                var error = ex as IntegrationException ?? new IntegrationException(ex is OperationCanceledException ? "timeout" : "read_failed", ex is OperationCanceledException ? "API isteği zaman aşımına uğradı veya iptal edildi." : "API yanıtı okunamadı; bağlantıyı ve yanıt sözleşmesini kontrol edin.");
                var updated = state with { Status = "error", LastOperation = DateTimeOffset.UtcNow, LastAction = operation, ErrorCode = error.Code, Message = error.Message, LastCount = null };
                registry.Connections[id] = updated; registry.Save();
                return Results.Json(new { error = error.Message, code = error.Code, connection = updated }, statusCode: error.Status);
            }
            finally { gate.Release(); }
        });
        group.MapPost("/connections/{id}/stock-price", (string id, MarketplaceStockRequest input, IntegrationRegistry registry, MarketplaceClients clients, MarketPilot.Web.Synchronization.StockOutbox outbox, MarketPilot.Web.Synchronization.MarketplaceStockWriter writer) =>
        {
            if(!registry.Connections.TryGetValue(id,out var store))return Results.NotFound();
            if(!input.Confirm)return Results.BadRequest(new{error="Stok/fiyat gönderimi için açık onay gerekli."});
            if(string.IsNullOrWhiteSpace(input.RequestId)||input.RequestId.Length>100 || input.Sku?.Length>100 || input.Barcode?.Length>100 ||
                string.IsNullOrWhiteSpace(input.Sku)&&string.IsNullOrWhiteSpace(input.Barcode) || input.Quantity is <0 or >1000000 ||
                input.Price is <=0 or >10000000 || input.Price.HasValue && decimal.Round(input.Price.Value,2)!=input.Price.Value)
                return Results.BadRequest(new{error="İşlem kimliği, SKU/barkod, tam sayı stok ve en fazla iki ondalıklı fiyat gerekli."});
            try
            {
                input=input with{Sku=string.IsNullOrWhiteSpace(input.Sku)?null:input.Sku.Trim(),Barcode=string.IsNullOrWhiteSpace(input.Barcode)?null:input.Barcode.Trim()};
                if(store.Settings.Mode=="real")writer.Check(store.Settings);
                var job=outbox.Enqueue(store,string.IsNullOrWhiteSpace(input.Barcode)?"sku:"+input.Sku:input.Barcode,input.Quantity,input.Price,"Mağaza stok/fiyat işlemi",nativeSku:input.Sku,requestId:input.RequestId);
                return Results.Accepted("/api/sync/outbox/"+job.Id,new{job,message="Kalıcı gönderim kuyruğuna alındı; mağazadan doğrulanınca tamamlanır."});
            }catch(IntegrationException e){return Results.Json(new{error=e.Message,code=e.Code},statusCode:e.Status);}
            catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
        });
        group.MapTrendyolApi();
        group.MapPost("/preview/{marketplace}/stock-price", (string marketplace, StockPriceDraft[] items) =>
        {
            if (!new[] { "trendyol", "hepsiburada", "n11", "amazon", "boyner", "ticimax" }.Contains(marketplace) || items.Length is 0 or > 1000 || items.Any(i => string.IsNullOrWhiteSpace(i.Barcode) || i.Stock < 0 || i.Price < 0))
                return Results.BadRequest(new { error = "Geçerli pazaryeri, barkod ve negatif olmayan stok/fiyat gerekli (1–1000 kalem)." });
            return Results.Ok(new UpdatePreview(marketplace, false, items));
        });
    }
}

public sealed record MarketplaceStockRequest(string RequestId, int Quantity, string? Barcode=null, string? Sku=null, decimal? Price=null, bool Confirm=false);
