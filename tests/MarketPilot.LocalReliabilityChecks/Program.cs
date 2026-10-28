using MarketPilot.Web.Local;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Demo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using System.Security.Cryptography;
using System.Text.Json;

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
 Console.WriteLine("PASS: hashed local accounts, role/session revocation, disabled users, persistence, restart-resumed background jobs, idempotency conflict, encrypted backup roundtrip, tamper rejection, traversal protection and single-process data lock. Temporary data only.");
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
