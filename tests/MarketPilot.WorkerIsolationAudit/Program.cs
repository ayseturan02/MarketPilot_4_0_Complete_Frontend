using System.Text.Json;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Demo;
using MarketPilot.Web.Local;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

var folder=Path.Combine(Path.GetTempPath(),"marketpilot-worker-isolation-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var env=new AuditEnvironment {ContentRootPath=folder};
    var settings=new ConnectionSettings("Healthy","trendyol","demo","stage");
    var healthy=new DemoWorkspace(env).Snapshot(settings);
    healthy.Jobs.Clear();
    healthy.Jobs.Add(new DemoJob {Kind="stock",Reference=healthy.Products[0].Barcode,Quantity=41,Price=449,RequestId="healthy-audit-job",DueAt=DateTimeOffset.UtcNow.AddSeconds(-1)});
    var bad=new DemoWorkspaceState {Products=null!};
    bad.Jobs.Add(new DemoJob {Kind="stock",Reference="bad",Quantity=1,Price=1,DueAt=DateTimeOffset.UtcNow.AddSeconds(-1)});
    var file=Path.Combine(folder,"Data","integration-demo.json");
    var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
    File.WriteAllText(file,JsonSerializer.Serialize(new Dictionary<string,DemoWorkspaceState>{{"trendyol|stage|Fault",bad},{"trendyol|stage|Healthy",healthy}},json));
    var state=new ReliabilityState();
    using var worker=new DemoQueueWorker(new DemoWorkspace(env),state,new LocalAudit(env),NullLogger<DemoQueueWorker>.Instance);
    await worker.StartAsync(default);
    try
    {
        for(var i=0;i<100;i++)
        {
            var snapshot=JsonSerializer.Deserialize<Dictionary<string,DemoWorkspaceState>>(File.ReadAllText(file),json)!;
            if(snapshot["trendyol|stage|Fault"].Jobs[0].State=="failed"&&snapshot["trendyol|stage|Healthy"].Jobs[0].State=="completed")break;
            await Task.Delay(50);
        }
        var persisted=JsonSerializer.Deserialize<Dictionary<string,DemoWorkspaceState>>(File.ReadAllText(file),json)!;
        var status=persisted["trendyol|stage|Healthy"].Jobs.Single(j=>j.RequestId=="healthy-audit-job").State;
        var failed=persisted["trendyol|stage|Fault"].Jobs[0];
        if(status!="completed"||failed.State!="failed"||worker.ExecuteTask is not {IsCompleted:false})Environment.ExitCode=1;
        Console.WriteLine(JsonSerializer.Serialize(new {workerAlive=worker.ExecuteTask is {IsCompleted:false},errorRecorded=failed.State=="failed"&&!string.IsNullOrWhiteSpace(failed.Error),healthyJobState=status,meetsRequirement=status=="completed"&&failed.State=="failed"&&worker.ExecuteTask is {IsCompleted:false},scenario="An unexpected error in the first demo workspace must not block a due healthy job in another workspace."}));
    }
    finally {using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));await worker.StopAsync(stop.Token);}
}
finally
{
    var target=Path.GetFullPath(folder);var tempRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
    if(!target.StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(target).StartsWith("marketpilot-worker-isolation-"))throw new Exception("Unsafe audit cleanup");
    Directory.Delete(target,true);
}
sealed class AuditEnvironment:IWebHostEnvironment
{
    public string ApplicationName{get;set;}="MarketPilot.Web";public string EnvironmentName{get;set;}="Production";public string ContentRootPath{get;set;}="";public string WebRootPath{get;set;}="";
    public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
}
