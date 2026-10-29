using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketPilot.Web.Integrations.Demo;

namespace MarketPilot.Web.Local;

public sealed record LocalAuditEvent(DateTimeOffset Date, string Level, string Category, string Message);
public sealed class LocalAudit
{
    private readonly object gate = new(); private readonly string file; private List<LocalAuditEvent> events;
    public LocalAudit(IWebHostEnvironment env)
    {
        var folder = Path.Combine(env.ContentRootPath, "Data"); Directory.CreateDirectory(folder); file = Path.Combine(folder, "system-events.json");
        events = File.Exists(file) ? JsonSerializer.Deserialize<List<LocalAuditEvent>>(File.ReadAllText(file)) ?? new() : new();
    }
    public void Add(string level, string category, string message)
    {
        lock(gate) { events.Insert(0, new(DateTimeOffset.UtcNow, level, category, message)); events = events.Take(200).ToList(); var tmp = file + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(events)); File.Move(tmp, file, true); }
    }
    public IReadOnlyList<LocalAuditEvent> Recent() { lock(gate) return events.ToArray(); }
}
public sealed class ReliabilityState
{
    public string? LastQueueTick { get; set; }
    public string? LastQueueError { get; set; }
    public string? LastBackupSuccess { get; set; }
    public string? LastBackupError { get; set; }
}
public sealed record BackupInfo(string Name, DateTimeOffset CreatedAt, long Size);
public sealed record BackupEnvelope(int Version, string Nonce, string Tag, string Cipher);
public sealed record BackupManifest(DateTimeOffset CreatedAt, Dictionary<string,string> Hashes);
public sealed class LocalBackups
{
    private readonly object gate = new(); private readonly string data,folder; private readonly byte[] key;
    private static readonly string[] Files = ["demo-store.json", "integration-demo.json", "integration-connections.json", "local-accounts.json", "system-events.json", "central-stock.json", "central-stock-real.json", "order-webhook-inbox.json", "marketplace-outbox.json", "synchronization.json", "enterprise-sync.json"];
    private const long MaxBytes = 256L * 1024 * 1024;
    public LocalBackups(IWebHostEnvironment env)
    {
        data = Path.GetFullPath(Path.Combine(env.ContentRootPath, "Data")); folder = Path.Combine(data,"Backups"); Directory.CreateDirectory(folder);
        var keyFile=Path.Combine(data,"backup.key");
        if(!File.Exists(keyFile)) { using var stream = new FileStream(keyFile,FileMode.CreateNew,FileAccess.Write,FileShare.None); var bytes=Encoding.ASCII.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))); stream.Write(bytes); }
        key=Convert.FromBase64String(File.ReadAllText(keyFile).Trim()); if(key.Length!=32)throw new InvalidOperationException("Invalid backup key.");
    }
    private static bool Allowed(string name) => Files.Contains(name) || Regex.IsMatch(name,@"^keys/[a-zA-Z0-9_-]+\.xml$");
    private string Target(string name)
    {
        if(!Regex.IsMatch(name,@"^\d{8}T\d{6}-[a-f0-9]{8}\.itbackup$"))throw new ArgumentException("Geçersiz yedek adı.");
        var target=Path.GetFullPath(Path.Combine(folder,name)); if(!target.StartsWith(folder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Geçersiz yedek yolu."); return target;
    }
    public IReadOnlyList<BackupInfo> List()
    {
        lock(gate) return Directory.EnumerateFiles(folder,"*.itbackup").Select(p=>new FileInfo(p)).Where(f=>Regex.IsMatch(f.Name,@"^\d{8}T\d{6}-[a-f0-9]{8}\.itbackup$")).OrderByDescending(f=>f.Name).Select(f=>new BackupInfo(f.Name,f.LastWriteTimeUtc,f.Length)).ToArray();
    }
    public BackupInfo Create()
    {
        lock(gate)
        {
            var manifest=new BackupManifest(DateTimeOffset.UtcNow,new()); using var compressed=new MemoryStream(); long total=0;
            using(var zip=new ZipArchive(compressed,ZipArchiveMode.Create,true))
            {
                var names=Files.Where(f=>File.Exists(Path.Combine(data,f))).ToList(); var keys=Path.Combine(data,"keys"); if(Directory.Exists(keys))names.AddRange(Directory.EnumerateFiles(keys,"*.xml").Select(p=>"keys/"+Path.GetFileName(p)));
                if(names.Count==0)throw new InvalidOperationException("Yedeklenecek veri bulunamadı.");
                foreach(var name in names)
                {
                    if(!Allowed(name))continue;
                    var path=Path.Combine(data,name.Replace('/',Path.DirectorySeparatorChar)); var info=new FileInfo(path); total+=info.Length; if(total>MaxBytes)throw new InvalidOperationException("Yerel yedek boyut sınırı aşıldı.");
                    var bytes=File.ReadAllBytes(path); if(name.EndsWith(".json"))using(JsonDocument.Parse(bytes)){}else System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
                    manifest.Hashes[name]=Convert.ToHexString(SHA256.HashData(bytes)); using var entry=zip.CreateEntry(name,CompressionLevel.Optimal).Open(); entry.Write(bytes);
                }
                using var meta=zip.CreateEntry("manifest.json").Open(); JsonSerializer.Serialize(meta,manifest);
            }
            var plain=compressed.ToArray(); var cipher=new byte[plain.Length]; var nonce=RandomNumberGenerator.GetBytes(12); var tag=new byte[16]; using(var aes=new AesGcm(key,16))aes.Encrypt(nonce,plain,cipher,tag,Encoding.UTF8.GetBytes("InternetTekstilBackup:v1"));
            var nameOut=DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]+".itbackup"; var target=Target(nameOut); var temp=target+".tmp";
            File.WriteAllText(temp,JsonSerializer.Serialize(new BackupEnvelope(1,Convert.ToBase64String(nonce),Convert.ToBase64String(tag),Convert.ToBase64String(cipher)))); File.Move(temp,target);
            Verify(nameOut);
            foreach(var old in List().Skip(14))File.Delete(Target(old.Name));
            var saved=new FileInfo(target);return new(nameOut,saved.LastWriteTimeUtc,saved.Length);
        }
    }
    public object Verify(string name)
    {
        lock(gate){var result=ReadArchive(name);return new {ok=true,files=result.Files.Count,createdAt=result.Manifest.CreatedAt};}
    }
    private (BackupManifest Manifest,Dictionary<string,byte[]> Files) ReadArchive(string name)
    {
            var path=Target(name);if(new FileInfo(path).Length>MaxBytes)throw new InvalidOperationException("Yedek boyutu geçersiz.");var env=JsonSerializer.Deserialize<BackupEnvelope>(File.ReadAllText(path))??throw new InvalidOperationException("Geçersiz yedek.");if(env.Version!=1)throw new InvalidOperationException("Desteklenmeyen yedek sürümü.");
            var cipher=Convert.FromBase64String(env.Cipher);var plain=new byte[cipher.Length];using(var aes=new AesGcm(key,16))aes.Decrypt(Convert.FromBase64String(env.Nonce),cipher,Convert.FromBase64String(env.Tag),plain,Encoding.UTF8.GetBytes("InternetTekstilBackup:v1"));
            using var stream=new MemoryStream(plain);using var zip=new ZipArchive(stream,ZipArchiveMode.Read);var meta=zip.GetEntry("manifest.json")??throw new InvalidOperationException("Yedek bilgisi eksik.");using var reader=meta.Open();var manifest=JsonSerializer.Deserialize<BackupManifest>(reader)??throw new InvalidOperationException("Yedek bilgisi okunamadı.");if(manifest.Hashes.Count>1000||zip.Entries.Select(e=>e.FullName).Distinct().Count()!=zip.Entries.Count)throw new InvalidOperationException("Geçersiz yedek içeriği.");
            var restored=new Dictionary<string,byte[]>();
            long total=0;foreach(var entry in zip.Entries)
            {
                if(entry.FullName=="manifest.json")continue;
                if(!Allowed(entry.FullName)||!manifest.Hashes.TryGetValue(entry.FullName,out var expected))throw new InvalidOperationException("Beklenmeyen yedek dosyası.");
                total+=entry.Length;if(total>MaxBytes)throw new InvalidOperationException("Yedek boyut sınırı aşıldı.");using var input=entry.Open();using var output=new MemoryStream();input.CopyTo(output);var bytes=output.ToArray();if(Convert.ToHexString(SHA256.HashData(bytes))!=expected)throw new InvalidOperationException("Yedek bütünlüğü doğrulanamadı.");
                restored.Add(entry.FullName,bytes);if(entry.FullName.EndsWith(".json"))using(JsonDocument.Parse(bytes)){}else System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            }
            if(zip.Entries.Count!=manifest.Hashes.Count+1)throw new InvalidOperationException("Yedek dosyaları eksik.");return (manifest,restored);
    }
    public object RestoreCheck(string name)
    {
        lock(gate)
        {
            var archive=ReadArchive(name);
            var parent=Path.GetFullPath(Path.Combine(data,"RestoreChecks"));Directory.CreateDirectory(parent);
            var target=Path.GetFullPath(Path.Combine(parent,Guid.NewGuid().ToString("N")));
            if(!target.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Invalid restore directory.");
            var destination=Path.Combine(target,"Data");Directory.CreateDirectory(destination);
            try
            {
                foreach(var file in archive.Files)
                {
                    var output=Path.GetFullPath(Path.Combine(destination,file.Key.Replace('/',Path.DirectorySeparatorChar)));
                    if(!output.StartsWith(destination+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Invalid restore entry.");
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);File.WriteAllBytes(output,file.Value);
                }
                var env=new RestoreEnvironment {ContentRootPath=target};
                var database=new DemoStore(env).Read();var stores=new MarketPilot.Web.Integrations.IntegrationRegistry(env);var workspace=new DemoWorkspace(env);var inbox=new MarketPilot.Web.Webhooks.OrderWebhookInbox(env);
                var result=new {ok=true,files=archive.Files.Count,products=database.Products.Count,orders=database.Orders.Count,stores=stores.Connections.Count,queue=workspace.QueueStatus(),webhooks=inbox.Status(),checkedAt=DateTimeOffset.UtcNow,liveDataChanged=false};
                File.WriteAllText(Path.Combine(data,"restore-check.json"),JsonSerializer.Serialize(result));return result;
            }
            finally {if(Directory.Exists(target))Directory.Delete(target,true);}
        }
    }
    private sealed class RestoreEnvironment:IWebHostEnvironment
    {
        public string ApplicationName{get;set;}="MarketPilot.Web";public string EnvironmentName{get;set;}="Production";public string ContentRootPath{get;set;}="";public string WebRootPath{get;set;}="";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider{get;set;}=new Microsoft.Extensions.FileProviders.NullFileProvider();public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider{get;set;}=new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
    public byte[] Download(string name){lock(gate)return File.ReadAllBytes(Target(name));}
}
public sealed class DemoQueueWorker(DemoWorkspace demo, ReliabilityState state, LocalAudit audit, ILogger<DemoQueueWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var nextLog = DateTimeOffset.MinValue;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var completed = demo.ProcessPendingJobs();
                    state.LastQueueTick = DateTimeOffset.UtcNow.ToString("O");
                    state.LastQueueError = null;
                    if (completed > 0) audit.Add("info", "queue", $"{completed} demo işlemi tamamlandı.");
                }
                catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    state.LastQueueError = "Demo kuyruğu tamamlanamadı.";
                    if (DateTimeOffset.UtcNow < nextLog) continue;
                    nextLog = DateTimeOffset.UtcNow.AddMinutes(5);
                    logger.LogWarning("Local demo queue processing failed ({ErrorType}).", error.GetType().Name);
                    try
                    {
                        audit.Add("error", "queue", state.LastQueueError);
                    }
                    catch (Exception auditError) when (auditError is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        // A failure while recording the original failure must not escape the worker either.
                        logger.LogWarning("Local queue failure could not be audited ({ErrorType}).", auditError.GetType().Name);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
public sealed class LocalBackupWorker(LocalBackups backups, ReliabilityState state, LocalAudit audit, ILogger<LocalBackupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
            {
                try
                {
                    var latest = backups.List().FirstOrDefault();
                    if (latest is null || DateTimeOffset.UtcNow - latest.CreatedAt > TimeSpan.FromHours(24))
                    {
                        var backup = backups.Create();
                        state.LastBackupSuccess = backup.CreatedAt.ToString("O");
                        state.LastBackupError = null;
                        audit.Add("info", "backup", "Şifreli yerel yedek oluşturuldu ve doğrulandı.");
                    }
                    else state.LastBackupSuccess = latest.CreatedAt.ToString("O");
                }
                catch (Exception error) when (error is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    state.LastBackupError = "Otomatik yedek tamamlanamadı.";
                    logger.LogWarning("Local encrypted backup failed ({ErrorType}).", error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
public sealed record VerifyBackupRequest(string Name);
// The JSON stores assume a single process; a second one on the same Data folder would silently overwrite its writes.
public sealed class DataDirectoryLock(FileStream handle) : IDisposable
{
    public static DataDirectoryLock Acquire(string contentRoot)
    {
        var folder=Path.Combine(contentRoot,"Data");Directory.CreateDirectory(folder);
        try{return new(new FileStream(Path.Combine(folder,".instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None));}
        catch(IOException){throw new InvalidOperationException("Bu Data klasörünü başka bir MarketPilot süreci kullanıyor. Diğer süreci kapatıp yeniden başlatın.");}
    }
    public void Dispose()=>handle.Dispose();
}
public static class LocalReliability
{
    public static void AddLocalReliability(this IServiceCollection services)
    {
        services.AddSingleton<ReliabilityState>();services.AddSingleton<LocalAudit>();services.AddSingleton<LocalBackups>();services.AddHostedService<DemoQueueWorker>();services.AddHostedService<LocalBackupWorker>();
    }
    public static void MapLocalReliability(this WebApplication app)
    {
        app.MapGet("/api/system/status",(ReliabilityState s,LocalBackups backups)=>Results.Ok(new {s.LastQueueTick,s.LastQueueError,s.LastBackupSuccess,s.LastBackupError,backupCount=backups.List().Count,queueMode="local-demo"}));
        app.MapGet("/api/system/events",(HttpContext c,LocalAudit audit)=>c.User.IsInRole("admin")?Results.Ok(audit.Recent()):Results.StatusCode(403));
        app.MapPost("/api/system/backups/restore-check",(VerifyBackupRequest input,HttpContext c,LocalBackups backups)=>{
            if(!c.User.IsInRole("admin"))return Results.StatusCode(403);
            try{return Results.Ok(backups.RestoreCheck(input.Name));}catch(Exception e)when(e is IOException or ArgumentException or InvalidOperationException or CryptographicException or JsonException){return Results.BadRequest(new{error="Yedekten geri alma provası tamamlanamadı; mevcut kayıtlar korunuyor."});}
        });
        app.MapGet("/api/system/backups",(HttpContext c,LocalBackups b)=>c.User.IsInRole("admin")?Results.Ok(b.List()):Results.StatusCode(403));
        app.MapPost("/api/system/backups",(HttpContext c,LocalBackups b,LocalAudit audit)=>{if(!c.User.IsInRole("admin"))return Results.StatusCode(403);try{var result=b.Create();audit.Add("info","backup","Yönetici şifreli yedek oluşturdu ve doğruladı.");return Results.Ok(result);}catch(Exception e)when(e is IOException or InvalidOperationException or CryptographicException or JsonException){return Results.Json(new{error="Yedek oluşturulamadı."},statusCode:503);}});
        app.MapPost("/api/system/backups/verify",(VerifyBackupRequest input,HttpContext c,LocalBackups b)=>{if(!c.User.IsInRole("admin"))return Results.StatusCode(403);try{return Results.Ok(b.Verify(input.Name));}catch(Exception e)when(e is IOException or ArgumentException or InvalidOperationException or CryptographicException or JsonException){return Results.BadRequest(new{error="Yedek doğrulanamadı."});}});
        app.MapGet("/api/system/backups/{name}",(string name,HttpContext c,LocalBackups b)=>{if(!c.User.IsInRole("admin"))return Results.StatusCode(403);try{return Results.File(b.Download(name),"application/octet-stream",name);}catch(Exception e)when(e is IOException or ArgumentException){return Results.NotFound();}});
    }
}
