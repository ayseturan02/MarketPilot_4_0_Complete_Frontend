using System.Text.Json;
using MarketPilot.Web.Integrations;

namespace MarketPilot.Web.Enterprise;

// The existing enterprise job store and worker remain the only execution queue.
public sealed partial class EnterpriseSync
{
    internal MarketPilot.Web.Wms.WmsMockProviderState? ReadMockWmsState(string tenant,string profile)
    {lock(gate)return state.MockWmsProviderStates.TryGetValue(IntegrationOrchestrator.Hash(new{tenant,profile}),out var value)?Clone(value):null;}
    internal void SaveMockWmsState(string tenant,string profile,MarketPilot.Web.Wms.WmsMockProviderState value)
    {lock(gate){state.MockWmsProviderStates[IntegrationOrchestrator.Hash(new{tenant,profile})]=Clone(value);Save();}}
    public ErpTransferPolicy? GetErpTransferPolicy(string tenant, string account)
    { lock(gate) return state.ErpTransferPolicies.TryGetValue(IntegrationOrchestrator.Hash(new {tenant,account}),out var p)?Clone(p):null; }
    public void ConfigureErpTransfer(ErpTransferPolicy policy)
    {
        if(policy.TenantId != (config["Workspace:BusinessId"]??"default") || string.IsNullOrWhiteSpace(policy.AccountId) ||
            policy.Customers==null || policy.Currencies==null || policy.VatRates==null || policy.Variants==null ||
            policy.CustomerPolicy is not ("fixed-account" or "existing-customer") || policy.VatRates.Any(x=>x<0||x>100) ||
            !ChannelContracts.All.Any(x=>x.Channel==policy.Channel))
            throw new ArgumentException("ERP aktarım politikası veya işletme kimliği geçersiz.");
        lock(gate){state.ErpTransferPolicies[IntegrationOrchestrator.Hash(new{tenant=policy.TenantId,account=policy.AccountId})]=Clone(policy);Save();}
    }
    public EnterpriseJob EnqueueIntegration(IntegrationEnvelope input)
    {
        var key=IntegrationOrchestrator.Hash(new{input.TenantId,input.AccountId,input.EventId});
        lock(gate)
        {
            var existing=state.Jobs.FirstOrDefault(x=>x.Kind=="integration-event"&&x.Key==key);
            if(existing!=null)
            {
                if(JsonSerializer.Serialize(existing.IntegrationEvent,json)!=JsonSerializer.Serialize(input,json))
                    throw new IntegrationException("idempotency_conflict","Olay kimliği farklı içerikle tekrarlandı.",409);
                return Clone(existing);
            }
            if(state.Jobs.Count>=10000)throw new IntegrationException("outbox_full","Kalıcı iş deposu dolu; kaynak checkpoint ilerletilmedi.",503);
            var job=new EnterpriseJob{Kind="integration-event",Key=key,Connector=input.TargetSystem,StoreId=input.AccountId,
                Source=input.SourceSystem,AccountIdentity=input.TenantId+":"+input.AccountId,Reference=input.EventId,
                IntegrationEvent=Clone(input),Payload=input.Payload.GetRawText()};
            var entity=IntegrationOrchestrator.SourceEntity(input);
            if(state.Jobs.Any(x=>x.IntegrationEvent is {} prior && prior.TenantId==input.TenantId && prior.AccountId==input.AccountId &&
                prior.EventType==input.EventType && prior.SourceSystem==input.SourceSystem && prior.TargetSystem==input.TargetSystem &&
                IntegrationOrchestrator.SourceEntity(prior)==entity && prior.SourceVersion>input.SourceVersion))
            {job.State="blocked";job.Error="Eski kaynak sürümü; daha yeni olay korundu.";job.Result="stale-source-version";}
            if(input.EventType=="StockChanged" && state.Jobs.Any(x=>x.IntegrationEvent is {} prior &&
                prior.TenantId==input.TenantId&&prior.AccountId==input.AccountId&&prior.SourceSystem==input.SourceSystem&&
                prior.EventType==input.EventType&&prior.TargetSystem==input.TargetSystem&&prior.SourceVersion==input.SourceVersion&&
                IntegrationOrchestrator.SourceEntity(prior)==entity&&prior.Payload.GetRawText()!=input.Payload.GetRawText()))
            {job.State="blocked";job.Error="Aynı kaynak sürümünde farklı stok; uzlaştırma gerekli.";job.Result="source-version-conflict";}
            state.Jobs.Insert(0,job);Save();return Clone(job);
        }
    }
    public EnterpriseJob[] IntegrationJobs() {lock(gate)return state.Jobs.Where(x=>x.IntegrationEvent!=null).Select(Clone).ToArray();}
    internal bool IntegrationEventExists(string tenant,string account,string eventId)
    {lock(gate)return state.Jobs.Any(x=>x.IntegrationEvent is {} e&&e.TenantId==tenant&&e.AccountId==account&&e.EventId==eventId);}
    internal void IntegrationUpdate(string id,Action<EnterpriseJob> change)=>UpdateJob(id,change);
    public void RetryIntegration(string id)
    {
        lock(gate)
        {
            var job=state.Jobs.SingleOrDefault(x=>x.Id==id&&x.IntegrationEvent!=null)??throw new ArgumentException("Olay bulunamadı.");
            if(job.State=="completed")throw new ArgumentException("Tamamlanan olay tekrar yazılamaz.");
            if(job.Result is "stale-source-version" or "source-version-conflict")throw new ArgumentException("Eski/çelişkili kaynak sürümü tekrar yazılamaz.");
            if(job.State=="intervention-required" || job.State=="reconciliation-required")throw new ArgumentException("ERP reddi/mutabakat farkı ayrı düzeltme işlemi gerektirir.");
            job.State=job.Attempts>0?"uncertain":"queued";job.NextAttemptAt=DateTimeOffset.UtcNow;job.Error=null;Save();
        }
    }
    internal bool HasErpAcceptance(string source,string reference)
    {lock(gate)return state.MockErpTransferReceipts.Values.Any(x=>x.SourceIdentity==source&&x.Operation is "create" or "correct"&&x.State=="accepted"&&x.AcceptanceReference==reference);}
    internal bool HasErpRejection(string source,string key)
    {lock(gate)return state.MockErpTransferReceipts.TryGetValue(key,out var r)&&r.SourceIdentity==source&&r.State=="rejected";}
    internal MockErpTransferReceipt MockErpSubmit(ErpTransferPlan plan)
    {
        lock(gate)
        {
            var fingerprint=IntegrationOrchestrator.Hash(plan with {MockScenario="success"});
            if(state.MockErpTransferReceipts.TryGetValue(plan.RequestKey,out var old))
            {
                if(old.Fingerprint!=fingerprint)throw new IntegrationException("erp_duplicate_conflict","Aynı ERP kaynak kimliği farklı sipariş içeriyor; kullanıcı müdahalesi gerekli.",409);
                return Clone(old);
            }
            var status=plan.MockScenario switch{"rejected"=>"rejected","pending"=>"pending",_=>"accepted"};
            var receipt=new MockErpTransferReceipt(plan.RequestKey,status,status=="accepted"?"MOCK-ERP-"+plan.RequestKey[..16]:null,
                status=="accepted"?plan.MarketplaceTotal+(plan.MockScenario=="mismatch"?1:0):null,plan.Currency,
                status=="rejected"?"Mock ERP siparişi reddetti; kullanıcı müdahalesi gerekli.":null,
                Fingerprint:fingerprint,SourceIdentity:plan.SourceIdentity,Operation:plan.Operation,ConfirmedAt:status=="accepted"?DateTimeOffset.UtcNow:null);
            state.MockErpTransferReceipts.Add(plan.RequestKey,receipt);Save();return Clone(receipt);
        }
    }
    internal MockErpTransferReceipt? MockErpQuery(string key)
    {
        lock(gate)
        {
            if(!state.MockErpTransferReceipts.TryGetValue(key,out var old))return null;
            var receipt=old with{Queries=old.Queries+1};
            if(receipt.State=="pending"&&receipt.Queries>=2)
            {
                var plan=state.Jobs.Where(j=>j.TransferPlan?.RequestKey==key).Select(j=>j.TransferPlan!).FirstOrDefault();
                if(plan!=null)receipt=receipt with{State="accepted",AcceptanceReference="MOCK-ERP-"+key[..16],ErpTotal=plan.MarketplaceTotal,ConfirmedAt=DateTimeOffset.UtcNow};
            }
            state.MockErpTransferReceipts[key]=receipt;Save();return Clone(receipt);
        }
    }
}
