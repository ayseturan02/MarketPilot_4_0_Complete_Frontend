using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketPilot.Web.Catalog;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.CentralStock;
using MarketPilot.Web.Integrations.Demo;

namespace MarketPilot.Web.Workspace;

public sealed class WorkspaceHub(DemoStore db, OperationsHub operations, IntegrationRegistry registry, DemoWorkspace demo, CentralStockService stock, MarketPilot.Web.Enterprise.EnterpriseSync enterprise)
{
    public static readonly string[] Modules = ["picking", "labels", "tracking", "returns", "refunds", "invoices", "invoice_drafts", "efatura", "invoice_archive", "invoice_matching", "credit_notes", "payments", "reconciliation", "marketplace_rules", "messages"];
    private static string Fingerprint<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private string Source(Order order) => order.StoreId.Length == 0 ? "local" : registry.Connections.TryGetValue(order.StoreId, out var store) ? CatalogHub.Source(store) : "missing";
    private static IntegrationException Error(string message, int status = 400) => new("workspace_validation", message, status);
    public Order FindOrder(string id) => operations.AllOrders().FirstOrDefault(o => o.Id == id) ?? throw Error("Sipariş bulunamadı.", 404);

    public object OrderDetail(string id)
    {
        var order = FindOrder(id);
        var documents = db.CatalogRead(s => s.WorkspaceRecords.Where(r => r.OrderId == id && r.Source == Source(order)).Select(r => new { r.Id, r.Kind, r.Number, r.Status, r.Amount, r.Currency, r.Version }).ToArray());
        var shipments = operations.Shipments(new(Size: int.MaxValue)).Items.Where(sh => sh.StoreId == order.StoreId && sh.OrderId == (order.StoreId.Length == 0 ? order.Id : order.ChannelOrderNumber)).ToArray();
        return new { order, documents, shipments };
    }

    public ListingPage<WorkspaceRow> Search(string module, ListingQuery query)
    {
        if (!Modules.Contains(module)) throw Error("Çalışma alanı bulunamadı.", 404);
        IEnumerable<WorkspaceRow> rows = Rows(module);
        rows = rows.Where(r => ListingFilters.Text(string.Join(" ", r.Reference, r.Title, r.Channel, r.Detail, r.OrderId), query.Search) && ListingFilters.Exact(r.Status, query.Status) && ListingFilters.Exact(r.Channel, query.Channel));
        rows = query.Sort == "oldest" ? rows.OrderBy(r => r.Date).ThenBy(r => r.Id) : rows.OrderByDescending(r => r.Date).ThenBy(r => r.Id);
        return ListingFilters.Page(rows, query);
    }

    private IEnumerable<WorkspaceRow> Rows(string module)
    {
        var orders = operations.AllOrders();
        var byId = orders.ToDictionary(o => o.Id);
        var records = db.CatalogRead(s => JsonSerializer.Deserialize<WorkspaceRecord[]>(JsonSerializer.Serialize(s.WorkspaceRecords))!);
        var local = records.Where(r => byId.TryGetValue(r.OrderId, out var order) && r.Source == Source(order)).Select(r => {
            var order = byId[r.OrderId];
            return new WorkspaceRow(r.Id, r.Kind, r.Number, order.Customer, order.Channel, order.StoreId, order.Id,
                order.Lines.Select(l => l.ProductId).FirstOrDefault(id => id != null) ?? "", r.Status,
                r.Kind == "invoice" && r.Status == "draft" ? order.TotalKnown ? order.Total : null : r.Amount,
                r.Currency, r.CreatedAt, r.Note, r.Status is "draft" or "open" ? "record" : "", r.Version, Origin: "record", Source: r.Source);
        }).ToArray();

        WorkspaceRow OrderRow(Order o, string kind, string status, string action = "", string detail = "") => new(o.Id, kind,
            o.ChannelOrderNumber.Length > 0 ? o.ChannelOrderNumber : o.Id, o.Customer, o.Channel, o.StoreId, o.Id,
            o.Lines.FirstOrDefault(l => l.ProductId != null)?.ProductId ?? "", status, o.TotalKnown ? o.Total : null, o.Currency, o.Date, detail, action, o.Version, o.PackageId, o.SourceMode, Source(o));
        if (module == "picking") return orders.Where(o => o.Status is "Yeni" or "Hazırlanıyor").Select(o => OrderRow(o, "order", o.Status, "advance", o.Product)).ToArray();
        if (module is "tracking" or "labels") return operations.Shipments(new(Size: int.MaxValue)).Items.Select(sh => {
            var order = orders.FirstOrDefault(o => o.StoreId == sh.StoreId && (sh.StoreId.Length == 0 ? o.Id : o.ChannelOrderNumber) == sh.OrderId);
            return new WorkspaceRow(sh.Id, "shipment", sh.TrackingNumber, sh.Carrier, order?.Channel ?? "", sh.StoreId, order?.Id ?? "", "", sh.Status, null, "", sh.CreatedAt, sh.OrderId, "shipment", Origin: order?.SourceMode ?? "local");
        }).ToArray();

        var invoices = orders.Where(o => !string.IsNullOrWhiteSpace(o.Invoice?.Number)).Select(o => OrderRow(o, "invoice", o.Invoice!.Status, detail: o.Invoice.Number) with { Id = "native-invoice:" + o.Id, Reference = o.Invoice!.Number }).Concat(local.Where(r => r.Kind == "invoice")).ToArray();
        if (module is "invoices" or "efatura") return invoices.Concat(local.Where(r => r.Kind == "credit_note"));
        if (module == "invoice_drafts") return invoices.Concat(local.Where(r => r.Kind == "credit_note")).Where(r => r.Origin == "record" && r.Status == "draft");
        if (module == "invoice_archive") return invoices.Concat(local.Where(r => r.Kind == "credit_note")).Where(r => r.Origin != "record" || r.Status == "archived");
        if (module == "invoice_matching") return orders.Select(o => {
            var invoice = invoices.FirstOrDefault(i => i.OrderId == o.Id && i.Status != "cancelled");
            return OrderRow(o, "matching", invoice is null ? "Belge bekleniyor" : "Eşleşti", detail: invoice?.Reference ?? "Siparişe bağlı belge yok.");
        }).ToArray();
        if (module == "credit_notes") return local.Where(r => r.Kind == "credit_note");
        if (module == "marketplace_rules") return new[] {
            new WorkspaceRow("stock-safety", "rule", "Güvenlik stoğu", "Stok kaynağı", "Tüm eşleşmiş mağazalar", "", "", "", "Uygulanıyor", null, "", null, (enterprise.Managed?enterprise.Plan.SafetyStock:stock.Settings.SafetyStock) + " adet", "rules"),
            new WorkspaceRow("stock-scheduler", "rule", "Kaynak stok zamanlayıcısı", "Stok kaynağı", "Tüm eşleşmiş mağazalar", "", "", "", (enterprise.Managed?enterprise.Plan.Enabled:stock.Settings.AutoSync) ? "Açık" : "Kapalı", null, "", null, (enterprise.Managed?enterprise.Plan.Schedules.First(s=>s.Task=="stock").IntervalSeconds:stock.Settings.IntervalSeconds) + " saniye", "rules")
        };

        var sources = registry.Connections.Values.Where(s => s.Settings.Mode == "demo").GroupBy(CatalogHub.Source).Select(g => g.First()).Select(s => (Store: s, State: demo.Snapshot(s.Settings))).ToArray();
        Order? Related(string storeId, string number) => orders.FirstOrDefault(o => o.StoreId == storeId && o.ChannelOrderNumber == number);
        if (module == "messages") return sources.SelectMany(s => s.State.Questions.Select(q => new WorkspaceRow(CatalogHub.Key(s.Store.Id, "question:" + q.Id), "question", q.Id, q.ProductName,
            OperationsHub.Name(s.Store.Settings.Marketplace), s.Store.Id, "", "", q.Status, null, "", q.CreationDate,
            q.Text + (q.Answer is null ? "" : " · Yanıt: " + q.Answer.Text), q.Status == "WAITING_FOR_ANSWER" ? "answer" : "", NativeReference: q.Id, Origin: "demo", Source: CatalogHub.Source(s.Store)))).ToArray();
        var claims = sources.SelectMany(s => s.State.Claims.Select(c => {
            var order = Related(s.Store.Id, c.OrderNumber);
            return new WorkspaceRow(CatalogHub.Key(s.Store.Id, "claim:" + c.Id), "claim", c.Id, c.CustomerName, OperationsHub.Name(s.Store.Settings.Marketplace),
                s.Store.Id, order?.Id ?? "", order?.Lines.FirstOrDefault(l => l.Barcode == c.Barcode)?.ProductId ?? "", c.Status, null, order?.Currency ?? "", c.ClaimDate,
                c.OrderNumber + " · " + c.Reason, c.Status == "İnceleniyor" ? "approve" : "", NativeReference: c.Id, Origin: "demo", Source: CatalogHub.Source(s.Store));
        })).ToArray();
        if (module == "returns") return claims.Concat(local.Where(r => r.Kind is "return" or "refund")).Concat(orders.Where(o => o.Status is "İptal" or "İade").Select(o => OrderRow(o, "order-return", o.Status))).ToArray();
        var finance = sources.SelectMany(s => s.State.Finance.Select(f => {
            var order = Related(s.Store.Id, f.OrderNumber);
            return new WorkspaceRow(CatalogHub.Key(s.Store.Id, "finance:" + f.Id), f.TransactionType, f.Id, f.Description,
                OperationsHub.Name(s.Store.Settings.Marketplace), s.Store.Id, order?.Id ?? "", "", "Kayıtlı işlem", f.Amount, order?.Currency ?? "", f.TransactionDate, f.OrderNumber, Origin: "demo");
        })).ToArray();
        if (module == "refunds") return finance.Where(f => f.Kind == "Return").Concat(local.Where(r => r.Kind == "refund")).ToArray();
        if (module == "payments") return finance;
        if (module == "reconciliation") return orders.Select(o => {
            var entries = finance.Where(f => f.OrderId == o.Id && f.Currency == o.Currency).ToArray();
            return OrderRow(o, "reconciliation", entries.Length == 0 ? "Finans kaydı bekleniyor" : "Kayıtlar bağlı",
                detail: entries.Length == 0 ? "Tahsilat veya kesinti kaydı bulunmuyor." : $"{entries.Length} işlem · net hareket {entries.Sum(f => f.Amount)} {o.Currency}");
        }).ToArray();
        return [];
    }

    public WorkspaceRecord CreateRecord(WorkspaceRecordDraft input)
    {
        if (!new[] { "invoice", "return", "refund", "credit_note" }.Contains(input.Kind) || string.IsNullOrWhiteSpace(input.RequestId) || input.RequestId.Length > 100 || input.Number?.Length > 80 || input.Note?.Length > 2000)
            throw Error("Belge türü, işlem kimliği veya açıklama geçersiz.");
        var fingerprint = Fingerprint(input);
        var order = FindOrder(input.OrderId);
        if (order.StoreId.Length > 0 && input.Source != Source(order)) throw Error("Siparişin mağaza kaynağı değişti; seçimi yenileyin.",409);
        if (input.Kind == "invoice" && order.Status == "İptal") throw Error("İptal siparişe yeni fatura taslağı açılamaz.");
        if (input.Kind is "refund" or "credit_note" && (!order.TotalKnown || input.Amount is null or <= 0 || input.Amount > order.Total)) throw Error("İade tutarı bilinen sipariş tutarını aşamaz ve pozitif olmalı.");
        return db.Modify(s => {
            var previous = s.WorkspaceRecords.FirstOrDefault(r => r.RequestId == input.RequestId);
            if (previous is not null) { if (previous.Fingerprint != fingerprint) throw Error("İşlem kimliği farklı bir kayıt için kullanılmış.", 409); return previous; }
            if (input.Kind == "invoice" && s.WorkspaceRecords.Any(r => r.Kind == "invoice" && r.OrderId == order.Id && r.Source == Source(order) && r.Status != "cancelled")) throw Error("Bu siparişin yerel fatura taslağı zaten var.", 409);
            if (input.Kind is "refund" or "credit_note" && s.WorkspaceRecords.Where(r => r.Kind == input.Kind && r.OrderId == order.Id && r.Status != "cancelled").Sum(r => r.Amount ?? 0) + input.Amount > order.Total) throw Error("Toplam iade talebi sipariş tutarını aşamaz.");
            var record = new WorkspaceRecord { Kind = input.Kind, OrderId = order.Id, Source = Source(order), Number = string.IsNullOrWhiteSpace(input.Number) ? "YEREL-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant() : input.Number.Trim(),
                Note = input.Note?.Trim() ?? "", Status = input.Kind is "invoice" or "credit_note" ? "draft" : "open", Amount = input.Kind is "refund" or "credit_note" ? input.Amount : order.TotalKnown ? order.Total : null, Currency = order.Currency, RequestId = input.RequestId, Fingerprint = fingerprint };
            s.WorkspaceRecords.Insert(0, record); db.Event(s, "Ortak işlem kaydı oluşturuldu", record.Number + " · " + (order.ChannelOrderNumber.Length > 0 ? order.ChannelOrderNumber : order.Id)); return record;
        });
    }

    public object Act(string module, string id, WorkspaceAction input)
    {
        if (string.IsNullOrWhiteSpace(input.RequestId) || input.RequestId.Length > 100) throw Error("İşlem kimliği gerekli.");
        var actionKey = Fingerprint(new { module, id, input });
        var applied = db.CatalogRead(s => s.WorkspaceActions.GetValueOrDefault(input.RequestId));
        if (applied != null) { if(applied != actionKey) throw Error("İşlem kimliği farklı işlemde kullanılmış.",409);return new {ok=true}; }
        var row = Rows(module).FirstOrDefault(r => r.Id == id) ?? throw Error("Kayıt artık bu ekranda bulunmuyor; yenileyin.", 404);
        if(row.StoreId.Length > 0 && input.Source != (row.Source.Length > 0 ? row.Source : Source(FindOrder(row.OrderId)))) throw Error("Mağaza kaynağı değişti; kaydı yenileyin.",409);
        if (row.Origin == "record") return db.Modify(s => {
            if(s.WorkspaceActions.TryGetValue(input.RequestId,out var saved)){if(saved!=actionKey)throw Error("İşlem kimliği farklı işlemde kullanılmış.",409);return new {ok=true};}
            var record = s.WorkspaceRecords.First(r => r.Id == id);
            if (input.Version != record.Version) throw Error("Kayıt başka bir ekranda değişti; yenileyin.", 409);
            var next = input.Action switch { "archive" when record.Status == "draft" => "archived", "close" when record.Status == "open" => "closed", "cancel" when record.Status is "draft" or "open" => "cancelled", _ => throw Error("Bu durum değişikliği uygulanamaz.") };
            record.Status = next; record.Version++; s.WorkspaceActions[input.RequestId]=actionKey; db.Event(s, "Ortak işlem kaydı güncellendi", record.Number + " · " + next); return new { ok = true };
        });
        if (row.Kind == "order" && row.StoreId.Length == 0 && input.Action == "advance") return db.Modify(s => {
            if(s.WorkspaceActions.TryGetValue(input.RequestId,out var saved)){if(saved!=actionKey)throw Error("İşlem kimliği farklı işlemde kullanılmış.",409);return new {ok=true};}
            var order = s.Orders.First(o => o.Id == id); if (input.Version != order.Version) throw Error("Sipariş başka bir ekranda değişti; yenileyin.", 409);
            LocalOrderStock.ChangeStatus(s, order, order.Status == "Yeni" ? "Hazırlanıyor" : "Kargolandı"); s.WorkspaceActions[input.RequestId]=actionKey; db.Event(s, "Sipariş durumu", order.Id + " · " + order.Status); return new { ok = true };
        });
        if (!registry.Connections.TryGetValue(row.StoreId, out var store) || store.Settings.Mode != "demo") throw Error("Bu kayıt için işlem ilgili mağaza servisinden yönetilmelidir.", 409);
        var type = input.Action switch { "advance" when row.Kind == "order" => "order-next", "answer" when row.Kind == "question" => "answer", "approve" when row.Kind == "claim" => "claim-approve", _ => throw Error("İşlem bu kayıt için desteklenmiyor.") };
        return demo.Enqueue(store.Settings, new(type, input.RequestId, Reference: row.NativeReference, Text: input.Text));
    }

    public Order CreateOrder(LocalOrderDraft input)
    {
        if (string.IsNullOrWhiteSpace(input.Customer) || input.Customer.Trim().Length > 200 || string.IsNullOrWhiteSpace(input.RequestId) || input.RequestId.Length > 100 || input.Lines is null || input.Lines.Length is < 1 or > 100 || input.Lines.Any(l => l is null || l.Quantity is < 1 or > 100000) || input.Channel?.Length > 100 || Local.RetiredChannelCleanup.IsRetired(input.Channel)) throw Error("Müşteri, işlem kimliği, ürün ve miktarları kontrol edin.");
        var fingerprint = Fingerprint(input);
        return db.Modify(s => {
            var previous = s.Orders.FirstOrDefault(o => o.RequestId == input.RequestId);
            if (previous != null) { if (previous.RequestFingerprint != fingerprint) throw Error("İşlem kimliği farklı siparişte kullanılmış.", 409); return previous; }
            var groups = input.Lines.GroupBy(l => l.ProductId).Select(g => (Id:g.Key, Quantity:g.Sum(l => l.Quantity))).ToArray();
            InventoryHold? reservation=null;
            if(input.ReservationId!=null){reservation=s.Operations.Holds.FirstOrDefault(h=>h.Id==input.ReservationId&&h.StoreId.Length==0)??throw Error("Rezervasyon bulunamadı.",404);if(!OperationalInventory.Active(reservation)||groups.Length!=1||groups[0].Id!=reservation.ProductId||groups[0].Quantity!=reservation.Quantity)throw Error("Sipariş, etkin rezervasyonun ürün ve miktarıyla aynı olmalı.",409);}
            var lines = groups.Select(g => {
                var product = s.Products.FirstOrDefault(p => p.Id == g.Id) ?? throw Error("Sipariş ürünü bulunamadı.", 404);
                if(product.SourceSystem!=ProductOwnershipPolicy.ErpSource)throw Error("Sipariş yalnızca ERP ana verisiyle eşleştirilmiş ürünleri kullanabilir.",409);
                if (product.Status != "Aktif" || !product.StockKnown || !product.PriceKnown || OperationalInventory.Available(s,product,reservation?.Id) < g.Quantity || product.Price <= 0) throw Error("Ürünün aktif, fiyatlı ve yeterli kullanılabilir stoğu olmalı.");
                return (Product:product, g.Quantity);
            }).ToArray();
            if (lines.Select(l => l.Product.Currency).Distinct().Count() != 1) throw Error("Aynı siparişte farklı para birimleri kullanılamaz.");
            var order = new Order { Id = "YEREL-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(), Customer = input.Customer.Trim(), Channel = string.IsNullOrWhiteSpace(input.Channel) ? "Yerel satış" : input.Channel.Trim(), SourceMode = "local", Currency = lines[0].Product.Currency,
                Product = string.Join(" · ", lines.Select(l => l.Product.Name)), Quantity = lines.Sum(l => l.Quantity), Total = lines.Sum(l => l.Product.Price * l.Quantity), StockManaged = false, RequestId = input.RequestId, RequestFingerprint = fingerprint,
                Lines = lines.Select(l => new OperationLine(l.Product.Name, l.Product.Sku, l.Product.Barcode, l.Quantity, l.Product.Price * l.Quantity, l.Product.Id)).ToArray() };
            if(reservation!=null){reservation.State="consumed";OperationalInventory.Touch(s,reservation.ProductId);}
            s.Orders.Insert(0, order); db.Event(s, "Yerel sipariş oluşturuldu", order.Id + " · stok kaynağı değiştirilmedi"); return order;
        });
    }
}

public static class LocalOrderStock
{
    public static void ChangeStatus(Database data, Order order, string status)
    {
        var consume = status is not ("İptal" or "İade");
        if (order.StockManaged && consume != order.StockApplied)
            throw new ArgumentException("Sipariş durumu stok bakiyesini değiştiremez; stok ve rezervasyon WMS/depo kaynağından alınmalıdır.");
        order.Status = status; order.Version++;
    }
}
