using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketPilot.Web.Enterprise;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Demo;

namespace MarketPilot.Web.Catalog;
public sealed partial class CatalogHub(DemoStore db, IntegrationRegistry registry, DemoWorkspace demo, IServiceProvider? services = null, IConfiguration? configuration = null,EnterpriseSync? enterpriseSync = null)
{
    private int initialized;
    private readonly object bootstrapGate = new();
    public static string Source(ConnectionState c) => c.Settings.Mode == "demo" ? $"demo|{c.Settings.Marketplace}|{c.Settings.Environment}|{c.Settings.Name}" : $"real|{c.Settings.Marketplace}|{c.Settings.Environment}" + (c.Settings.CredentialProfile is null ? "" : "|" + c.Settings.CredentialProfile);
    public static string Key(string storeId, string identity) => storeId + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    public void Ensure()
    {
        if (Volatile.Read(ref initialized) == 1)
            return;
        lock (bootstrapGate)
        {
            if (initialized == 1)
                return;
            if (!db.CatalogRead(s => s.Catalog.Version > 0))
            {
                var seeds = registry.Connections.Values.Where(c => c.Settings.Mode == "demo").OrderBy(c => c.Id == "trendyol" ? 0 : 1).ThenBy(c => c.Id).Select(c =>
                {
                    var snapshot = demo.Snapshot(c.Settings);
                    return (Store: c, Rows: snapshot.Products.Select(p => Candidate(c, p)with { Description = snapshot.ProductInfo.GetValueOrDefault(p.Barcode)?.Description, Attributes = snapshot.ProductInfo.GetValueOrDefault(p.Barcode)?.Attributes }).ToArray());
                }).ToArray();
                db.Modify(s =>
                {
                    if (s.Catalog.Version > 0)
                        return false;
                    foreach (var seed in seeds)
                        ImportRows(s, seed.Store, seed.Rows, DateTimeOffset.UtcNow);
                    s.Catalog.Version = 1;
                    db.Event(s, "Katalog bağlandı", "ERP sahibi demo ürünler mağaza kayıtlarıyla eşleştirildi; sahipsiz eski kayıtlar korundu ve salt okunur bırakıldı.");
                    return true;
                });
            }

            Volatile.Write(ref initialized, 1);
        }
    }

    public static CatalogCandidate Candidate(ConnectionState store, MarketplaceProduct p) => new(Key(store.Id, Identity(p.Id, p.Sku, p.Barcode)), p.Id, p.Sku, p.Barcode, p.Name, p.CategoryName, p.BrandName, p.Stock, p.SalePrice, p.Currency, store.Settings.Mode == "demo" ? true : null, p.ApprovalStatus, p.Images);
    public static string Identity(string id, string sku, string barcode) => !string.IsNullOrWhiteSpace(id) ? "id:" + id : !string.IsNullOrWhiteSpace(sku) ? "sku:" + sku : !string.IsNullOrWhiteSpace(barcode) ? "barcode:" + barcode : throw new IntegrationException("missing_identity", "Mağaza ürününün kimliği bildirilmedi.", 409);
    private Func<CatalogListing, CatalogListing> Fresh()
    {
        var cache = new Dictionary<string, Dictionary<string, MarketplaceProduct>>();
        return link => Current(link, cache);
    }
    public CatalogListing[] ReconciliationSnapshot(){Ensure();var fresh=Fresh();return db.CatalogRead(s=>s.Catalog.Listings.Values.Select(fresh).ToArray());}

    private CatalogListing Current(CatalogListing link, Dictionary<string, Dictionary<string, MarketplaceProduct>> cache)
    {
        if (!registry.Connections.TryGetValue(link.StoreId, out var c) || Source(c) != link.Source)
            return link with
            {
                Status = "source_changed"
            };
        if (c.Settings.Mode != "demo")
            return link;
        if (!cache.TryGetValue(c.Id, out var products))
        {
            products = demo.Snapshot(c.Settings).Products.GroupBy(p => p.Id + "\0" + p.Barcode).ToDictionary(g => g.Key, g => g.First());
            cache[c.Id] = products;
        }

        var p = products.GetValueOrDefault(link.ExternalId + "\0" + link.Barcode);
        if (p == null)
            return link with
            {
                Status = "missing"
            };
        // FBA quantities are not merchant-fulfilled stock and must not become a publish target.
        var stock = c.Settings.Mode == "demo" && c.Settings.Marketplace == "amazon" && MarketPilot.Web.Integrations.Amazon.AmazonDemo.IsAmazonFulfilled(p.Barcode) || c.Settings.Mode == "demo" && c.Settings.Marketplace == "n11" && MarketPilot.Web.Integrations.N11.N11Demo.Depot(p.Barcode) ? null : p.Stock;
        return link with
        {
            Stock = stock,
            Price = p.SalePrice,
            Currency = p.Currency,
            Status = p.ApprovalStatus,
            ObservedAt = DateTimeOffset.UtcNow
        };
    }

    public ListingPage<CatalogProductRow> Search(CatalogQuery q)
    {
        Ensure();
        var fresh = Fresh();
        var safetyStock=enterpriseSync?.Plan.SafetyStock??0;
        return db.CatalogRead<ListingPage<CatalogProductRow>>(s =>
        {
            var byProduct = s.Catalog.Listings.Values.GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => g.ToArray());
            var rows = s.Products.Where(p=>p.SourceSystem==ProductOwnershipPolicy.ErpSource).Where(p => string.IsNullOrWhiteSpace(p.Category) || IsClothingCategory(p.Category)).Where(p => string.IsNullOrWhiteSpace(q.Search) || ListingFilters.Text(p.Name, q.Search) || ListingFilters.Text(p.Sku, q.Search) || ListingFilters.Text(p.Barcode, q.Search)).Where(p => ListingFilters.Exact(p.Status, q.Status) && ListingFilters.Text(p.Category, q.Category) && ListingFilters.Exact(p.SourceMode, q.Mode)).Where(p => string.IsNullOrWhiteSpace(q.StoreId) || byProduct.GetValueOrDefault(p.Id, []).Any(l => l.StoreId == q.StoreId)).Where(p => q.Mapped != "yes" || byProduct.ContainsKey(p.Id)).Where(p => q.Mapped != "no" || !byProduct.ContainsKey(p.Id)).OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id);
            var total = rows.Count();
            var items = rows.Skip(q.Page * q.Size).Take(q.Size).Select(p => new CatalogProductRow(Project(s,p,safetyStock), byProduct.GetValueOrDefault(p.Id, []).Take(8).Select(fresh).ToArray(), byProduct.GetValueOrDefault(p.Id, []).Length)).ToArray();
            return new(items, q.Page, q.Size, total, (int)Math.Ceiling(total / (double)q.Size));
        });
    }

    public object Detail(string id, CatalogQuery q)
    {
        Ensure();
        var fresh = Fresh();
        var safetyStock=enterpriseSync?.Plan.SafetyStock??0;
        return db.CatalogRead(s =>
        {
            var product = s.Products.FirstOrDefault(p => p.Id == id && p.SourceSystem==ProductOwnershipPolicy.ErpSource) ?? throw new IntegrationException("not_found", "ERP ana ürünü bulunamadı.", 404);
            var links = s.Catalog.Listings.Values.Where(l => l.ProductId == id && (string.IsNullOrWhiteSpace(q.StoreId) || l.StoreId == q.StoreId)).OrderBy(l => l.StoreId).ToArray();
            return (object)new
            {
                product = Project(s,product,safetyStock),
                listings = new ListingPage<CatalogListing>(links.Skip(q.Page * q.Size).Take(q.Size).Select(fresh).ToArray(), q.Page, q.Size, links.Length, (int)Math.Ceiling(links.Length / (double)q.Size))
            };
        });
    }

    private static Product Clone(Product p) => JsonSerializer.Deserialize<Product>(JsonSerializer.Serialize(p))!;
    private static Product Project(Database s,Product product,int safetyStock)
    {
        var projected=Clone(product);
        MarketPilot.Web.Pricing.PricingProjection.Apply(s,projected);
        var price=MarketPilot.Web.Pricing.ErpPricingService.MasterEntry(s,product,DateTimeOffset.UtcNow);
        projected.PriceKnown=price?.Amount is >0;
        if(price?.Amount is >0){projected.Price=price.Amount.Value;projected.Currency=price.Currency;projected.VatIncluded=price.VatIncluded;}
        projected.SellableStock=MarketPilot.Web.Workspace.OperationalInventory.Sellable(s,product,requireReservationSnapshot:true,safetyStock:safetyStock);
        projected.SellableStockState=projected.SellableStock.HasValue?StockValueState.Known:
            !product.StockKnown||product.ReservationState==StockValueState.Unknown?StockValueState.Unknown:StockValueState.NotProvided;
        return projected;
    }
    public object Edit(string id, CatalogEdit edit)
    {
        Ensure();
        ValidateEdit(edit);
        if (!db.CatalogRead(s => s.Products.Any(p => p.Id == id)))
            throw new IntegrationException("not_found", "Ürün bulunamadı.", 404);
        throw ProductOwnershipPolicy.ManualWriteDenied();
    }

    public object Import(CandidateBatch batch, string[] keys)
    {
        Ensure();
        var fresh = Fresh();
        if (keys == null || keys.Length is < 1 or > 100 || keys.Distinct().Count() != keys.Length)
            throw new IntegrationException("invalid_selection", "1–100 farklı ürün seç.", 400);
        if (!registry.Connections.TryGetValue(batch.StoreId, out var store) || Source(store) != batch.Source)
            throw new IntegrationException("source_changed", "Mağaza bağlantısı değişti. Listeyi yenile.", 409);
        var rows = keys.Select(k => batch.Rows.FirstOrDefault(r => r.Key == k) ?? throw new IntegrationException("invalid_selection", "Seçilen kayıt bu sayfada bulunamadı.", 400)).ToArray();
        return db.Modify(s =>
        {
            var result = ImportRows(s, store, rows, batch.ObservedAt);
            db.Event(s, "Kataloğa aktarıldı", $"{store.Settings.Name} · {result.Count} eşleştirme · merkezi stok ve fiyat korunur.");
            return result;
        });
    }

    private static List<object> ImportRows(Database s, ConnectionState store, CatalogCandidate[] rows, DateTimeOffset at)
    {
        var result = new List<object>();
        foreach (var r in rows)
        {
            if (r.Name.Length > 300 || r.Sku.Length > 100 || r.Barcode.Length > 100 || r.Description?.Length > 20000 || r.Images?.Count > 100 || r.Attributes?.Count > 100)
                throw new IntegrationException("invalid_product", "Mağaza ürün alanları izin verilen sınırı aşıyor.", 409);
            if (ProductionRules.CompositionHasPolyamide([r.Name, r.Category, .. (IEnumerable<string>?)r.Attributes?.Values ?? []]) || ProductionRules.TextHasPolyamide(r.Description))
                throw new IntegrationException("production_rule", $"{r.Name}: {ProductionRules.PolyamideMessage}", 422);
            var old = s.Catalog.Listings.GetValueOrDefault(r.Key);
            Product? p = old == null ? null : s.Products.FirstOrDefault(p => p.Id == old.ProductId);
            if (old != null && old.Source != Source(store))
                throw new IntegrationException("source_changed", "Kayıt farklı bir mağaza bağlantısına ait.", 409);
            if (p == null)
            {
                var matches = s.Products.Where(p => p.SourceMode == store.Settings.Mode && (r.Barcode.Length > 0 ? p.Barcode == r.Barcode : r.Sku.Length > 0 && p.Sku.Equals(r.Sku, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (matches.Length > 1)
                    throw new IntegrationException("ambiguous_identity", "Birden fazla ürün eşleşiyor; kimlikleri düzelt.", 409);
                p = matches.SingleOrDefault();
                if (p != null && p.SourceSystem != ProductOwnershipPolicy.ErpSource)
                    throw new IntegrationException("master_missing", "ERP ana ürün kaydı yok; mağaza verisi yalnızca eşleştirme için kullanılabilir.", 409);
                if (p == null && r.Sku.Length > 0)
                {
                    var skuMatch = s.Products.Where(p => p.SourceMode == store.Settings.Mode && p.Sku.Equals(r.Sku, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (skuMatch.Length > 0)
                    {
                        if (skuMatch.Length != 1 || r.Barcode.Length > 0 && skuMatch[0].Barcode.Length > 0 && skuMatch[0].Barcode != r.Barcode)
                            throw new IntegrationException("identity_conflict", "SKU aynı ancak barkod farklı. Otomatik birleştirme yapılmadı.", 409);
                        p = skuMatch[0];
                    }
                }
                if (p != null && p.SourceSystem != ProductOwnershipPolicy.ErpSource)
                    throw new IntegrationException("master_missing", "ERP ana ürün kaydı yok; mağaza verisi yalnızca eşleştirme için kullanılabilir.", 409);

                if (p == null)
                    throw new IntegrationException("master_missing", "Mağaza ürünü ERP ana verisi olmadan merkezi ürün oluşturamaz.", 409);
            }
            if (p.SourceSystem != ProductOwnershipPolicy.ErpSource)
                throw new IntegrationException("master_missing", "ERP ana ürün kaydı yok; mağaza verisi yalnızca eşleştirme için kullanılabilir.", 409);

            if (r.Name.Length == 0)
                throw new IntegrationException("invalid_product", "Ürün adı bildirilmedi.", 409);
            s.Catalog.Listings[r.Key] = new(r.Key, p.Id, store.Id, Source(store), r.ExternalId, r.Sku, r.Barcode, r.Name, r.Category, r.Brand, r.Stock, r.Price, r.Currency, r.VatIncluded, r.Status, at);
            RefreshChannels(s, p.Id);
            result.Add(new { key = r.Key, productId = p.Id });
        }

        return result;
    }

    private static void RefreshChannels(Database s, string id)
    {
        var p = s.Products.First(x => x.Id == id);
        p.Channels = s.Catalog.Listings.Values.Where(l => l.ProductId == id).Select(l => l.Source.Split('|')[1]).Distinct().Select(x => x switch
        {
            "trendyol" => "Trendyol",
            "hepsiburada" => "Hepsiburada",
            "amazon" => "Amazon",
            "boyner" => "Boyner",
            "ticimax" => "Ticimax",
            _ => x
        }).ToList();
    }

    public object Remap(CatalogRemap input)
    {
        Ensure();
        return db.Modify(s =>
        {
            var link = s.Catalog.Listings.GetValueOrDefault(input.ListingKey) ?? throw new IntegrationException("not_found", "Mağaza eşleştirmesi bulunamadı.", 404);
            var p = s.Products.FirstOrDefault(p => p.Id == input.ProductId && p.SourceSystem==ProductOwnershipPolicy.ErpSource) ?? throw new IntegrationException("not_found", "ERP ana ürünü bulunamadı.", 404);
            if (p.SourceMode != link.Source.Split('|')[0])
                throw new IntegrationException("mode_mismatch", "Demo ve gerçek ürünler aynı kayıtta birleştirilemez.", 409);
            s.Catalog.Listings[link.Key] = link with
            {
                ProductId = p.Id
            };
            RefreshChannels(s, p.Id);
            RefreshChannels(s, link.ProductId);
            db.Event(s, "Ürün eşleştirmesi değişti", link.Sku + " → " + p.Sku);
            return new
            {
                ok = true
            };
        });
    }

    public object Categories(CatalogQuery q)
    {
        Ensure();
        return db.CatalogRead(s =>
        {
            var rows = s.Products.Where(p=>p.SourceSystem==ProductOwnershipPolicy.ErpSource).GroupBy(p => p.Category).Where(g => IsClothingCategory(g.Key) && ListingFilters.Text(g.Key, q.Search)).OrderBy(g => g.Key).Select(g => new { category = g.Key, products = g.Count(), brands = g.Select(p => p.Brand).Where(b => b.Length > 0).Distinct().Take(10), bindings = s.Catalog.Categories.Values.Where(b => b.Category == g.Key).ToArray() }).ToArray();
            return ListingFilters.Page(rows, new ListingQuery(Page: q.Page, Size: q.Size));
        });
    }

    internal static bool IsClothingCategory(string category) =>
        category.StartsWith("Giyim", StringComparison.OrdinalIgnoreCase) ||
        category.Equals("Moda", StringComparison.OrdinalIgnoreCase) ||
        category.StartsWith("Tekstil /", StringComparison.OrdinalIgnoreCase);

    public object Bind(CategoryBinding input)
    {
        Ensure();
        if (string.IsNullOrWhiteSpace(input.Category) || input.Category.Length > 200 || string.IsNullOrWhiteSpace(input.ExternalCategoryId) || input.ExternalCategoryId.Length > 100 || input.ExternalCategoryName.Length > 300 || input.BrandId.Length > 100 || input.Attributes == null || input.Attributes.Count > 100 || input.Attributes.Any(a => a.Key.Length > 100 || a.Value.Length > 500) || !registry.Connections.ContainsKey(input.StoreId))
            throw new IntegrationException("invalid_mapping", "Kategori, mağaza ve kanal kategori numarasını kontrol et.", 400);
        return db.Modify(s =>
        {
            s.Catalog.Categories[Key(input.StoreId, input.Category)] = input;
            db.Event(s, "Kategori eşleştirildi", input.Category);
            return input;
        });
    }

    private bool RealWrites(ConnectionState c)
    {
        try
        {
            return configuration != null && configuration.GetValue<bool>("Synchronization:AllowRealWrites") && CredentialProfiles.Writes(CredentialProfiles.For(configuration, c.Settings), c.Settings);
        }
        catch (IntegrationException)
        {
            return false;
        }
    }

    public PublishLine[] Preview(string[] keys)
    {
        Ensure();
        var fresh = Fresh();
        if (keys == null || keys.Length is < 1 or > 100 || keys.Distinct().Count() != keys.Length)
            throw new IntegrationException("invalid_selection", "1–100 farklı mağaza kaydı seç.", 400);
        return db.CatalogRead(s => keys.Select(k =>
        {
            var l = s.Catalog.Listings.GetValueOrDefault(k) ?? throw new IntegrationException("not_found", "Eşleştirme bulunamadı.", 404);
            var p = s.Products.First(x => x.Id == l.ProductId);
            var c = registry.Connections.GetValueOrDefault(l.StoreId);
            var current = fresh(l);
            var reason = ProductionRules.CompositionHasPolyamide(p.Name, p.Category) ||
                ProductionRules.TextHasPolyamide(p.Description)
                ? ProductionRules.PolyamideMessage
                : "Use ERP pricing preview and approval; combined stock/price publishing is disabled.";
            return new PublishLine(k, p.Id, p.Version, l.StoreId, p.Name, current.Stock, current.Price,
                null, null, false, reason);
        }).ToArray());
    }

    public object Publish(CatalogPublish input)
    {
        if(string.IsNullOrWhiteSpace(input.RequestId)||input.Keys==null||input.Versions==null)throw new IntegrationException("invalid_request","İşlem kimliği, ilanlar ve sürümler gerekli.",400);
        throw new IntegrationException("pricing_workflow_required","ERP fiyat listesi, fark önizlemesi ve onay akışı gerekli.",409);
    }

    public object Jobs(CatalogQuery q)
    {
        Ensure();
        return db.CatalogRead(s => ListingFilters.Page(s.Catalog.Jobs.Where(j => ListingFilters.Exact(j.State, q.Status)).OrderByDescending(j => j.CreatedAt), new ListingQuery(Page: q.Page, Size: q.Size)));
    }

    public async Task Tick(CancellationToken ct)
    {
        if (initialized == 0)
            return;
        var jobs = db.CatalogRead(s => s.Catalog.Jobs.Where(j => j.State == "pending").Take(10).Select(j => JsonSerializer.Deserialize<CatalogJob>(JsonSerializer.Serialize(j))!).ToArray());
        foreach (var job in jobs)
            foreach (var t in job.Targets.Where(t => t.State is "pending" or "queued"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if(t.Price>0)throw new IntegrationException("pricing_workflow_required","Legacy price queue disabled; create ERP price approval.",409);
                    if (!registry.Connections.TryGetValue(t.StoreId, out var store) || Source(store) != t.Source)
                        throw new ArgumentException("Mağaza bağlantısı değişti.");
                    if (store.Settings.Mode == "real")
                    {
                        var queue = services!.GetRequiredService<MarketPilot.Web.Synchronization.StockOutbox>();
                        if (t.State == "pending")
                        {
                            var stale = db.CatalogRead(s => s.Products.FirstOrDefault(p => p.Id == t.ProductId)?.Version != t.Version || s.Catalog.Listings.GetValueOrDefault(t.Key)?.ProductId != t.ProductId);
                            if (stale)
                                throw new ArgumentException("Sıraya alındıktan sonra ürün veya eşleştirme değişti. Yeniden önizle.");
                            var sent = queue.Enqueue(store, t.Barcode.Length > 0 ? t.Barcode : "sku:" + t.NativeSku, (int)t.Stock, t.Price, "Ortak katalog", nativeSku: t.NativeSku, requestId: "catalog-" + job.Id + "-" + t.Key);
                            t.DemoJobId = sent.Id;
                            t.State = "queued";
                        }

                        var result = queue.Find(t.DemoJobId!);
                        if (result?.State == "completed")
                            t.State = "completed";
                        else if (result == null || result.State is "failed" or "blocked" or "superseded")
                        {
                            t.State = "failed";
                            t.Error = result?.Error ?? "Kalıcı gönderim kaydı bulunamadı.";
                        }
                    }
                    else
                    {
                        if (t.State == "pending")
                        {
                            var stale = db.CatalogRead(s => s.Products.FirstOrDefault(p => p.Id == t.ProductId)?.Version != t.Version || s.Catalog.Listings.GetValueOrDefault(t.Key)?.ProductId != t.ProductId);
                            if (stale)
                                throw new ArgumentException("Sıraya alındıktan sonra ürün veya eşleştirme değişti. Yeniden önizle.");
                            var sent = demo.Enqueue(store.Settings, new("stock", "catalog-" + job.Id + "-" + Key(t.StoreId, t.Key)[^16..], t.Barcode, (int)t.Stock, t.Price));
                            t.DemoJobId = sent.Id;
                            t.State = "queued";
                        }

                        var result = demo.Snapshot(store.Settings).Jobs.FirstOrDefault(j => j.Id == t.DemoJobId);
                        if (result?.State == "completed")
                            t.State = "completed";
                        else if (result == null)
                        {
                            t.State = "failed";
                            t.Error = "Mağaza işlem kaydı bulunamadı; yeniden önizle.";
                        }
                        else if (result.State == "failed")
                        {
                            t.State = "failed";
                            t.Error = result.Error;
                        }
                    }
                }
                catch (Exception e)when (e is ArgumentException or InvalidOperationException or IntegrationException)
                {
                    t.State = "failed";
                    t.Error = e.Message;
                }

                db.Modify(s =>
                {
                    var saved = s.Catalog.Jobs.Single(j => j.Id == job.Id);
                    var target = saved.Targets.Single(v => v.Key == t.Key);
                    target.State = t.State;
                    target.DemoJobId = t.DemoJobId;
                    target.Error = t.Error;
                    saved.State = saved.Targets.Any(v => v.State is "pending" or "queued") ? "pending" : saved.Targets.Any(v => v.State == "failed") ? "failed" : "completed";
                    return true;
                });
            }

        await Task.CompletedTask;
    }
}

public sealed class CatalogWorker(CatalogHub hub, ILogger<CatalogWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await hub.Tick(stoppingToken);
                }
                catch (Exception e)when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    log.LogError("Katalog işlem sırası tamamlanamadı: {Type}", e.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
