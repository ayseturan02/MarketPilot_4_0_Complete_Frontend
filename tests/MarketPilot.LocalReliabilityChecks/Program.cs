using MarketPilot.Web.Local;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Demo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
static void Reject(Action action){try{action();}catch(ArgumentException){return;}catch(InvalidOperationException){return;}catch(CryptographicException){return;}throw new Exception("Invalid action accepted");}
var folder=Path.Combine(Path.GetTempPath(),"marketpilot-local-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
try
{
 var env=new LocalTestEnvironment{ContentRootPath=folder};var accounts=new LocalAccountStore(env);Check(!accounts.Configured,"Unexpected configured accounts");
 var admin=accounts.Create("test-admin","Test-Only-Password-2026","admin",true);Check(accounts.Configured,"Setup missing");
 Reject(()=>accounts.Create("other-admin","Test-Only-Password-2026","admin",true));
 var user=accounts.Create("test-viewer","Viewer-Only-Password-2026","viewer");var login=accounts.Authenticate("test-viewer","Viewer-Only-Password-2026");Check(login is not null,"Login failed");Check(accounts.Authenticate("test-viewer","wrong") is null,"Wrong password accepted");
 var accountText=File.ReadAllText(Path.Combine(folder,"Data","local-accounts.json"));Check(!accountText.Contains("Test-Only-Password-2026")&&!accountText.Contains("Viewer-Only-Password-2026"),"Password stored in plaintext");
 accounts.Change(user.Id,"operator",true,admin.Id);Check(!accounts.ValidateSession(user.Id,login!.Value.Version.ToString()),"Old role session survived");accounts.Change(user.Id,"operator",false,admin.Id);Check(accounts.Authenticate("test-viewer","Viewer-Only-Password-2026") is null,"Disabled account logged in");
 Reject(()=>accounts.Change(admin.Id,"viewer",true,admin.Id));Check(new LocalAccountStore(env).Authenticate("test-admin","Test-Only-Password-2026") is not null,"Account persistence failed");
 var workspace=new DemoWorkspace(env);var store=new ConnectionSettings("Background test","trendyol","demo","stage");var initial=workspace.Snapshot(store);workspace.Enqueue(store,new("stock","persisted-job",initial.Products[0].Barcode,47,499m));
 // Reconstruct the service before the due time; process without calling Snapshot or a browser.
 workspace=new DemoWorkspace(env);await Task.Delay(2200);Check(workspace.ProcessPendingJobs()==1,"Background processor did not complete resumed job");
 Check(workspace.ProcessPendingJobs()==0,"Job processed twice");Check(new DemoWorkspace(env).Snapshot(store).Products[0].Stock==47,"Processed job not persisted");
 Reject(()=>workspace.Enqueue(store,new("stock","persisted-job",initial.Products[0].Barcode,48,499m)));
 var backups=new LocalBackups(env);var backup=backups.Create();Check(backups.List().Count==1,"Backup not listed");backups.Verify(backup.Name);
 var archive=backups.Download(backup.Name);Check(!System.Text.Encoding.UTF8.GetString(archive).Contains("test-admin"),"Archive not encrypted");
 var envelope=JsonSerializer.Deserialize<BackupEnvelope>(archive)!;var cipher=Convert.FromBase64String(envelope.Cipher);cipher[0]^=1;var altered=envelope with{Cipher=Convert.ToBase64String(cipher)};var archivePath=Path.Combine(folder,"Data","Backups",backup.Name);File.WriteAllText(archivePath,JsonSerializer.Serialize(altered));Reject(()=>backups.Verify(backup.Name));
 File.WriteAllBytes(archivePath,archive);new LocalBackups(env).Verify(backup.Name);Reject(()=>backups.Download("../local-accounts.json"));
 // A second process on the same Data folder must be refused; the folder is free again after release.
 using(DataDirectoryLock.Acquire(folder)){Reject(()=>DataDirectoryLock.Acquire(folder));}using(DataDirectoryLock.Acquire(folder)){}

 // Exported decimal values and quoting must stay stable under the operator's locale.
 var previousCulture=CultureInfo.CurrentCulture;
 try
 {
     var product=new Product{Id="CSV-1",Name="Virgül, \"tırnak\"",Sku="=FORMULA",Category="Giyim",Price=1234.50m,Stock=1.5m,Status="Aktif"};
     byte[]? canonical=null;
     foreach(var locale in new[]{"tr-TR","de-DE","en-US"})
     {
         CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(locale);
         var bytes=ProductCsv.Create([product]);
         canonical??=bytes;
         Check(bytes.SequenceEqual(canonical),"CSV output changed under "+locale);
         Check(bytes.Take(3).SequenceEqual(Encoding.UTF8.GetPreamble()),"CSV UTF-8 BOM missing");
         var csv=Encoding.UTF8.GetString(bytes);
         Check(csv.Contains("\"Virgül, \"\"tırnak\"\"\"")&&csv.Contains("\"'=FORMULA\""),"CSV escaping or formula protection lost");
         Check(csv.Contains(",1234.50,1.5,\"Aktif\""),"CSV numeric columns merged under "+locale);
     }
 }
 finally{CultureInfo.CurrentCulture=previousCulture;}

 // Unexpected data/XML faults must leave the workers alive and must allow a clean shutdown.
 var workerEnv=new LocalTestEnvironment{ContentRootPath=Path.Combine(folder,"worker-faults")};
 Directory.CreateDirectory(Path.Combine(workerEnv.ContentRootPath,"Data","keys"));
 var failedState=new DemoWorkspaceState{Products=null!};
 failedState.Jobs.Add(new DemoJob{Kind="stock",Reference="fixture",Quantity=1,Price=1m,DueAt=DateTimeOffset.UtcNow.AddSeconds(-1)});
 File.WriteAllText(Path.Combine(workerEnv.ContentRootPath,"Data","integration-demo.json"),
     JsonSerializer.Serialize(new Dictionary<string,DemoWorkspaceState>{{"trendyol|stage|Fault",failedState}},new JsonSerializerOptions(JsonSerializerDefaults.Web)));
 File.WriteAllText(Path.Combine(workerEnv.ContentRootPath,"Data","keys","invalid.xml"),"<unclosed>");
 var workerAudit=new LocalAudit(workerEnv);
 var queueState=new ReliabilityState();var backupState=new ReliabilityState();
 var queueLog=new CapturingLogger<DemoQueueWorker>();var backupLog=new CapturingLogger<LocalBackupWorker>();
 using var queueWorker=new DemoQueueWorker(new DemoWorkspace(workerEnv),queueState,workerAudit,queueLog);
 using var backupWorker=new LocalBackupWorker(new LocalBackups(workerEnv),backupState,workerAudit,backupLog);
 await backupWorker.StartAsync(default);
 await queueWorker.StartAsync(default);
 try
 {
     for(var attempt=0;attempt<100&&queueState.LastQueueTick is null;attempt++)await Task.Delay(25);
     var failedQueue=JsonSerializer.Deserialize<Dictionary<string,DemoWorkspaceState>>(File.ReadAllText(Path.Combine(workerEnv.ContentRootPath,"Data","integration-demo.json")),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
     Check(failedQueue.Values.Single().Jobs[0].State=="failed"&&!string.IsNullOrWhiteSpace(failedQueue.Values.Single().Jobs[0].Error),"Unexpected job error was not quarantined and recorded");
     Check(backupState.LastBackupError is not null&&backupLog.Messages.Any(m=>m.Contains("XmlException")),"Unexpected backup XML error was not recorded");
     Check(queueWorker.ExecuteTask is{IsCompleted:false}&&backupWorker.ExecuteTask is{IsCompleted:false},"Unexpected fault stopped a worker");
     Check(backupState.LastBackupSuccess is null,"A failed backup was reported as successful");
 }
 finally
 {
     using var shutdown=new CancellationTokenSource(TimeSpan.FromSeconds(5));
     await queueWorker.StopAsync(shutdown.Token);await backupWorker.StopAsync(shutdown.Token);
 }
 Check(queueWorker.ExecuteTask?.IsCompletedSuccessfully==true&&backupWorker.ExecuteTask?.IsCompletedSuccessfully==true,"Shutdown cancellation faulted a worker");
 Console.WriteLine("PASS: hashed local accounts, role/session revocation, disabled users, persistence, restart-resumed background jobs, idempotency conflict, encrypted backup roundtrip, tamper rejection, traversal protection single-process data lock, locale-independent CSV and unexpected worker failures/clean shutdown. Temporary data only.");
}
finally
{
 var absolute=Path.GetFullPath(folder);var root=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
 if(!absolute.StartsWith(root,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(absolute).StartsWith("marketpilot-local-check-"))throw new Exception("Unsafe cleanup path");Directory.Delete(absolute,true);
}
sealed class LocalTestEnvironment:IWebHostEnvironment
{
 public string ApplicationName{get;set;}="MarketPilot.Web";public string EnvironmentName{get;set;}="Development";public string ContentRootPath{get;set;}="";public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();public string WebRootPath{get;set;}="";public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
}

sealed class CapturingLogger<T>:ILogger<T>
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Messages{get;}=new();
    public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
    public bool IsEnabled(LogLevel level)=>true;
    public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter)=>Messages.Enqueue(formatter(state,exception));
}
