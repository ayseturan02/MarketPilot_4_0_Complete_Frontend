using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketPilot.Web.Catalog;
using MarketPilot.Web.Integrations;

namespace MarketPilot.Web.Webhooks;

public sealed class OrderHookEvent
{
    public string Id{get;set;}="";public string EndpointId{get;set;}="";public string ConnectionId{get;set;}="";public string Source{get;set;}="";public string SellerId{get;set;}="";
    public MarketplaceOrder Order{get;set;}=null!;public string State{get;set;}="pending";public int Attempts{get;set;}
    public DateTimeOffset ReceivedAt{get;set;}=DateTimeOffset.UtcNow;public DateTimeOffset DueAt{get;set;}=DateTimeOffset.UtcNow;public DateTimeOffset? FinishedAt{get;set;}public string? Error{get;set;}
}
public sealed class OrderWebhookInbox
{
    private readonly object gate=new();private readonly SemaphoreSlim processing=new(1,1);private readonly string file;private readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);private Dictionary<string,OrderHookEvent> events;private string persisted;
    public OrderWebhookInbox(IWebHostEnvironment env){var dir=Path.Combine(env.ContentRootPath,"Data");Directory.CreateDirectory(dir);file=Path.Combine(dir,"order-webhook-inbox.json");events=File.Exists(file)?JsonSerializer.Deserialize<Dictionary<string,OrderHookEvent>>(File.ReadAllText(file),json)??throw new InvalidOperationException("Webhook inbox cannot be read."):new();persisted=JsonSerializer.Serialize(events,json);}
    private void Save(){try{var value=JsonSerializer.Serialize(events,json);File.WriteAllText(file+".tmp",value);File.Move(file+".tmp",file,true);persisted=value;}catch{events=JsonSerializer.Deserialize<Dictionary<string,OrderHookEvent>>(persisted,json)!;throw;}}
    public object Accept(OrderHookBinding binding,OrderWebhookPayload.Item[] items)
    {
        lock(gate){var now=DateTimeOffset.UtcNow;var identities=items.Select(i=>(Item:i,Id:Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CatalogHub.Source(binding.Store)+"|"+binding.SellerId+"|"+i.Order.PackageId+"|"+i.Version+"|"+i.Fingerprint))).ToLowerInvariant())).ToArray();
            var added=identities.Where(i=>!events.ContainsKey(i.Id)).DistinctBy(i=>i.Id).ToArray();if(added.Length==0)return new{accepted=0,duplicates=items.Length};
            var expired=events.Values.Where(e=>e.FinishedAt<now.AddDays(-7)&&e.State is "processed" or "ignored").Select(e=>e.Id).ToArray();foreach(var id in expired)events.Remove(id);
            if(events.Count+added.Length>10000){events=JsonSerializer.Deserialize<Dictionary<string,OrderHookEvent>>(persisted,json)!;throw new IntegrationException("inbox_full","Bildirim sırası dolu. Daha sonra tekrar gönderin.",503);}
            foreach(var i in added)events[i.Id]=new(){Id=i.Id,EndpointId=binding.Id,ConnectionId=binding.Store.Id,Source=CatalogHub.Source(binding.Store),SellerId=binding.SellerId,Order=i.Item.Order};if(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(events,json))>32*1024*1024){events=JsonSerializer.Deserialize<Dictionary<string,OrderHookEvent>>(persisted,json)!;throw new IntegrationException("inbox_full","Bildirim deposu dolu. Daha sonra tekrar gönderin.",503);}Save();return new{accepted=added.Length,duplicates=items.Length-added.Length};
        }
    }
    public object Status(){lock(gate)return new{pending=events.Values.Count(e=>e.State=="pending"),processed=events.Values.Count(e=>e.State=="processed"),ignored=events.Values.Count(e=>e.State=="ignored"),failed=events.Values.Count(e=>e.State=="failed"),lastReceivedAt=events.Values.Max(e=>(DateTimeOffset?)e.ReceivedAt),recent=events.Values.OrderByDescending(e=>e.ReceivedAt).Take(25).Select(e=>new{e.Id,e.EndpointId,e.State,e.Attempts,e.ReceivedAt,e.FinishedAt,e.Error}).ToArray()};}
    public void Retry(string id){lock(gate){if(!events.TryGetValue(id,out var e))throw new IntegrationException("not_found","Bildirim bulunamadı.",404);if(e.State!="failed")throw new IntegrationException("invalid_state","Yalnızca başarısız bildirim tekrar denenebilir.",409);e.State="pending";e.Attempts=0;e.DueAt=DateTimeOffset.UtcNow;e.Error=null;Save();}}
    public async Task Process(OrderWebhookReceiver receiver,OperationsHub operations,CancellationToken ct)
    {
        if(!await processing.WaitAsync(0,ct))return;try{
            OrderHookEvent[] due;lock(gate)due=events.Values.Where(e=>e.State=="pending"&&e.DueAt<=DateTimeOffset.UtcNow).OrderBy(e=>e.ReceivedAt).Take(25).Select(e=>JsonSerializer.Deserialize<OrderHookEvent>(JsonSerializer.Serialize(e,json),json)!).ToArray();
            foreach(var item in due){ct.ThrowIfCancellationRequested();string state="processed";string? error=null;try{
                var binding=receiver.Binding(item.EndpointId);if(binding==null||binding.Store.Id!=item.ConnectionId||CatalogHub.Source(binding.Store)!=item.Source||binding.SellerId!=item.SellerId){state="failed";error="Bildirim mağaza bağlantısı değişmiş veya devre dışı.";}
                else{var applied=operations.Observe(binding.Store,new MarketplacePage<MarketplaceOrder>([item.Order],0,1,1,1,null));if(applied==0)state="ignored";}
            }catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidOperationException){state="pending";error="Sipariş kaydedilemedi; otomatik tekrar denenecek.";}
                lock(gate){var stored=events[item.Id];stored.Attempts++;stored.State=state=="pending"&&stored.Attempts>=8?"failed":state;stored.Error=error;stored.DueAt=DateTimeOffset.UtcNow.AddSeconds(Math.Min(300,Math.Pow(2,stored.Attempts)));if(stored.State!="pending")stored.FinishedAt=DateTimeOffset.UtcNow;Save();}
            }
        }finally{processing.Release();}
    }
}
public sealed class OrderWebhookWorker(OrderWebhookInbox inbox,OrderWebhookReceiver receiver,OperationsHub operations,ILogger<OrderWebhookWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken){using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));try{while(await timer.WaitForNextTickAsync(stoppingToken))try{await inbox.Process(receiver,operations,stoppingToken);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidOperationException){log.LogWarning("Webhook inbox processing failed: {Type}",e.GetType().Name);}}catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}}
}
