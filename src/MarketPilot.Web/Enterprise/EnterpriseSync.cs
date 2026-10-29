using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketPilot.Web.Catalog;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.CentralStock;
using MarketPilot.Web.Integrations.Demo;
using MarketPilot.Web.Synchronization;

namespace MarketPilot.Web.Enterprise;

public sealed class EnterpriseSync
{
    private readonly object gate=new();private readonly SemaphoreSlim tickGate=new(1,1);
    private readonly IServiceProvider services;private readonly IConfiguration config;private readonly string file;
    private readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);private EnterpriseState state;private string persisted;private long revision;
    public long Revision=>Interlocked.Read(ref revision);
    public bool Managed {get{lock(gate)return state.Managed;}}
    public EnterpriseSync(IWebHostEnvironment env,IServiceProvider services,IConfiguration config)
    {
        this.services=services;this.config=config;var data=Path.Combine(env.ContentRootPath,"Data");Directory.CreateDirectory(data);file=Path.Combine(data,"enterprise-sync.json");
        state=File.Exists(file)?JsonSerializer.Deserialize<EnterpriseState>(File.ReadAllText(file),json)??throw new InvalidOperationException("Entegrasyon durumu okunamadı."):new();
        foreach(var s in EnterprisePlan.Defaults())state.Tasks.TryAdd(s.Task,new(){Task=s.Task,NextRunAt=DateTimeOffset.UtcNow});
        foreach(var job in state.Jobs.Where(j=>j.State=="sending"))job.State="uncertain";
        foreach(var task in state.Tasks.Values.Where(t=>t.State=="running")){task.State="retry";task.Manual=true;}
        persisted=JsonSerializer.Serialize(state,json);
    }
    private T Clone<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value,json),json)!;
    public EnterprisePlan Plan {get{lock(gate)return Clone(state.Plan);}}
    private void Save()
    {
        try{var terminal=state.Jobs.Where(j=>j.State is "completed" or "superseded").OrderByDescending(j=>j.CreatedAt).Skip(2000).ToHashSet();state.Jobs.RemoveAll(terminal.Contains);var candidate=JsonSerializer.Serialize(state,json);File.WriteAllText(file+".tmp",candidate);for(var attempt=0;;attempt++){try{File.Move(file+".tmp",file,true);break;}catch(Exception e)when(attempt<4&&(e is IOException or UnauthorizedAccessException)){Thread.Sleep(10*(1<<attempt));}}persisted=candidate;Interlocked.Increment(ref revision);}
        catch{state=JsonSerializer.Deserialize<EnterpriseState>(persisted,json)!;throw;}
    }
    public void Configure(EnterprisePlan input)
    {
        if(input.Schedules?.Any(s=>s==null||string.IsNullOrWhiteSpace(s.Task))==true)throw new ArgumentException("Eşitleme görev adı boş olamaz.");
        if(input.Schedules==null||input.Targets==null||input.Targets.Length>100||input.Schedules.Length!=EnterprisePlan.Defaults().Length||input.Schedules.Any(s=>s==null||s.IntervalSeconds is <30 or >86400||!state.Tasks.ContainsKey(s.Task))||input.Schedules.Select(s=>s.Task).Distinct().Count()!=input.Schedules.Length||input.Targets.Select(t=>t?.StoreId).Distinct().Count()!=input.Targets.Length||input.StockAuthority is not ("erp" or "wms")||input.FulfillmentMode is not ("disabled" or "nebim-integrated" or "standalone")||input.SafetyStock is <0 or >1000000||input.WarehouseCode?.Length>100||input.PriceListCode?.Length>100)throw new ArgumentException("Zaman aralıkları 30–86400 saniye olmalı; hedef, kaynak ve depo bilgilerini kontrol edin.");
        var enabledTasks=input.Schedules.Where(s=>s.Enabled).Select(s=>s.Task).ToHashSet(StringComparer.Ordinal);
        if(input.FulfillmentMode=="disabled"&&(input.StockAuthority=="wms"||enabledTasks.Overlaps(["wms-orders","wms-stock","wms-dispatches","cargo-create","shipments"]))||
           input.FulfillmentMode=="nebim-integrated"&&enabledTasks.Contains("wms-orders")||
           input.FulfillmentMode=="standalone"&&enabledTasks.Contains("wms-orders")&&!enabledTasks.Contains("erp-orders")||
           enabledTasks.Contains("wms-stock")&&input.StockAuthority!="wms")
            throw new ArgumentException("WMS topolojisi ve görev planı tutarsız; bağımsız WMS siparişleri için önce ERP aktarımı etkin olmalıdır.");
        var registry=services.GetRequiredService<IntegrationRegistry>();
        foreach(var target in input.Targets)if(target==null||!registry.Connections.TryGetValue(target.StoreId,out var store)||target.Source!=CatalogHub.Source(store))throw new ArgumentException("Mağaza kaynağı değişti; sayfayı yenileyin.");
        // A saved plan owns scheduling; the older all-store timer must not bypass selected targets.
        if(input.LookbackDays is <1 or >14||input.WarehouseCode==null||input.PriceListCode==null)throw new ArgumentException("Sipariş tarama penceresi 1–14 gün olmalı; depo bilgilerini kontrol edin.");
        var legacy=services.GetRequiredService<OrderSynchronization>();legacy.Configure(legacy.Settings with{Enabled=false,StockEnabled=false,LookbackDays=input.LookbackDays});
        var stock=services.GetRequiredService<CentralStockService>();if(stock.Settings.AutoSync)stock.UpdateSettings(stock.Settings with{AutoSync=false});
        lock(gate){state.Plan=Clone(input);state.Managed=true;foreach(var task in state.Tasks.Values)task.NextRunAt=DateTimeOffset.UtcNow;Save();}
    }
    public void Request(string task)
    {
        if(string.IsNullOrWhiteSpace(task))throw new ArgumentException("Eşitleme görev adı boş olamaz.");
        lock(gate)
        {
            var selected=task=="all"?state.Plan.Schedules.Where(s=>s.Enabled).Select(s=>s.Task).ToArray():[task];
            if(selected.Any(t=>!state.Tasks.ContainsKey(t)))throw new ArgumentException("Eşitleme görevi bulunamadı.");
            if(selected.Any(t=>(t=="wms-orders"&&state.Plan.FulfillmentMode!="standalone")||
                ((t is "wms-stock" or "wms-dispatches" or "cargo-create" or "shipments")&&state.Plan.FulfillmentMode=="disabled")||
                (t=="wms-stock"&&state.Plan.StockAuthority!="wms")))
                throw new IntegrationException("fulfillment_disabled","Seçilen WMS/depo topolojisi bu görev için etkin değil.",409);
            foreach(var key in selected){state.Tasks[key].Manual=true;state.Tasks[key].NextRunAt=DateTimeOffset.UtcNow;}Save();
        }
    }
    public object Status()
    {
        var registry=services.GetRequiredService<IntegrationRegistry>();var bridge=services.GetRequiredService<EnterpriseBridge>();
        lock(gate)return new {managed=state.Managed,plan=Clone(state.Plan),tasks=Clone(state.Tasks.Values.ToArray()),jobs=state.Jobs.OrderByDescending(j=>j.CreatedAt).Take(100).Select(j=>new {j.Id,j.Kind,j.Connector,j.StoreId,j.Reference,j.State,j.Attempts,j.Error,j.CreatedAt,j.FinishedAt,j.ExternalReference}).ToArray(),
            counts=state.Jobs.GroupBy(j=>j.State).ToDictionary(g=>g.Key,g=>g.Count()),state.LastCatalogAt,state.LastWmsStockAt,
            connectors=new {erp=new {catalogPull=bridge.Configured("erp"),catalogPush=!string.IsNullOrWhiteSpace(config["Enterprise:Erp:InboundKey"]),orders=bridge.Writes("erp")},wms=new {configured=bridge.Configured("wms"),orders=bridge.Writes("wms")},cargo=new {configured=bridge.Configured("cargo"),writes=bridge.Writes("cargo")}},
            stores=registry.Connections.Values.Select(s=>new {s.Id,s.Settings.Name,s.Settings.Marketplace,s.Settings.Mode,source=CatalogHub.Source(s),mediaConfigured=bridge.Writes("channel-"+s.Id)}).ToArray(),
            dispatches=state.Dispatches.Values.OrderByDescending(d=>d.UpdatedAt).Take(50).ToArray(),realReads=config.GetValue<bool>("Synchronization:IncludeRealStores"),realWrites=config.GetValue<bool>("Synchronization:AllowRealWrites"),demoExchange=config.GetValue<bool>("Enterprise:AllowDemoExchange")};
    }
    public void CatalogReceived(){lock(gate){state.LastCatalogAt=DateTimeOffset.UtcNow;Save();}}
    public void Retry(string id)
    {
        lock(gate){var job=state.Jobs.FirstOrDefault(j=>j.Id==id)??throw new ArgumentException("İş bulunamadı.");if(job.State is not ("blocked" or "failed" or "uncertain"))throw new ArgumentException("Bu iş tekrar başlatılamaz.");
            if(job.NativeJobId==null&&job.Payload=="{}"){job.State="superseded";Request(job.Kind switch{"erp-order"=>"erp-orders","wms-order"=>"wms-orders","cargo-order"=>"cargo-create",_=>job.Kind});return;}
            if(job.NativeJobId!=null)services.GetRequiredService<StockOutbox>().Retry(job.NativeJobId);
            if(job.NativeJobId==null&&job.Attempts==0)job.ConnectorIdentity=services.GetRequiredService<EnterpriseBridge>().Identity(job.Connector);
            job.State=job.NativeJobId!=null?"waiting":job.State=="uncertain"?"uncertain":"queued";job.NextAttemptAt=DateTimeOffset.UtcNow;job.Error=null;Save();}
    }
    public async Task Tick(CancellationToken ct)
    {
        if(!await tickGate.WaitAsync(0,ct))return;
        try{
            var plan=Plan;var keys=new[]{"orders","erp-orders","wms-orders","wms-dispatches","cargo-create","shipments","catalog","wms-stock","listings","stock","price","images"};
            foreach(var key in keys)
            {
                bool due;lock(gate){var t=state.Tasks[key];due=t.NextRunAt<=DateTimeOffset.UtcNow&&(t.Manual||plan.Enabled&&plan.Schedules.Any(s=>s.Task==key&&s.Enabled));if(due){t.Manual=false;t.State="running";t.LastRunAt=DateTimeOffset.UtcNow;t.NextRunAt=DateTimeOffset.UtcNow.AddSeconds(plan.Schedules.FirstOrDefault(s=>s.Task==key)?.IntervalSeconds??60);Save();}}
                if(!due)continue;
                try{var count=await Run(key,plan,ct);lock(gate){var t=state.Tasks[key];t.State="completed";t.LastSuccessAt=DateTimeOffset.UtcNow;t.Count=count;t.Error=null;Save();}}
                catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                catch(Exception error){lock(gate){var t=state.Tasks[key];t.State=error is IntegrationException or ArgumentException?"blocked":"retry";t.Error=error is IntegrationException or ArgumentException?error.Message:"Görev tamamlanamadı; önceki veriler korundu.";Save();}}
            }
            await Deliver(ct);
        }finally{tickGate.Release();}
    }
    private SyncTarget[] Targets(EnterprisePlan plan,string task)
    {
        var registry=services.GetRequiredService<IntegrationRegistry>();
        var targets=plan.Targets.Where(t=>t.Enabled&&(task switch{"stock"=>t.Stock,"price"=>t.Price,"images"=>t.Images,"listings"=>t.Products,"orders"=>t.Orders,_=>t.Orders})).ToArray();
        foreach(var target in targets){if(!registry.Connections.TryGetValue(target.StoreId,out var s)||target.Source!=CatalogHub.Source(s))throw new IntegrationException("source_changed","Hedef mağaza kaynağı değişti; planı güncelleyin.",409);}
        return targets;
    }
    private async Task<int> Run(string task,EnterprisePlan plan,CancellationToken ct)
    {
        var bridge=services.GetRequiredService<EnterpriseBridge>();var db=services.GetRequiredService<DemoStore>();var registry=services.GetRequiredService<IntegrationRegistry>();
        if(task=="catalog"){
            var items=await services.GetRequiredService<IErpCatalogProvider>().ReadCatalogAsync(plan.WarehouseCode,plan.PriceListCode,ct);
            services.GetRequiredService<ErpCatalog>().Import(new("pull-"+Guid.NewGuid().ToString("N"),items),plan.StockAuthority=="wms");CatalogReceived();return items.Length;
        }
        if(task=="wms-stock"){
            if(plan.FulfillmentMode=="disabled")throw new IntegrationException("fulfillment_disabled","WMS topolojisi yapılandırılmadı.",409);
            if(plan.StockAuthority!="wms")throw new ArgumentException("Stok kaynağı olarak WMS seçilmedi.");
            var items=await services.GetRequiredService<IWmsProvider>().ReadStockAsync(plan.WarehouseCode,plan.PriceListCode,ct);
            if(items.Any(i=>string.IsNullOrWhiteSpace(i.Barcode)||i.Stock is null or <0 or >10000000||i.WarehouseCode!=null&&i.WarehouseCode!=plan.WarehouseCode)||items.GroupBy(i=>i.Barcode).Any(g=>g.Count()>1))throw new ArgumentException("WMS stok/barkod/depo yanıtı geçersiz; stoklar değiştirilmedi.");
            db.Modify(s=>{foreach(var item in items){var rows=s.Products.Where(p=>p.SourceSystem=="nebim-v3"&&p.Barcode==item.Barcode).ToArray();if(rows.Length!=1)throw new ArgumentException("WMS barkodu tek Nebim ürününe eşleşmiyor.");rows[0].Stock=item.Stock!.Value;rows[0].StockKnown=true;rows[0].Version++;}MarketPilot.Web.Workspace.OperationalInventory.Acknowledge(s,items.SelectMany(i=>i.StockAcknowledgements??[]).ToArray(),s.Products.Where(p=>items.Any(i=>i.Barcode==p.Barcode)).Select(p=>p.Id).ToHashSet());db.Event(s,"WMS stokları alındı",items.Length+" barkod güncellendi.");return true;});
            lock(gate){state.LastWmsStockAt=DateTimeOffset.UtcNow;Save();}return items.Length;
        }
        if(task=="orders"){
            var targets=Targets(plan,task);if(targets.Length==0)throw new ArgumentException("Sipariş okunacak mağazaları seçin.");
            await services.GetRequiredService<OrderSynchronization>().TickSelected(targets.Select(t=>t.StoreId).ToHashSet(),ct);
            return targets.Length;
        }
        if(task=="wms-dispatches")return await ReadDispatches(plan,ct);
        if(task is "erp-orders" or "wms-orders" or "cargo-create"){
            if((task is "wms-orders" or "cargo-create")&&plan.FulfillmentMode=="disabled"||task=="wms-orders"&&plan.FulfillmentMode!="standalone")throw new IntegrationException("fulfillment_disabled","WMS ve kargo akışları için topoloji açıkça yapılandırılmalıdır.",409);
            var connector=task=="erp-orders"?"erp":task=="wms-orders"?"wms":"cargo";var kind=task=="cargo-create"?"cargo-order":connector+"-order";
            var ids=Targets(plan,task).Select(t=>t.StoreId).ToHashSet();var count=0;
            foreach(var order in services.GetRequiredService<OperationsHub>().AllOrders().Where(o=>ids.Contains(o.StoreId))){
                if(order.SourceMode=="demo"&&!config.GetValue<bool>("Enterprise:AllowDemoExchange"))continue;
                if(task=="cargo-create"&&order.Status is not ("Yeni" or "Hazırlanıyor"))continue;
                var source=CatalogHub.Source(registry.Connections[order.StoreId]);
                if(task=="wms-orders"){lock(gate)if(!state.Jobs.Any(j=>j.Kind=="erp-order"&&j.Reference==order.Id&&j.StoreId==order.StoreId&&j.Source==source&&j.State=="completed"))continue;}
                WmsDispatch? dispatch=null;if(task=="cargo-create"){lock(gate)dispatch=state.Dispatches.GetValueOrDefault(order.Id);if(dispatch==null||dispatch.AccountSource!=CatalogHub.Source(registry.Connections[order.StoreId])||dispatch.Status is not ("ready" or "packed"))continue;}
                if(order.Lines.Length==0||order.Lines.Any(l=>string.IsNullOrWhiteSpace(l.Barcode))){Blocked(kind,registry.Connections[order.StoreId],source,order.Id,"Sipariş satırlarında barkod eksik; kaynak eşleştirmesi bekleniyor.",connector);continue;}
                Queue(kind,connector,order.StoreId,source,order.Id,new {externalOrderId=order.Id,accountSource=source,storeId=order.StoreId,order,warehouseCode=plan.WarehouseCode,package=dispatch});count++;
            }return count;
        }
        if(task=="shipments")return await ReadShipments(plan,ct);
        if(task is "stock" or "price" or "images" or "listings"){
            var targets=Targets(plan,task);if(targets.Length==0)throw new ArgumentException("Veri gönderilecek mağazaları seçin.");
            var products=db.CatalogRead(s=>JsonSerializer.Deserialize<Product[]>(JsonSerializer.Serialize(s.Products.Where(p=>p.SourceSystem=="nebim-v3"&&p.Status=="Aktif")),json)!);
            if(products.Length==0)throw new ArgumentException("Önce Nebim V3 ürün verilerini alın.");
            var sourceUnconfirmed=false;
            if(task=="stock")lock(gate){var activeTargets=plan.Targets.Where(t=>t.Enabled).Select(t=>t.StoreId+"|"+t.Source).ToHashSet();var transfers=state.Jobs.Where(j=>(j.Kind is "erp-order" or "wms-order")&&activeTargets.Contains(j.StoreId+"|"+j.Source)&&(j.Attempts>0||plan.Schedules.Any(s=>s.Enabled&&s.Task==(j.Kind=="erp-order"?"erp-orders":"wms-orders")))).ToArray();var fresh=plan.StockAuthority=="wms"?state.LastWmsStockAt:state.LastCatalogAt;
                if(!fresh.HasValue)throw new ArgumentException("Seçilen stok kaynağından ilk başarılı okuma bekleniyor; stok gönderilmedi.");
                sourceUnconfirmed=transfers.Any(j=>j.State!="completed")||transfers.Any(j=>j.FinishedAt.HasValue&&(!fresh.HasValue||j.FinishedAt>fresh));}
            var count=0;
            foreach(var target in targets){var store=registry.Connections[target.StoreId];
                if(store.Settings.Mode=="demo"&&!config.GetValue<bool>("Enterprise:AllowDemoExchange"))continue;
                foreach(var product in products){
                    if(task=="listings"){Queue("listings","channel-"+store.Id,store.Id,target.Source,product.Id,new {product,storeId=store.Id,accountSource=target.Source,categoryBindings=db.CatalogRead(s=>s.Catalog.Categories.Values.Where(c=>c.StoreId==store.Id&&c.Category==product.Category).ToArray())});count++;continue;}
                    if(task=="images"){if(product.Images.Count==0)continue;Queue("images","channel-"+store.Id,store.Id,target.Source,product.Id,new {productId=product.Id,product.Sku,product.Barcode,images=product.Images,storeId=store.Id,accountSource=target.Source});count++;continue;}
                    lock(gate){if(state.Jobs.Any(j=>j.StoreId==store.Id&&j.Source==target.Source&&j.Reference==product.Id&&j.NativeJobId!=null&&j.State=="waiting")){state.Tasks[task].Manual=true;state.Tasks[task].NextRunAt=DateTimeOffset.UtcNow.AddSeconds(5);Save();continue;}}
                    try{
                        var native=store.Settings.Mode=="demo"?services.GetRequiredService<DemoWorkspace>().Snapshot(store.Settings).Products.Where(p=>p.Barcode==product.Barcode).SingleOrDefault():await services.GetRequiredService<MarketplaceStockWriter>().Read(store.Settings,product.Barcode,null,ct);
                        if(native==null)throw new ArgumentException("Mağazada barkod eşleşmesi yok.");
                        if(task=="price"&&native.Currency!=product.Currency)throw new ArgumentException("Kaynak ve mağaza para birimleri farklı; fiyat gönderilmedi.");
                        var quantity=task=="stock"?product.StockKnown?db.CatalogRead(s=>MarketPilot.Web.Workspace.OperationalInventory.Available(s,product))-plan.SafetyStock:(decimal?)null:native.Stock;
                        if(quantity==null||quantity>1000000||quantity!=decimal.Truncate(quantity.Value)||task=="price"&&(!product.PriceKnown||product.Price<=0))throw new ArgumentException("Stok veya fiyat bilinmiyor ya da kanal biçimine uygun değil.");
                        if(task=="stock"&&sourceUnconfirmed&&(native.Stock==null||quantity>native.Stock))throw new ArgumentException("Sipariş / kaynak onayı beklenirken stok artışı engellendi; yalnızca güvenli stok azaltma yapılabilir.");
                        var price=task=="price"?(decimal?)product.Price:null;
                        var job=services.GetRequiredService<StockOutbox>().Enqueue(store,product.Barcode,Math.Max(0,(int)quantity.Value),price,"Nebim V3 · "+task,nativeSku:native.Sku);
                        TrackNative(task,store,target.Source,product.Id,job.Id);count++;
                    }catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
                    catch(Exception error)when(error is IntegrationException or ArgumentException){Blocked(task,store,target.Source,product.Id,error.Message);}
                }
            }return count;
        }
        throw new ArgumentException("Görev desteklenmiyor.");
    }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Entity(EnterpriseJob j)=>j.Kind+"|"+j.Connector+"|"+j.Source+"|"+j.AccountIdentity+"|"+j.Reference;
    private void Queue(string kind,string connector,string storeId,string source,string reference,object payload)
    {
        var body=JsonSerializer.Serialize(payload,json);var identity=services.GetRequiredService<EnterpriseBridge>().Identity(connector);
        var store=services.GetRequiredService<IntegrationRegistry>().Connections[storeId];var account=store.Settings.Mode=="real"?CredentialProfiles.Identity(config,store.Settings):"";
        var key=Hash(kind+"|"+connector+"|"+identity+"|"+account+"|"+source+"|"+reference+"|"+body);
        var candidate=new EnterpriseJob{Key=key,Kind=kind,Connector=connector,ConnectorIdentity=identity,StoreId=storeId,Source=source,AccountIdentity=account,Reference=reference,Payload=body};var entity=Entity(candidate);
        lock(gate){if(state.Delivered.GetValueOrDefault(entity)==key||state.Jobs.Any(j=>j.Key==key))return;if(state.Jobs.Count(j=>j.State is "queued" or "sending" or "uncertain" or "waiting")>=5000||state.Jobs.Count>=10000)throw new ArgumentException("Aktarım kuyruğu dolu.");foreach(var prior in state.Jobs.Where(j=>Entity(j)==entity&&j.Attempts==0&&j.State is "queued" or "blocked"))prior.State="superseded";state.Jobs.Insert(0,candidate);Save();}
    }
    private void TrackNative(string kind,ConnectionState store,string source,string reference,string id)
    {
        lock(gate){if(state.Jobs.Any(j=>j.NativeJobId==id))return;state.Jobs.Insert(0,new(){Kind=kind,Connector="marketplace",StoreId=store.Id,Source=source,Reference=reference,NativeJobId=id,State="waiting"});Save();}
    }
    private void Blocked(string kind,ConnectionState store,string source,string reference,string error,string connector="marketplace")
    {
        lock(gate){var existing=state.Jobs.FirstOrDefault(j=>j.Kind==kind&&j.StoreId==store.Id&&j.Source==source&&j.Reference==reference&&j.State=="blocked"&&j.NativeJobId==null);if(existing!=null){existing.Error=error;return;}state.Jobs.Insert(0,new(){Kind=kind,Connector=connector,StoreId=store.Id,Source=source,Reference=reference,State="blocked",Error=error});Save();}
    }
    private async Task Deliver(CancellationToken ct)
    {
        EnterpriseJob[] jobs;lock(gate)jobs=state.Jobs.Where(j=>j.State is "queued" or "waiting" or "uncertain").GroupBy(Entity).Select(g=>g.OrderBy(j=>j.CreatedAt).First()).Where(j=>j.NextAttemptAt<=DateTimeOffset.UtcNow).OrderBy(j=>j.CreatedAt).Take(20).Select(Clone).ToArray();
        var bridge=services.GetRequiredService<EnterpriseBridge>();var registry=services.GetRequiredService<IntegrationRegistry>();
        foreach(var job in jobs){ct.ThrowIfCancellationRequested();
            try{
                if(!registry.Connections.TryGetValue(job.StoreId,out var store)||CatalogHub.Source(store)!=job.Source)throw new IntegrationException("source_changed","Satıcı kaynağı değişti; aktarım durduruldu.",409);
                if(store.Settings.Mode=="real"&&job.NativeJobId==null&&CredentialProfiles.Identity(config,store.Settings)!=job.AccountIdentity)throw new IntegrationException("account_changed","Satıcı kimlik bilgileri değişti; aktarım durduruldu.",409);
                if(job.State=="queued"&&!Plan.Targets.Any(t=>t.Enabled&&t.StoreId==job.StoreId&&t.Source==job.Source))throw new IntegrationException("target_disabled","Bu mağaza artık seçili hedef değil; gönderim durduruldu.",409);
                if(job.NativeJobId!=null){var native=services.GetRequiredService<StockOutbox>().Find(job.NativeJobId);UpdateJob(job.Id,j=>{j.State=native?.State switch{"completed"=>"completed","blocked"=>"blocked","failed"=>"failed","superseded"=>"superseded",_=>"waiting"};j.Error=native?.Error;if(j.State=="completed")j.FinishedAt=DateTimeOffset.UtcNow;j.NextAttemptAt=DateTimeOffset.UtcNow.AddSeconds(2);});continue;}
                if(!bridge.Configured(job.Connector)||bridge.Identity(job.Connector)!=job.ConnectorIdentity)throw new IntegrationException("connector_changed","Bağlantı eksik veya kimliği değişmiş; onaylanmadan gönderilmeyecek.",409);
                if(store.Settings.Mode=="demo"&&!config.GetValue<bool>("Enterprise:AllowDemoExchange"))throw new IntegrationException("demo_guard","Demo verilerinin harici servise aktarımı sunucuda kapalı.",409);
                if(job.Connector.StartsWith("channel-",StringComparison.Ordinal)&&store.Settings.Mode=="real")services.GetRequiredService<MarketplaceStockWriter>().Check(store.Settings);
                BridgeReceipt receipt;
                if(job.State is "waiting" or "uncertain")receipt=await bridge.Receipt(job,ct);
                else{UpdateJob(job.Id,j=>{j.State="sending";j.Attempts++;});receipt=await bridge.Submit(job,ct);}
                UpdateJob(job.Id,j=>{j.State=receipt.State switch{"completed"=>"completed","failed"=>"failed","not-found" when receipt.SafeToRetry=>"queued",_=>"waiting"};j.ExternalReference=receipt.Reference;j.Error=null;j.NextAttemptAt=DateTimeOffset.UtcNow.AddSeconds(5);if(j.State=="completed")j.FinishedAt=DateTimeOffset.UtcNow;});
                if(receipt.State=="completed"&&(job.Kind=="erp-order"||job.Kind=="wms-order"&&Plan.FulfillmentMode!="disabled"))Request(Plan.StockAuthority=="wms"?"wms-stock":"catalog");
            }catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(IntegrationException error){UpdateJob(job.Id,j=>{j.State=error.Status==429?job.State=="queued"?"queued":job.State:error.Status is 400 or 401 or 403 or 404 or 409?"blocked":"uncertain";j.Error=error.Message;j.NextAttemptAt=DateTimeOffset.UtcNow.AddSeconds(60);});}
            catch(Exception){UpdateJob(job.Id,j=>{j.State="uncertain";j.Error="Aktarım sonucu doğrulanamadı; kör tekrar yerine işlem makbuzu kontrol edilecek.";j.NextAttemptAt=DateTimeOffset.UtcNow.AddSeconds(30);});}
        }
    }
    private void UpdateJob(string id,Action<EnterpriseJob> update){lock(gate){var job=state.Jobs.First(j=>j.Id==id);update(job);if(job.State=="completed"&&job.Key.Length>0)state.Delivered[Entity(job)]=job.Key;Save();}}
    private async Task<int> ReadDispatches(EnterprisePlan plan,CancellationToken ct)
    {
        if(plan.FulfillmentMode=="disabled")throw new IntegrationException("fulfillment_disabled","WMS topolojisi yapılandırılmadı.",409);
        var rows=await services.GetRequiredService<IWmsProvider>().ReadDispatchesAsync(plan.WarehouseCode,plan.PriceListCode,ct);
        var orders=services.GetRequiredService<OperationsHub>().AllOrders().ToDictionary(o=>o.Id);var targets=Targets(plan,"wms-dispatches").ToDictionary(t=>t.StoreId);
        foreach(var row in rows){
            if(!orders.TryGetValue(row.ExternalOrderId,out var order)||!targets.TryGetValue(row.StoreId,out var target)||order.StoreId!=row.StoreId||target.Source!=row.AccountSource||row.Lines==null||row.Lines.Length>1000||string.IsNullOrWhiteSpace(row.PackageId)||row.PackageId.Length>150||row.Status is not ("ready" or "packed" or "cancelled")||order.SourceMode=="demo"&&!config.GetValue<bool>("Enterprise:AllowDemoExchange"))throw new ArgumentException("WMS paket kaydı mağaza veya sipariş doğrulamasından geçmedi.");
            var expected=order.Lines.GroupBy(l=>l.Barcode).ToDictionary(g=>g.Key,g=>g.Sum(l=>l.Quantity));var packed=row.Lines.GroupBy(l=>l.Barcode).ToDictionary(g=>g.Key,g=>g.Sum(l=>l.Quantity));
            if(packed.Any(p=>string.IsNullOrWhiteSpace(p.Key)||p.Value<=0)||row.Status!="cancelled"&&(packed.Count!=expected.Count||packed.Any(p=>!expected.TryGetValue(p.Key,out var quantity)||quantity!=p.Value)))throw new ArgumentException("Paket barkodları ve miktarlar sipariş satırlarıyla eşleşmiyor.");
        }
        lock(gate){foreach(var row in rows)if(!state.Dispatches.TryGetValue(row.ExternalOrderId,out var old)||old.UpdatedAt<row.UpdatedAt)state.Dispatches[row.ExternalOrderId]=row;Save();}return rows.Length;
    }
    private async Task<int> ReadShipments(EnterprisePlan plan,CancellationToken ct)
    {
        if(plan.FulfillmentMode=="disabled")throw new IntegrationException("fulfillment_disabled","WMS/kargo topolojisi yapılandırılmadı.",409);
        var rows=await services.GetRequiredService<ICargoProvider>().ReadShipmentsAsync(plan.WarehouseCode,plan.PriceListCode,ct);
        var orders=services.GetRequiredService<OperationsHub>().AllOrders().ToDictionary(o=>o.Id);var targets=Targets(plan,"shipments").ToDictionary(t=>t.StoreId);var db=services.GetRequiredService<DemoStore>();
        var valid=rows.Select(r=>{
            if(!orders.TryGetValue(r.ExternalOrderId,out var order)||!targets.TryGetValue(r.StoreId,out var target)||order.StoreId!=r.StoreId||target.Source!=r.AccountSource||string.IsNullOrWhiteSpace(r.TrackingNumber)||r.TrackingNumber.Length>150||r.LabelUrl!=null&&!EnterpriseBridge.SafeLink(r.LabelUrl)||!new[]{"preparing","shipped","delivered","cancelled","returned"}.Contains(r.Status))throw new ArgumentException("Kargo kaydı sipariş, mağaza veya bağlantı doğrulamasından geçemedi.");return (Row:r,Order:order);
        }).ToArray();
        db.Modify(s=>{foreach(var item in valid){var r=item.Row;var id="cargo-"+Hash(r.AccountSource+"|"+r.ShipmentId);var sh=s.Shipments.FirstOrDefault(x=>x.Id==id);if(sh!=null&&sh.UpdatedAt>=r.UpdatedAt)continue;if(sh==null){sh=new(){Id=id,StoreId=r.StoreId,OrderId=item.Order.ChannelOrderNumber};s.Shipments.Add(sh);}sh.Source=r.AccountSource;sh.TrackingNumber=r.TrackingNumber;sh.Carrier=r.Carrier;sh.LabelUrl=r.LabelUrl;sh.UpdatedAt=r.UpdatedAt;sh.Status=r.Status switch{"shipped"=>"Kargoya verildi","delivered"=>"Teslim edildi","cancelled"=>"İptal","returned"=>"İade",_=>"Hazırlanıyor"};}return true;});
        foreach(var item in valid)Queue("marketplace-shipment","channel-"+item.Row.StoreId,item.Row.StoreId,item.Row.AccountSource,item.Row.ExternalOrderId,item.Row);
        return valid.Length;
    }
}

public sealed class EnterpriseSyncWorker(EnterpriseSync sync,ILogger<EnterpriseSyncWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
        try{while(await timer.WaitForNextTickAsync(stoppingToken)){try{await sync.Tick(stoppingToken);}catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){throw;}catch(Exception e){log.LogWarning("Entegrasyon döngüsü tamamlanamadı: {Type}",e.GetType().Name);}}}
        catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}
    }
}
