using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketPilot.Web.Catalog;
using MarketPilot.Web.Enterprise;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Local;

namespace MarketPilot.Web.Workspace;
public sealed class OperationalService(DemoStore db,CatalogHub catalog,OperationsHub orders,IntegrationRegistry registry,EnterpriseBridge bridge,IConfiguration config,IServiceProvider services)
{
    private readonly SemaphoreSlim workerGate=new(1,1);
    private string lastHoldHash="";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static string Hash(object value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,Json))));
    private static IntegrationException Error(string message,int status=409)=>new("operation_blocked",message,status);
    private static string OrderHash(Order o)=>Hash(new{o.Id,o.SourceIdentity,o.Status,o.Lines,o.Total,o.Currency});
    private string Account(Order order)=>order.StoreId.Length==0?"":CredentialProfiles.Identity(config,registry.Connections.GetValueOrDefault(order.StoreId)?.Settings??throw Error("Mağaza bulunamadı."));
    private static bool CurrencyKnown(string value)=>value.Length==3&&value.All(c=>c is >='A' and <='Z');
    private Order Order(string id)=>orders.AllOrders().FirstOrDefault(o=>o.Id==id)??throw Error("Sipariş bulunamadı.",404);
    public object Status()=>db.CatalogRead(s=>JsonSerializer.SerializeToElement(new {policy=s.Operations.Policy,lastScanAt=s.Operations.LastScanAt,s.Operations.FindingCount,findings=s.Operations.Findings.Take(100),holds=s.Operations.Holds.OrderByDescending(h=>h.CreatedAt).Take(100),requests=s.Operations.Requests.OrderByDescending(r=>r.CreatedAt).Take(100),deliveries=s.Operations.Deliveries.TakeLast(100).Reverse().Select(d=>new{d.Id,d.Connector,d.Kind,d.Reference,d.State,d.Error,d.Attempts,d.ExternalReference,d.NextAttemptAt}),settlements=s.Operations.Settlements.TakeLast(100).Reverse(),inventory=s.Products.Where(p=>p.StockKnown).Take(100).Select(p=>new{p.Id,p.Name,p.Sku,p.Stock,reserved=OperationalInventory.Reserved(s,p.Id),available=OperationalInventory.Available(s,p)}),connectors=new {notification=bridge.Writes("notification"),documents=bridge.Writes("documents"),payments=bridge.Writes("payments")}},Json));
    public void Configure(OperationalPolicy input)
    {
        if(input.SourceStockKind is not ("physical" or "available")||input.ScanIntervalSeconds is <30 or >86400||input.StaleMinutes is <1 or >10080||input.ReservationsEnabled&&input.SourceStockKind!="physical")throw Error("Rezervasyon için kaynak rezervasyonsuz fiziksel stok olmalı; tarama 30–86400 saniye, eskime süresi 1–10080 dakika olmalı.",400);
        if(input.ReservationsEnabled&&!services.GetRequiredService<EnterpriseSync>().Managed)throw Error("Önce Senkronizasyon Merkezi’nde ortak eşitleme planını kaydedin.");
        db.Modify(s=>{
            if(input.SourceStockKind!=s.Operations.Policy.SourceStockKind&&s.Operations.Holds.Any(OperationalInventory.Active))throw Error("Etkin rezervasyon varken kaynak stok türü değiştirilemez.");
            if(s.Operations.Policy.ReservationsEnabled&&!input.ReservationsEnabled&&s.Operations.Holds.Any(OperationalInventory.Active))throw Error("Etkin rezervasyonlar varken rezervasyon yönetimi kapatılamaz.");
            s.Operations.Policy=input;
            if(input.ReservationsEnabled)foreach(var row in s.Catalog.Orders.Values)if(registry.Connections.TryGetValue(row.StoreId,out var store)&&CatalogHub.Source(store)==row.Source&&CredentialProfiles.Identity(config,store.Settings)==row.AccountIdentity)OperationalInventory.Observe(s,store,[row.Order],row.AccountIdentity??"");
            db.Event(s,"Operasyon kuralları kaydedildi","Stok rezervasyonu, fark taraması ve bildirim tercihleri güncellendi.");return true;
        });
    }
    public InventoryHold Reserve(HoldDraft input)
    {
        if(string.IsNullOrWhiteSpace(input.RequestId)||input.RequestId.Length>100||input.Quantity is <1 or >1000000||input.Minutes is <1 or >1440)throw Error("Rezervasyon işlem kimliği, miktarı veya süresi geçersiz.",400);
        var fingerprint=Hash(input);
        return db.Modify(s=>{
            var old=s.Operations.Holds.FirstOrDefault(h=>h.RequestId==input.RequestId);if(old!=null){if(old.Fingerprint!=fingerprint)throw Error("İşlem kimliği farklı rezervasyonda kullanılmış.");return old;}
            if(!s.Operations.Policy.ReservationsEnabled)throw Error("Rezervasyon yönetimi açık değil.");
            if(s.Operations.Holds.Count>=10000)throw Error("Rezervasyon kayıt sınırı dolu.");
            var p=s.Products.FirstOrDefault(p=>p.Id==input.ProductId)??throw Error("Ürün bulunamadı.",404);
            if(p.Status!="Aktif"||!p.StockKnown||OperationalInventory.Available(s,p)<input.Quantity)throw Error("Yeterli kullanılabilir stok yok.");
            var hold=new InventoryHold{RequestId=input.RequestId,Fingerprint=fingerprint,ProductId=p.Id,Quantity=input.Quantity,ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(input.Minutes)};s.Operations.Holds.Add(hold);OperationalInventory.Touch(s,p.Id);return hold;
        });
    }
    public void Release(string id)=>db.Modify(s=>{var h=s.Operations.Holds.FirstOrDefault(h=>h.Id==id)??throw Error("Rezervasyon bulunamadı.",404);if(h.StoreId.Length>0)throw Error("Mağaza rezervasyonu sipariş durumu veya imzalı stok kaynağı onayıyla çözülür.");if(h.State=="held"){h.State="released";OperationalInventory.Touch(s,h.ProductId);}return true;});
    public SettlementRecord Settle(SettlementDraft input)
    {
        if(string.IsNullOrWhiteSpace(input.RequestId)||input.RequestId.Length>100||string.IsNullOrWhiteSpace(input.ExternalReference)||input.ExternalReference.Length>150||new[]{input.Commission,input.Shipping,input.Other,input.Refund}.Any(v=>v<0||v>100000000)||input.NetPaid is < -100000000 or >100000000)throw Error("Finans işlem kimliği, referansı veya tutarları geçersiz.",400);
        input=input with{RequestId=input.RequestId.Trim(),ExternalReference=input.ExternalReference.Trim(),Currency=(input.Currency??"").Trim().ToUpperInvariant()};
        var order=Order(input.OrderId);if(!CurrencyKnown(order.Currency)||!order.TotalKnown||order.Currency!=input.Currency||input.Refund>order.Total)throw Error("Finans kaydı bilinen sipariş tutarı ve aynı para birimiyle eşleşmeli.");var fingerprint=Hash(input with{RequestId=""});
        return db.Modify(s=>{
            var old=s.Operations.Settlements.FirstOrDefault(r=>r.RequestId==input.RequestId||r.StoreId==order.StoreId&&r.Source==order.SourceIdentity&&r.ExternalReference==input.ExternalReference);if(old!=null){if(old.Fingerprint!=fingerprint)throw Error("Finans referansı veya işlem kimliği farklı içerikle kullanılmış.");return old;}
            if(s.Operations.Settlements.Count>=10000)throw Error("Finans kayıt sınırı dolu.");
            var fees=input.Commission+input.Shipping+input.Other;var expected=order.Total-fees-input.Refund;
            var record=new SettlementRecord(input.RequestId,fingerprint,order.Id,order.StoreId,order.SourceIdentity,input.ExternalReference,input.Currency,order.Total,fees,input.Refund,expected,input.NetPaid,input.NetPaid-expected,DateTimeOffset.UtcNow);s.Operations.Settlements.Add(record);return record;
        });
    }
    public void Cancel(string id)=>db.Modify(s=>{
        var r=s.Operations.Requests.SingleOrDefault(r=>r.Id==id)??throw Error("Talep bulunamadı.",404);if(r.State=="cancelled")return true;
        if(r.State is "completed" or "failed"||s.Operations.Deliveries.Any(d=>d.Reference==id&&d.Attempts>0))throw Error("İşlenmiş veya servise gönderilmiş talep bu ekrandan iptal edilemez.");
        foreach(var d in s.Operations.Deliveries.Where(d=>d.Reference==id))d.State="cancelled";r.State="cancelled";return true;
    });
    private static void Quantities(Database s,Order order,OperationQuantity[] lines,string? except=null)
    {
        if(lines.Length is <1 or >1000||lines.Any(l=>l==null||string.IsNullOrWhiteSpace(l.LineId)||l.LineId.Length>200||l.Quantity is <1 or >1000000)||lines.Select(l=>l.LineId).Distinct().Count()!=lines.Length)throw Error("Sipariş satırı ve miktarları geçersiz.",400);
        foreach(var l in lines)
        {
            var matches=order.Lines.Where(row=>OperationalInventory.LineId(row)==l.LineId).ToArray();
            var waiting=s.Operations.Requests.Where(r=>r.OrderId==order.Id&&r.Id!=except&&(r.Kind is "cancel" or "return")&&(r.State is "awaiting-receipt" or "queued")).SelectMany(r=>r.Lines).Where(row=>row.LineId==l.LineId).Sum(row=>row.Quantity);
            if(matches.Length!=1||l.Quantity>matches[0].Quantity-waiting)throw Error("Miktar, siparişin işlem yapılabilir satır miktarını aşıyor.");
        }
    }
    public OperationalRequest Prepare(OperationDraft input)
    {
        if(input.Kind is not ("cancel" or "return" or "split" or "invoice" or "refund")||string.IsNullOrWhiteSpace(input.RequestId)||input.RequestId.Length>100||input.Note?.Length>2000)throw Error("İşlem türü, kimliği veya açıklaması geçersiz.",400);
        if(input.Kind!="refund"&&input.Amount!=null)throw Error("Tutar yalnızca geri ödeme işleminde girilebilir.",400);
        var order=Order(input.OrderId);var fingerprint=Hash(input);
        return db.Modify(s=>{
            var old=s.Operations.Requests.FirstOrDefault(r=>r.RequestId==input.RequestId);if(old!=null){if(old.Fingerprint!=fingerprint)throw Error("İşlem kimliği farklı taleple kullanılmış.");return old;}
            if(input.Kind is "invoice" or "split"&&s.Operations.Requests.Any(r=>r.OrderId==order.Id&&r.Kind==input.Kind&&r.State is not ("cancelled" or "stale" or "failed")))throw Error("Bu sipariş için aynı türde etkin bir talep zaten var.");
            if(s.Operations.Requests.Count>=5000)throw Error("Talep kayıt sınırı dolu.");
            var lines=input.Lines??[];var packages=input.Packages??[];
            if(input.Kind is "invoice" or "refund"&&lines.Length>0)Quantities(s,order,lines);
            if(input.Kind=="invoice"&&(!order.TotalKnown||!CurrencyKnown(order.Currency)||order.Status is "İptal" or "İade"))throw Error("Belge için geçerli, tutarı ve para birimi bilinen sipariş gerekir.");
            if(input.Kind is "cancel" or "return")Quantities(s,order,lines);
            if(input.Kind=="cancel"&&order.Status is not ("Yeni" or "Hazırlanıyor"))throw Error("Yalnızca sevk edilmemiş sipariş kalemleri iptal edilebilir.");
            if(input.Kind=="return"&&order.Status is not ("Kargolandı" or "Tamamlandı"))throw Error("İade için sevk edilmiş veya teslim edilmiş sipariş gerekir.");
            if(input.Kind=="split")
            {
                if(order.Status is not ("Yeni" or "Hazırlanıyor")||packages.Length is <2 or >50||packages.Any(p=>p==null||string.IsNullOrWhiteSpace(p.Code)||p.Code.Length>100||p.Lines==null||p.Lines.Length==0)||packages.Select(p=>p.Code).Distinct().Count()!=packages.Length)throw Error("Paket kodları ve sipariş durumu bölmeye uygun değil.",400);
                foreach(var p in packages)Quantities(s,order,p.Lines);
                var allocated=packages.SelectMany(p=>p.Lines).GroupBy(l=>l.LineId).ToDictionary(g=>g.Key,g=>g.Sum(l=>l.Quantity));
                if(order.Lines.Length!=allocated.Count||order.Lines.Any(l=>allocated.GetValueOrDefault(OperationalInventory.LineId(l))!=l.Quantity))throw Error("Paketler siparişin kalan tüm satır ve miktarlarını tam karşılamalı.");
            }
            if(input.Kind=="refund"&&(input.Amount is null or <=0||!CurrencyKnown(order.Currency)||!order.TotalKnown||input.Amount>order.Total-s.Operations.Requests.Where(r=>r.OrderId==order.Id&&r.Kind=="refund"&&r.State!="cancelled").Sum(r=>r.Amount??0)))throw Error("Geri ödeme tutarı, bilinen ve henüz talep edilmemiş sipariş tutarını aşamaz.");
            var request=new OperationalRequest{RequestId=input.RequestId,Fingerprint=fingerprint,OrderId=order.Id,StoreId=order.StoreId,Source=order.SourceIdentity,AccountIdentity=Account(order),OrderFingerprint=OrderHash(order),Kind=input.Kind,Lines=lines,Packages=packages,Note=input.Note??"",Amount=input.Amount,Currency=order.Currency};s.Operations.Requests.Add(request);return request;
        });
    }
    private static void ApplyLocal(Database s,Order order,OperationalRequest request)
    {
        Quantities(s,order,request.Lines,request.Id);decimal amount=0;var remaining=new List<OperationLine>();
        foreach(var line in order.Lines)
        {
            var quantity=request.Lines.FirstOrDefault(l=>l.LineId==OperationalInventory.LineId(line))?.Quantity??0;
            if(quantity==0){remaining.Add(line);continue;}
            var unit=line.Amount/line.Quantity;amount+=(unit??0)*quantity;
            if(order.StockManaged&&order.StockApplied)
            {
                var p=s.Products.FirstOrDefault(p=>p.Id==line.ProductId)??throw Error("Stok hareketi için ürün bulunamadı.");p.Stock+=quantity;p.Version++;
                s.StockMovements.Insert(0,new(){Id=Guid.NewGuid().ToString("N"),ProductId=p.Id,ProductName=p.Name,Delta=quantity,Balance=p.Stock,Reason=order.Id+" · kısmi "+request.Kind});
            }
            if(line.Quantity>quantity)remaining.Add(line with{Quantity=line.Quantity-quantity,Amount=unit*(line.Quantity-quantity)});
        }
        order.Lines=remaining.ToArray();order.Quantity=remaining.Sum(l=>l.Quantity);order.Total=Math.Max(0,order.Total-amount);order.Version++;
        if(order.Quantity==0){order.Status=request.Kind=="cancel"?"İptal":"İade";order.StockApplied=false;}
        request.Amount=amount;request.State="completed";dbEvent(s,request);
    }
    private static void dbEvent(Database s,OperationalRequest request){s.Activity.Insert(0,new(){Title="Sipariş satırı işlemi tamamlandı",Detail=request.OrderId+" · "+request.Kind});s.Activity=s.Activity.Take(40).ToList();}
    public OperationalRequest Approve(string id)
    {
        var request=db.CatalogRead(s=>s.Operations.Requests.FirstOrDefault(r=>r.Id==id))??throw Error("Talep bulunamadı.",404);if(request.State!="draft")return request;
        var order=Order(request.OrderId);if(Account(order)!=request.AccountIdentity)throw Error("Satıcı hesabı değişmiş; yeni işlem taslağı hazırlayın.");if(OrderHash(order)!=request.OrderFingerprint)throw Error("Sipariş değişmiş; yeni bir işlem taslağı hazırlayın.");
        if(order.StoreId.Length==0&&request.Kind is "cancel" or "return" or "split")return db.Modify(s=>{
            var current=s.Operations.Requests.Single(r=>r.Id==id);if(current.State!="draft")return current;
            var local=s.Orders.Single(o=>o.Id==order.Id);if(OrderHash(local)!=current.OrderFingerprint)throw Error("Sipariş değişmiş.");
            if(current.Kind=="cancel")ApplyLocal(s,local,current);else{if(current.Kind=="return")Quantities(s,local,current.Lines,current.Id);current.State=current.Kind=="return"?"awaiting-receipt":"planned";}return current;
        });
        var connector=request.Kind=="invoice"?"documents":request.Kind=="refund"?"payments":"channel-"+order.StoreId;
        if(!bridge.Writes(connector))throw Error("İşlem hazır; ilgili panel / belge / ödeme bağlantısı ve yazma izni bekleniyor.");
        if(order.SourceMode=="demo"&&!config.GetValue<bool>("Enterprise:AllowDemoExchange"))throw Error("Demo taleplerinin dış servise gönderimi kapalı.");
        if(order.StoreId.Length>0)
        {
            var store=registry.Connections.GetValueOrDefault(order.StoreId)??throw Error("Mağaza bulunamadı.");
            if(CatalogHub.Source(store)!=request.Source)throw Error("Mağaza kaynağı değişmiş.");
            if(store.Settings.Mode=="real")services.GetRequiredService<MarketPilot.Web.Synchronization.MarketplaceStockWriter>().Check(store.Settings);
            if(request.Kind is "cancel" or "return" or "split"&&order.Lines.Any(l=>string.IsNullOrWhiteSpace(l.NativeLineId)))throw Error("Panelin gerçek sipariş satırı kimlikleri eksik; gönderim hazırlanamadı.");
        }
        return db.Modify(s=>{
            var current=s.Operations.Requests.Single(r=>r.Id==id);if(current.State!="draft")return current;
            if(Account(order)!=current.AccountIdentity||OrderHash(Order(current.OrderId))!=current.OrderFingerprint)throw Error("Sipariş veya satıcı hesabı değişmiş.");
            if(current.Kind is "cancel" or "return")Quantities(s,order,current.Lines,current.Id);
            var kind=current.Kind switch{"invoice"=>"document-submit","refund"=>"refund-submit","split"=>"fulfillment-split",_=>"order-adjustment"};
            AddDelivery(s,connector,kind,current.Id,new{request=current,order},order);current.State="queued";return current;
        });
    }
    public OperationalRequest ReceiveReturn(string id,string receiptId)
    {
        if(string.IsNullOrWhiteSpace(receiptId)||receiptId.Length>150)throw Error("Depo kabul / barkod doğrulama referansı gerekli.",400);
        return db.Modify(s=>{
            var r=s.Operations.Requests.SingleOrDefault(r=>r.Id==id)??throw Error("Talep bulunamadı.",404);
            if(r.State=="completed"&&r.ReceiptId==receiptId)return r;
            if(r.State!="awaiting-receipt"||r.Kind!="return"||r.StoreId.Length>0)throw Error("Bu işlem yerel iade depo kabulü beklemiyor.");
            if(s.Operations.Requests.Any(other=>other.Id!=id&&other.ReceiptId==receiptId))throw Error("Depo referansı başka iadede kullanılmış.");
            var order=s.Orders.Single(o=>o.Id==r.OrderId);if(order.Status is not ("Kargolandı" or "Tamamlandı"))throw Error("Sipariş durumu depo iade kabulüne uygun değil.");ApplyLocal(s,order,r);r.ReceiptId=receiptId;return r;
        });
    }
    private void AddDelivery(Database s,string connector,string kind,string reference,object payload,Order? order=null)
    {
        if(s.Operations.Deliveries.Any(d=>d.Reference==reference&&d.Connector==connector))return;
        if(s.Operations.Deliveries.Count>=5000)throw Error("Aktarım kayıt sınırı dolu.");
        var account=order?.StoreId is {Length:>0} storeId?CredentialProfiles.Identity(config,registry.Connections[storeId].Settings):"";
        s.Operations.Deliveries.Add(new(){Connector=connector,Kind=kind,Reference=reference,ConnectorIdentity=bridge.Identity(connector),StoreId=order?.StoreId??"",Source=order?.SourceIdentity??"",AccountIdentity=account,Payload=JsonSerializer.Serialize(payload,Json)});
    }
    public object Scan()
    {
        var links=catalog.ReconciliationSnapshot();var now=DateTimeOffset.UtcNow;
        return db.Modify(s=>{
            var findings=new List<OperationalFinding>();var count=0;void Find(string kind,string store,string reference,string message,DateTimeOffset? observed=null){count++;if(findings.Count<1000)findings.Add(new(Hash(new{kind,store,reference}),kind,store,reference,message,observed??now));}
            var products=s.Products.ToDictionary(p=>p.Id);var reserved=OperationalInventory.Totals(s);
            foreach(var l in links)
            {
                var p=products.GetValueOrDefault(l.ProductId);if(p==null){Find("mapping",l.StoreId,l.Barcode,"Merkezi ürün eşleştirmesi yok.");continue;}
                if(l.Status is "missing" or "source_changed"){Find("mapping",l.StoreId,p.Sku,"Panel kaydı eksik veya mağaza kaynağı değişmiş.");continue;}
                if(now-l.ObservedAt>TimeSpan.FromMinutes(s.Operations.Policy.StaleMinutes)){Find("stale",l.StoreId,p.Sku,"Panel verisi eski; önce güncel veriyi okuyun.",l.ObservedAt);continue;}
                var available=Math.Max(0,p.Stock-reserved.GetValueOrDefault(p.Id));
                if(l.Stock.HasValue&&p.StockKnown&&MarketPilot.Web.Synchronization.StockOutbox.StockRestriction(registry.Connections[l.StoreId],l.Barcode)==null&&l.Stock!=available)Find("stock",l.StoreId,p.Sku,$"Kaynak kullanılabilir stok {available}; son okunan panel stoğu {l.Stock}.",l.ObservedAt);
                if(l.Currency!=p.Currency)Find("currency",l.StoreId,p.Sku,"Kaynak ve panel para birimleri farklı; fiyatlar karşılaştırılmadı.");
                else if(p.PriceKnown&&l.Price.HasValue&&l.Price!=p.Price)Find("price",l.StoreId,p.Sku,$"Kaynak fiyat {p.Price} {p.Currency}; son okunan panel fiyatı {l.Price} {l.Currency}.",l.ObservedAt);
            }
            foreach(var p in s.Products.Where(p=>p.StockKnown&&reserved.GetValueOrDefault(p.Id)>p.Stock))Find("shortage","",p.Sku,"Rezervasyon toplamı kaynak stoktan fazla; yeni satış stoğu sıfırlandı.");
            foreach(var settlement in s.Operations.Settlements.Where(r=>Math.Abs(r.Difference)>0.01m))Find("finance",settlement.StoreId,settlement.ExternalReference,$"Beklenen net {settlement.ExpectedNet} {settlement.Currency}; kaydedilen ödeme {settlement.NetPaid} {settlement.Currency}.");
            var erp=JsonSerializer.SerializeToElement(services.GetRequiredService<EnterpriseSync>().Status(),Json);
            foreach(var task in erp.GetProperty("tasks").EnumerateArray().Where(t=>t.GetProperty("state").GetString() is "blocked" or "retry"))Find("task","",task.GetProperty("task").GetString()!,task.GetProperty("error").GetString()??"Eşitleme görevi tamamlanamadı.");
            foreach(var job in erp.GetProperty("jobs").EnumerateArray().Where(j=>j.GetProperty("state").GetString() is "queued" or "waiting" or "uncertain").Where(j=>now-j.GetProperty("createdAt").GetDateTimeOffset()>TimeSpan.FromMinutes(s.Operations.Policy.StaleMinutes)))Find("queue",job.GetProperty("storeId").GetString()??"",job.GetProperty("id").GetString()!,"Aktarım uzun süredir tamamlanmadı; kuyruk kaydını kontrol edin.");
            var outbox=JsonSerializer.SerializeToElement(services.GetRequiredService<MarketPilot.Web.Synchronization.StockOutbox>().Status(),Json);
            var stockErrors=outbox.GetProperty("blocked").GetInt32()+outbox.GetProperty("failed").GetInt32();
            if(stockErrors>0)Find("queue","","stock-outbox",$"Stok/fiyat kuyruğunda {stockErrors} engellenmiş veya hatalı işlem var.");
            var hooks=JsonSerializer.SerializeToElement(services.GetRequiredService<MarketPilot.Web.Webhooks.OrderWebhookInbox>().Status(),Json);
            if(hooks.GetProperty("failed").GetInt32()>0)Find("webhook","","webhook-inbox","Başarısız sipariş bildirimi var; webhook kuyruğunu kontrol edin.");
            foreach(var hook in hooks.GetProperty("recent").EnumerateArray().Where(h=>h.GetProperty("error").ValueKind==JsonValueKind.String&&h.GetProperty("state").GetString()=="pending"))Find("webhook","",hook.GetProperty("id").GetString()!,hook.GetProperty("error").GetString()!);
            foreach(var delivery in s.Operations.Deliveries.Where(d=>d.Kind!="notification"&&d.State is "blocked" or "failed" or "uncertain"))Find("delivery",delivery.StoreId,delivery.Reference,delivery.Error??"Servis işlemi henüz doğrulanamadı.");
            s.Operations.FindingCount=count;s.Operations.Findings=findings;s.Operations.LastScanAt=now;
            if(count==0)s.Operations.LastAlertSignature="";
            if(s.Operations.Policy.NotificationsEnabled&&findings.Count>0)
            {
                var signature=Hash(new{count,items=findings.Select(f=>new{f.Id,f.Message}).OrderBy(f=>f.Id)});
                if(signature!=s.Operations.LastAlertSignature){s.Operations.NotificationEpisode++;s.Operations.LastAlertSignature=signature;AddDelivery(s,"notification","notification",signature+"-"+s.Operations.NotificationEpisode,new{subject="MarketPilot operasyon uyarısı",findings=findings.Take(100).Select(f=>new{f.Kind,f.Reference,f.Message}),count});}
            }
            return new{count,stored=s.Operations.Findings.Count,checkedAt=now};
        });
    }
    public async Task Tick(CancellationToken ct)
    {
        if(!await workerGate.WaitAsync(0,ct))return;
        try
        {
            var state=db.CatalogRead(s=>JsonSerializer.Deserialize<OperationalState>(JsonSerializer.Serialize(s.Operations,Json),Json)!);
            if(state.Policy.ReservationsEnabled)
            {
                var currentHoldHash=Hash(state.Holds.Where(OperationalInventory.Active).Select(h=>new{h.Id,h.Quantity,h.State}).OrderBy(h=>h.Id));
                if(currentHoldHash!=lastHoldHash){var enterprise=services.GetRequiredService<EnterpriseSync>();if(enterprise.Plan.Enabled&&enterprise.Plan.Schedules.Any(s=>s.Task=="stock"&&s.Enabled))enterprise.Request("stock");lastHoldHash=currentHoldHash;}
            }
            foreach(var request in state.Requests.Where(r=>r.State=="planned"))
            {
                var order=orders.AllOrders().FirstOrDefault(o=>o.Id==request.OrderId);
                if(order==null||OrderHash(order)!=request.OrderFingerprint)db.Modify(s=>{var r=s.Operations.Requests.Single(r=>r.Id==request.Id);if(r.State=="planned"){r.State="stale";r.Error="Sipariş bulunamadı veya değişmiş; paket planını yeniden hazırlayın.";}return true;});
            }
            if(state.Holds.Any(h=>h.State=="held"&&h.ExpiresAt<=DateTimeOffset.UtcNow))db.Modify(s=>{foreach(var h in s.Operations.Holds.Where(h=>h.State=="held"&&h.ExpiresAt<=DateTimeOffset.UtcNow)){h.State="expired";OperationalInventory.Touch(s,h.ProductId);}return true;});
            if(state.Policy.AutoScan&&(!state.LastScanAt.HasValue||DateTimeOffset.UtcNow-state.LastScanAt>TimeSpan.FromSeconds(state.Policy.ScanIntervalSeconds)))Scan();
            foreach(var delivery in state.Deliveries.Where(d=>d.State is "queued" or "sending" or "waiting" or "uncertain").Where(d=>d.NextAttemptAt<=DateTimeOffset.UtcNow).Take(10))
            {
                try
                {
                    if(!db.CatalogRead(s=>s.Operations.Deliveries.Any(d=>d.Id==delivery.Id&&d.State==delivery.State)))continue;
                    if(!bridge.Writes(delivery.Connector)||bridge.Identity(delivery.Connector)!=delivery.ConnectorIdentity)throw Error("Bağlantı / yazma izni eksik veya değişmiş; gönderim bekliyor.");
                    if(delivery.StoreId.Length>0&&(!registry.Connections.TryGetValue(delivery.StoreId,out var store)||CatalogHub.Source(store)!=delivery.Source||CredentialProfiles.Identity(config,store.Settings)!=delivery.AccountIdentity))throw Error("Mağaza kimliği değişmiş; aktarım durduruldu.");
                    var job=new EnterpriseJob{Id=delivery.Id,Kind=delivery.Kind,Connector=delivery.Connector,Payload=delivery.Payload};
                    BridgeReceipt receipt;
                    if(delivery.State!="queued")receipt=await bridge.Receipt(job,ct);
                    else {
                        var send=db.Modify(s=>{
                            var d=s.Operations.Deliveries.Single(d=>d.Id==delivery.Id);if(d.State!="queued")return false;
                            var request=s.Operations.Requests.FirstOrDefault(r=>r.Id==d.Reference);
                            if(request!=null&&(request.State=="cancelled"||OrderHash(Order(request.OrderId))!=request.OrderFingerprint))throw Error("Sipariş değişmiş veya talep iptal edilmiş; gönderim durduruldu.");
                            d.State="sending";d.Attempts++;return true;
                        });if(!send)continue;receipt=await bridge.Submit(job,ct);
                    }
                    db.Modify(s=>{var d=s.Operations.Deliveries.Single(d=>d.Id==delivery.Id);d.State=receipt.State switch{"completed"=>"completed","failed"=>"failed","not-found" when receipt.SafeToRetry=>"queued",_=>"waiting"};d.ExternalReference=receipt.Reference;d.Error=receipt.State=="failed"?"Harici servis işlemi başarısız bildirdi.":null;d.NextAttemptAt=DateTimeOffset.UtcNow.AddSeconds(5);var request=s.Operations.Requests.FirstOrDefault(r=>r.Id==d.Reference);if(request!=null){request.Error=d.Error;request.State=d.State=="completed"?"completed":d.State=="failed"?"failed":"queued";}return true;});
                }
                catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                catch(Exception error)
                {
                    db.Modify(s=>{var d=s.Operations.Deliveries.Single(d=>d.Id==delivery.Id);d.State=error is IntegrationException i&&i.Status is 400 or 401 or 403 or 404 or 409?"blocked":"uncertain";d.Error=error is IntegrationException?error.Message:"Aktarım sonucu doğrulanamadı; makbuz kontrol edilecek.";d.NextAttemptAt=DateTimeOffset.UtcNow.AddSeconds(30);var request=s.Operations.Requests.FirstOrDefault(r=>r.Id==d.Reference);if(request!=null)request.Error=d.Error;return true;});
                }
            }
        }
        finally {workerGate.Release();}
    }
    public void Retry(string id)=>db.Modify(s=>{var d=s.Operations.Deliveries.SingleOrDefault(d=>d.Id==id)??throw Error("Aktarım bulunamadı.",404);if(d.State is not ("blocked" or "failed" or "uncertain"))throw Error("Bu aktarım tekrar deneme beklemiyor.");if(d.Attempts==0)d.ConnectorIdentity=bridge.Identity(d.Connector);else if(d.ConnectorIdentity!=bridge.Identity(d.Connector))throw Error("Sonucu belirsiz aktarımın bağlantı kimliği değişmiş; önce eski serviste doğrulayın.");d.State=d.Attempts>0?"uncertain":"queued";d.Error=null;d.NextAttemptAt=DateTimeOffset.UtcNow;return true;});
}
public sealed class OperationalWorker(OperationalService service,ILogger<OperationalWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct){using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));try{while(await timer.WaitForNextTickAsync(ct)){try{await service.Tick(ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch(Exception e){log.LogWarning("Operasyon kontrolü tamamlanamadı ({ErrorType}).",e.GetType().Name);}}}catch(OperationCanceledException)when(ct.IsCancellationRequested){}}
}
