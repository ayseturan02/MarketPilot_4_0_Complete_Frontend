using MarketPilot.Web.Contracts;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.RegularExpressions;
using MarketPilot.Web.Catalog;
using MarketPilot.Web.Enterprise;
using MarketPilot.Web.Integrations;
using MarketPilot.Web.Synchronization;
using MarketPilot.Web.Webhooks;
using MarketPilot.Web.Workspace;
using MarketPilot.Web.Wms;
using Microsoft.Extensions.FileProviders;

namespace MarketPilot.Web.Local;

// Each business has its own stores, catalogue, orders, queues, settings, clients and backups.
// Authentication stays global; the signed cookie determines the request workspace.
public sealed class BusinessWorkspaces(IServiceProvider root,IWebHostEnvironment environment,IConfiguration configuration,LocalAccountStore accounts,TenantAccessDirectory? tenants=null):IAsyncDisposable
{
    private readonly ConcurrentDictionary<string,Lazy<BusinessWorkspace>> workspaces=new();
    public static bool ValidId(string? id)=>id!=null&&Regex.IsMatch(id,"^[a-z0-9][a-z0-9-]{0,63}$");
    public static string BusinessId(HttpContext context)=>context.User.Identity?.IsAuthenticated==true?context.User.FindFirstValue("business_id")??"default":"default";
    public bool Exists(string id)=>ValidId(id)&&(tenants?.Exists(id)??accounts.Users().Any(u=>u.BusinessId==id));
    public BusinessWorkspace Get(string id)
    {
        if(id=="default"||!Exists(id))throw new ArgumentException("İşletme bulunamadı.");
        return workspaces.GetOrAdd(id,key=>new(()=>Create(key),LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
    private BusinessWorkspace Create(string id)
    {
        var parent=Path.GetFullPath(Path.Combine(environment.ContentRootPath,"Data","businesses"));
        var folder=Path.GetFullPath(Path.Combine(parent,id));
        if(!folder.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Geçersiz işletme dizini.");
        Directory.CreateDirectory(folder);
        var env=new BusinessEnvironment(environment,folder);
        // Credentials and connector settings are explicitly per business; never inherit another seller's keys.
        string[] privateRoots=["Marketplaces:","MarketplaceAccounts:","Enterprise:","Wms:","Webhooks:","Businesses:"];
        var values=configuration.AsEnumerable().Where(p=>p.Value!=null&&!privateRoots.Any(prefix=>p.Key.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
        foreach(var entry in configuration.GetSection("Businesses:"+id).AsEnumerable(true).Where(p=>p.Value!=null))values[entry.Key]=entry.Value;
        values["Workspace:BusinessId"]=id;
        var scopedConfig=new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services=new ServiceCollection();
        services.AddSingleton<IConfiguration>(scopedConfig);services.AddSingleton<IWebHostEnvironment>(env);
        services.AddSingleton(accounts);services.AddSingleton(root.GetRequiredService<ILoggerFactory>());services.AddLogging();
        if(tenants!=null)services.AddSingleton(tenants);
        services.AddSingleton<CredentialReferenceStore>();
        services.AddSingleton(root.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>());
        services.AddSingleton<DemoStore>();services.AddCatalog();services.AddOrderWebhooks();services.AddMarketplaceIntegrations(scopedConfig);
        services.AddLocalReliability();services.AddBackgroundSynchronization();services.AddEnterprise();services.AddWmsProviders(scopedConfig);services.AddSingleton<WorkspaceHub>();services.AddOperationalPreparation();services.AddCanonicalContracts();
        var dataLock=DataDirectoryLock.Acquire(folder);
        ServiceProvider? provider=null;
        try
        {
            provider=services.BuildServiceProvider(new ServiceProviderOptions{ValidateScopes=true});
            provider.GetRequiredService<CatalogHub>().Ensure();
            return new(provider,dataLock);
        }
        catch {provider?.Dispose();dataLock.Dispose();throw;}
    }
    public async Task StartKnown(CancellationToken ct)
    {
        foreach(var id in accounts.Users().Select(u=>u.BusinessId).Distinct().Where(id=>id!="default"))
        {
            try {await Get(id).Start(ct);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception error){root.GetRequiredService<ILogger<BusinessWorkspaces>>().LogWarning("İşletme arka plan servisleri başlatılamadı ({Business}, {ErrorType}).",id,error.GetType().Name);}
        }
    }
    public async ValueTask DisposeAsync()
    {
        foreach(var entry in workspaces.Values.Where(l=>l.IsValueCreated))await entry.Value.DisposeAsync();
    }
    private sealed class BusinessEnvironment(IWebHostEnvironment source,string folder):IWebHostEnvironment
    {
        public string ApplicationName{get;set;}=source.ApplicationName;public string EnvironmentName{get;set;}=source.EnvironmentName;
        public string ContentRootPath{get;set;}=folder;public string WebRootPath{get;set;}=source.WebRootPath;
        public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();public IFileProvider WebRootFileProvider{get;set;}=source.WebRootFileProvider;
    }
}
public sealed class BusinessWorkspace(ServiceProvider provider,DataDirectoryLock dataLock):IAsyncDisposable
{
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly List<IHostedService> started=[];
    private bool running;
    public IServiceScope CreateScope()=>provider.CreateScope();
    public async Task Start(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if(running)return;
            foreach(var worker in provider.GetServices<IHostedService>())
            {
                if(started.Contains(worker))continue;
                await worker.StartAsync(ct);started.Add(worker);
            }
            running=true;
        }
        finally {gate.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach(var worker in started.AsEnumerable().Reverse())try{await worker.StopAsync(stop.Token);}catch(OperationCanceledException){}
        await provider.DisposeAsync();dataLock.Dispose();gate.Dispose();
    }
}
public sealed class BusinessWorkspaceWorker(BusinessWorkspaces workspaces):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await workspaces.StartKnown(ct);
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
            while(await timer.WaitForNextTickAsync(ct))await workspaces.StartKnown(ct);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
    }
}
public static class BusinessWorkspaceMiddleware
{
    public static void UseBusinessWorkspaces(this WebApplication app)
    {
        app.Use(async(context,next)=>
        {
            if(!context.Request.Path.StartsWithSegments("/api")&&!context.Request.Path.StartsWithSegments("/hooks")){await next();return;}
            var id=BusinessWorkspaces.BusinessId(context);
            if(context.Request.Path.StartsWithSegments("/hooks/businesses"))id=context.Request.RouteValues["businessId"]?.ToString()??"";
            if(id=="default"){await next();return;}
            var rootServices=context.RequestServices;var manager=rootServices.GetRequiredService<BusinessWorkspaces>();
            if(!manager.Exists(id)){context.Response.StatusCode=404;return;}
            var workspace=manager.Get(id);await workspace.Start(rootServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
            using var scope=workspace.CreateScope();
            context.RequestServices=new WorkspaceServices(scope.ServiceProvider,rootServices);
            try {await next();}
            finally {context.RequestServices=rootServices;}
        });
    }
    private sealed class WorkspaceServices(IServiceProvider business,IServiceProvider infrastructure):IServiceProvider
    {
        public object? GetService(Type type)
        {
            var value=business.GetService(type);
            if(value!=null)return value;
            if(type.Assembly==typeof(DemoStore).Assembly)throw new InvalidOperationException("İşletme servisi kayıtlı değil; ortak veriye erişim engellendi.");
            return infrastructure.GetService(type);
        }
    }
}
