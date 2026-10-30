using System.Text.Json;
using MarketPilot.Web.Enterprise;
using MarketPilot.Web.Integrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

var root=Path.Combine(Path.GetTempPath(),"marketpilot-orchestrator-checks-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Check(bool ok,string reason){if(!ok)throw new Exception(reason);}
try
{
    using var services=new ServiceCollection().BuildServiceProvider();
    var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Workspace:BusinessId"]="default"}).Build();
    var environment=new TestEnvironment(root);
    var jobs=new EnterpriseSync(environment,services,config);
    var orchestrator=new IntegrationOrchestrator(jobs,services,config);
    IntegrationEnvelope Event(string id,long version=1,string account="seller")=>new("default",account,id,"correlation-"+id,
        "StockChanged","mock-wms",version,"marketplace",DateTimeOffset.UtcNow,JsonSerializer.SerializeToElement(new{barcode="BC1",quantity=3}));
    var older=Event("older");
    var accepted=orchestrator.Accept(older);
    Check(orchestrator.Accept(older).Id==accepted.Id,"Repeated event duplicated durable work");
    try{orchestrator.Accept(older with{CorrelationId="different-content"});throw new Exception("Conflict accepted");}
    catch(IntegrationException error){Check(error.Code=="idempotency_conflict","Wrong duplicate conflict");}
    orchestrator.Accept(Event("newer",2));
    await orchestrator.ProcessAsync(accepted,default);
    var blocked=jobs.IntegrationJobs().Single(job=>job.Id==accepted.Id);
    Check(blocked.State=="blocked"&&blocked.Result=="stale-source-version"&&blocked.ProcessedAt!=null&&blocked.Error!=null,
        "An event superseded after intake was not blocked before target access");
    Check(blocked.Attempts==0,"Old stock caused a target write");
    var otherAccount=orchestrator.Accept(Event("other-account",1,"seller-b"));
    Check(otherAccount.State=="queued","Different account affected source ordering");
    foreach(var type in IntegrationOrchestrator.EventTypes)
    {
        var job=orchestrator.Accept(Event("audit-"+type) with{EventType=type,TargetSystem="audit"});
        await orchestrator.ProcessAsync(job,default);
        Check(jobs.IntegrationJobs().Single(saved=>saved.Id==job.Id).State=="completed","Audit event type failed");
    }
    var restarted=new EnterpriseSync(environment,services,config);
    Check(restarted.IntegrationJobs().Length==jobs.IntegrationJobs().Length,"Restart lost accepted events");
    Check(restarted.IntegrationJobs().Single(job=>job.Id==otherAccount.Id).State=="queued","Pending order/event not restartable");
    Check(Directory.GetFiles(Path.Combine(root,"Data")).Select(Path.GetFileName).SequenceEqual(new[]{"enterprise-sync.json"}),
        "Orchestrator created a second queue");
    Console.WriteLine("PASS central orchestrator: existing durable queue, all 12 event envelopes, exact replay/conflict, stale-after-intake protection, tenant/account ordering isolation, execution timestamps/results/errors and restart persistence. No provider/network access.");
}
finally
{
    if(Path.GetDirectoryName(Path.GetFullPath(root))!=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)||!Path.GetFileName(root).StartsWith("marketpilot-orchestrator-checks-"))throw new Exception("Unsafe cleanup");
    Directory.Delete(root,true);
}
sealed class TestEnvironment(string root):IWebHostEnvironment
{
    public string EnvironmentName{get;set;}="Production";
    public string ApplicationName{get;set;}="OrchestratorChecks";
    public string ContentRootPath{get;set;}=root;
    public string WebRootPath{get;set;}=root;
    public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();
    public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
}
