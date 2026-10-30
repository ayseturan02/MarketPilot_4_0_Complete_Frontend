using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketPilot.Web.Catalog;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Synchronization;
using MarketPilot.Web.Wms;

namespace MarketPilot.Web.Enterprise;

public sealed record IntegrationEnvelope(string TenantId,string AccountId,string EventId,string CorrelationId,
    string EventType,string SourceSystem,long SourceVersion,string TargetSystem,DateTimeOffset OccurredAt,JsonElement Payload);
public sealed record IntegrationStockCommand(string Barcode,int Quantity,string? NativeSku=null);
public sealed class IntegrationOrchestrator(EnterpriseSync jobs,IServiceProvider services,IConfiguration config)
{
    public static readonly string[] EventTypes=["ProductChanged","PriceChanged","StockChanged","MarketplaceOrderReceived",
        "ErpOrderAccepted","WmsOrderAccepted","StockReserved","PickCompleted","PackageCompleted","ShipmentDispatched","OrderCancelled","ReturnAccepted"];
    public static string Hash<T>(T value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    public static string SourceEntity(IntegrationEnvelope e)
    {
        foreach(var key in new[]{"sourceOrderId","barcode","entityId"})
            if(e.Payload.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String)return value.GetString()!;
        return e.EventId;
    }
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public EnterpriseJob Accept(IntegrationEnvelope input)
    {
        if(input is null || input.TenantId!=(config["Workspace:BusinessId"]??"default") || string.IsNullOrWhiteSpace(input.AccountId) ||
            string.IsNullOrWhiteSpace(input.EventId) || string.IsNullOrWhiteSpace(input.CorrelationId) || !EventTypes.Contains(input.EventType) ||
            string.IsNullOrWhiteSpace(input.SourceSystem) || input.SourceVersion<0 || input.OccurredAt==default ||
            input.TargetSystem is not ("erp" or "wms" or "marketplace" or "catalog" or "audit") ||
            input.Payload.ValueKind!=JsonValueKind.Object ||
            new[]{input.AccountId,input.EventId,input.CorrelationId,input.SourceSystem}.Any(value=>value.Length>200))
            throw new ArgumentException("Merkezi olay kimliği, işletmesi, kaynağı veya hedefi geçersiz.");
        return jobs.EnqueueIntegration(input);
    }
    // Both poller and durable webhook inbox call this before committing their success/checkpoint.
    public void ObserveOrders(ConnectionState store,IEnumerable<MarketplaceOrder> orders,string? targetOverride=null)
    {
        var tenant=config["Workspace:BusinessId"]??"default";
        foreach(var order in orders)
        {
            var sourceId=string.IsNullOrWhiteSpace(order.PackageId)?order.OrderNumber:order.PackageId;
            var source=CatalogHub.Source(store);
            var id=Hash(new{tenant,store.Id,source,sourceId,order});
            var projection=ChannelContracts.For(store.Settings.Marketplace).Order(tenant,store.Id,order);
            var request=new ErpTransferRequest(tenant,store.Id,store.Settings.Marketplace,sourceId,"create",id,order,order.CustomerEmail,CentralOrder:projection);
            var workflowPolicy=services.GetService<FulfillmentOrchestrationService>()?.ReadPolicy();
            var target=targetOverride??(workflowPolicy?.Approved==true?"wms":"erp");
            var eventId=Hash(new{id,target});
            if(jobs.IntegrationEventExists(tenant,store.Id,eventId))continue;
            Accept(new(tenant,store.Id,eventId,id,"MarketplaceOrderReceived",source,order.LastModifiedDate??0,target,
                order.OrderDate??DateTimeOffset.UnixEpoch,JsonSerializer.SerializeToElement(request,Json)));
        }
    }
    public async Task ProcessAsync(EnterpriseJob job,CancellationToken ct)
    {
        var e=job.IntegrationEvent!;
        void Finish(string state,string result,string? error=null)=>jobs.IntegrationUpdate(job.Id,j=>
        {j.State=state;j.Result=result;j.Error=error;j.ProcessedAt=DateTimeOffset.UtcNow;
            j.NextAttemptAt=DateTimeOffset.UtcNow.AddSeconds(5);if(state=="completed")j.FinishedAt=j.ProcessedAt;});
        try
        {
            if(e.TargetSystem=="audit") {Finish("completed","observed-only; no target write");return;}
            if(job.Attempts==0 && jobs.HasNewerIntegrationSource(e))
            {Finish("blocked","stale-source-version","Daha yeni kaynak olayı mevcut; eski olay hedefe yazılmadı.");return;}
            if(e.SourceSystem.StartsWith("demo|",StringComparison.Ordinal)||e.SourceSystem.StartsWith("real|",StringComparison.Ordinal))
            {
                var registry=services.GetRequiredService<IntegrationRegistry>();
                if(!registry.Connections.TryGetValue(e.AccountId,out var store)||CatalogHub.Source(store)!=e.SourceSystem)
                    throw new ArgumentException("Kaynak hesap bağlantısı değişmiş; kullanıcı onayı gerekli.");
            }
            if(e.SourceVersion==0)throw new ArgumentException("Kaynak sürümü bilinmiyor; hedef yazma inceleme bekliyor.");
            if(e.TargetSystem=="catalog" && e.EventType=="ProductChanged")
            {
                var request=e.Payload.Deserialize<ChannelProductPreparation>(Json)??throw new ArgumentException("İlan eşleme verisi eksik.");
                if(request.AccountId!=e.AccountId)throw new ArgumentException("İlan hesabı uyuşmuyor.");
                var policy=jobs.GetErpTransferPolicy(e.TenantId,e.AccountId);
                if(policy==null||!policy.Approved)throw new ArgumentException("Onaylı ERP ilan eşleme politikası gerekli.");
                var projection=ChannelContracts.For(policy.Channel).Product(e.AccountId,request.Product,policy.Variants);
                Finish(projection.ReviewReasons.Length==0?"completed":"blocked",JsonSerializer.Serialize(projection,Json),
                    projection.ReviewReasons.Length==0?null:string.Join(" ",projection.ReviewReasons));return;
            }
            if(e.TargetSystem=="erp")
            {
                if(e.EventType is not ("MarketplaceOrderReceived" or "OrderCancelled" or "ReturnAccepted"))
                    throw new ArgumentException("Bu ERP olayının doğrulanmış yazma sözleşmesi yok.");
                var request=e.Payload.Deserialize<ErpTransferRequest>(Json)??throw new ArgumentException("ERP hazırlık verisi eksik.");
                if(request.TenantId!=e.TenantId || request.AccountId!=e.AccountId ||
                    e.EventType=="OrderCancelled"&&request.Operation!="cancel" || e.EventType=="ReturnAccepted"&&request.Operation!="return" ||
                    e.EventType=="MarketplaceOrderReceived"&&request.Operation is not ("create" or "correct"))
                    throw new ArgumentException("ERP işlem türü olay/hesap kimliğiyle uyuşmuyor.");
                var transfer=services.GetRequiredService<ErpOrderTransferService>();
                var plan=job.TransferPlan??transfer.Prepare(request);
                if(job.TransferPlan==null)jobs.IntegrationUpdate(job.Id,j=>j.TransferPlan=plan);
                var queryOnly=job.Attempts>0 || job.State is "waiting" or "uncertain";
                if(!queryOnly)jobs.IntegrationUpdate(job.Id,j=>{j.State="sending";j.Attempts++;});
                var outcome=await transfer.ExecuteAsync(plan,queryOnly,ct);
                jobs.IntegrationUpdate(job.Id,j=>j.ExternalReference=outcome.AcceptanceReference);
                if(outcome.State=="accepted")
                    Accept(e with{EventId=e.EventId+":accepted",SourceSystem="mock-erp",SourceVersion=1,OccurredAt=outcome.ConfirmedAt??e.OccurredAt,TargetSystem="audit",EventType="ErpOrderAccepted",
                        Payload=JsonSerializer.SerializeToElement(outcome,Json)});
                Finish(outcome.State switch{"accepted"=>"completed","pending"=>"waiting",_=>outcome.State},JsonSerializer.Serialize(outcome,Json),outcome.Error);
                return;
            }
            if(e.TargetSystem=="wms" && e.EventType=="MarketplaceOrderReceived")
            {
                var request=e.Payload.Deserialize<ErpTransferRequest>(Json)??throw new ArgumentException("WMS sipariş verisi eksik.");
                if(request.TenantId!=e.TenantId||request.AccountId!=e.AccountId||request.Operation!="create"||request.Order?.Lines==null||request.Order.Lines.Any(x=>x==null||x.UnitPrice==null) ||
                    ChannelContracts.For(request.Channel).Order(e.TenantId,e.AccountId,request.Order).ChannelManagedStock)
                    throw new ArgumentException("WMS hesap/kalem doğrulaması başarısız.");
                var workflow=services.GetRequiredService<FulfillmentOrchestrationService>();
                var order=new FulfillmentWorkflowOrder(request.Channel,e.AccountId,request.SourceOrderId,
                    Hash(new{e.TenantId,e.AccountId,request.SourceOrderId}),request.Order.Status,request.Order.OrderDate??DateTimeOffset.UnixEpoch,
                    request.Order.Currency,request.Order.Lines.Select(x=>new FulfillmentWorkflowOrderLine(x.Id,x.Sku,x.Quantity,"EA",x.UnitPrice!.Value,x.Currency)).ToArray());
                var record=await workflow.ReceiveAsync(order,ct,requirePreparedErpAcceptance:true);
                if(record.PolicySnapshot?.WriteSequence.Contains(FulfillmentWriteSteps.ErpOrderRegistration)==true)
                {
                    var transfer=services.GetRequiredService<ErpOrderTransferService>();
                    var plan=job.TransferPlan??transfer.Prepare(request);
                    if(job.TransferPlan==null)jobs.IntegrationUpdate(job.Id,j=>j.TransferPlan=plan);
                    if(record.ErpRegistrationState=="registered" && !jobs.HasErpAcceptance(plan.SourceIdentity,record.ErpOrderId??""))
                        throw new ArgumentException("Mevcut workflow ERP kaydı yeni aktarım sözleşmesiyle doğrulanmadı; kullanıcı incelemesi gerekli.");
                    await workflow.MarkErpPreparationValidatedAsync(record.Id,ct);
                    if(record.NextWriteStep<record.PolicySnapshot.WriteSequence.Length &&
                        record.PolicySnapshot.WriteSequence[record.NextWriteStep]==FulfillmentWriteSteps.ErpOrderRegistration && record.ErpRegistrationState!="registered")
                    {
                        var queryOnly=job.Attempts>0;
                        if(!queryOnly)jobs.IntegrationUpdate(job.Id,j=>{j.State="sending";j.Attempts++;});
                        var outcome=await transfer.ExecuteAsync(plan,queryOnly,ct);
                        jobs.IntegrationUpdate(job.Id,j=>j.ExternalReference=outcome.AcceptanceReference);
                        if(outcome.State!="accepted")
                        {Finish(outcome.State=="pending"?"waiting":outcome.State,JsonSerializer.Serialize(outcome,Json),outcome.Error);return;}
                        await workflow.ConfirmPreparedErpAcceptanceAsync(record.Id,outcome.AcceptanceReference!,ct);
                        Accept(e with{EventId=e.EventId+":accepted",EventType="ErpOrderAccepted",SourceSystem="mock-erp",SourceVersion=1,
                            OccurredAt=outcome.ConfirmedAt??e.OccurredAt,TargetSystem="audit",Payload=JsonSerializer.SerializeToElement(outcome,Json)});
                    }
                }
                if(record.State is not ("wms-accepted" or "processed"))record=await workflow.AdvanceAsync(record.Id,ct);
                else if(record.NextWriteStep<(record.PolicySnapshot?.WriteSequence.Length??0))record=await workflow.AdvanceAsync(record.Id,ct);
                var accepted=record.Warehouses.Length>0&&record.Warehouses.All(x=>x.AcceptanceState=="accepted")&&
                    record.NextWriteStep>=(record.PolicySnapshot?.WriteSequence.Length??0);
                if(accepted)PublishWmsEvent(e,record,"WmsOrderAccepted","wms","wms-accepted");
                Finish(accepted?"completed":record.State.StartsWith("blocked")||record.State=="wms-rejected"?"blocked":"waiting",
                    JsonSerializer.Serialize(new{workflowId=record.Id,state=record.State,accepted,processed=record.State=="processed"},Json),record.BlockReason);
                return;
            }
            if(e.TargetSystem=="wms" && e.EventType is "WmsOrderAccepted" or "StockReserved" or "PickCompleted" or "PackageCompleted")
            {
                if(!e.Payload.TryGetProperty("workflowId",out var id))throw new ArgumentException("WMS workflow kimliği eksik.");
                var workflow=services.GetRequiredService<FulfillmentOrchestrationService>();
                var record=workflow.Get(id.GetString()!);
                if(record.Order.AccountId!=e.AccountId)throw new ArgumentException("WMS workflow hesabı uyuşmuyor.");
                var (prerequisite,requiredState,next)=e.EventType switch
                {
                    "WmsOrderAccepted"=>("wms-accepted","reserved","StockReserved"),"StockReserved"=>("reserved","picked","PickCompleted"),
                    "PickCompleted"=>("picked","packed","PackageCompleted"),_=>("packed","processed","ShipmentDispatched")
                };
                if(!record.History.Any(h=>h.State==prerequisite))
                    throw new ArgumentException("WMS olayı için önceki aşama doğrulanmadı; sırasız işlem bloke.");
                bool Reached()=>next=="ShipmentDispatched"
                    ? record.State is "processed" or "partially-shipped" && record.Packages.Count>0 && record.Publications.All(publication=>publication.State=="acknowledged")
                    : record.History.Any(h=>h.State==requiredState);
                if(!Reached())record=await workflow.AdvanceAsync(record.Id,ct);
                var reached=Reached();
                if(reached)PublishWmsEvent(e,record,next,next=="ShipmentDispatched"?"audit":"wms",requiredState);
                Finish(reached?"completed":record.State.StartsWith("blocked")||record.State=="wms-rejected"?"blocked":"waiting",
                    JsonSerializer.Serialize(new{workflowId=record.Id,state=record.State,packages=record.Packages,processed=record.State=="processed"},Json),record.BlockReason);
                return;
            }
            if(e.TargetSystem=="marketplace" && e.EventType=="StockChanged")
            {
                var registry=services.GetRequiredService<IntegrationRegistry>();
                if(!registry.Connections.TryGetValue(e.AccountId,out var store)||store.Settings.Mode!="demo")
                    throw new ArgumentException("Merkezi orkestratör yalnız mock stok aktarabilir; gerçek yazma kapalı.");
                var stock=services.GetRequiredService<StockOutbox>();
                var command=e.Payload.Deserialize<IntegrationStockCommand>(Json)??throw new ArgumentException("Stok verisi eksik.");
                var native=stock.Enqueue(store,command.Barcode,command.Quantity,null,"IntegrationOrchestrator",nativeSku:command.NativeSku,requestId:job.Key);
                Finish(native.State=="completed"?"completed":native.State is "failed" or "blocked"?"blocked":"waiting",
                    JsonSerializer.Serialize(new{outboxId=native.Id,state=native.State},Json),native.Error);return;
            }
            // Catalog/price changes require existing ERP ownership and price approval workflows.
            // Unsupported commands are visible, never acknowledged as successful target writes.
            throw new ArgumentException(e.EventType=="PriceChanged"?"ERP fiyat önizleme/onay akışı gerekli; otomatik fiyat yazılmaz.":
                "Bu olay/hedef için onaylı işlem sözleşmesi yok; hazırlık kullanıcı incelemesi bekliyor.");
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(ArgumentException error){Finish("blocked","validation-blocked",error.Message);}
        catch(IntegrationException error){Finish("intervention-required","target-conflict",error.Message);}
        catch(Exception error)when(error is IOException or TimeoutException or HttpRequestException)
        {Finish("uncertain","query-target-before-any-retry","Mock hedef erişilemiyor veya sonuç belirsiz; kaynak sipariş kalıcı işte korunuyor.");}
    }
    private void PublishWmsEvent(IntegrationEnvelope parent,FulfillmentWorkflowRecord record,string eventType,string target,string state)
    {
        var eventId=Hash(new{record.Id,eventType,parent.TenantId,parent.AccountId});
        if(jobs.IntegrationEventExists(parent.TenantId,parent.AccountId,eventId))return;
        var history=record.History.FirstOrDefault(h=>h.State==state)??record.History.Last();
        Accept(parent with{EventId=eventId,EventType=eventType,
            SourceSystem="mock-wms",SourceVersion=record.History.IndexOf(history)+1,TargetSystem=target,OccurredAt=history.At,
            Payload=JsonSerializer.SerializeToElement(new{workflowId=record.Id,sourceOrderId=record.Order.ExternalOrderId},Json)});
    }
}
