using System.Security.Claims;
using System.Text.Json;
using MarketPilot.Web.Integrations;

namespace MarketPilot.Web.Local;

public sealed record TenantSwitch(string TenantId);
public static class TenantBoundary
{
    private static readonly HashSet<string> TenantFields=new(["tenantId","businessId","sourceTenantId","targetTenantId","sourceBusinessId","targetBusinessId"],StringComparer.OrdinalIgnoreCase);
    public static bool Matches(JsonElement body,string tenant)
    {
        if(body.ValueKind==JsonValueKind.Object)foreach(var field in body.EnumerateObject())
        {
            if(TenantFields.Contains(field.Name)&&field.Value.ValueKind!=JsonValueKind.Null &&
                (field.Value.ValueKind!=JsonValueKind.String||field.Value.GetString()!=tenant))return false;
            if(!Matches(field.Value,tenant))return false;
        }
        else if(body.ValueKind==JsonValueKind.Array)foreach(var item in body.EnumerateArray())if(!Matches(item,tenant))return false;
        return true;
    }
    public static void UseTenantBoundary(this WebApplication app)
    {
        app.Use(async(c,next)=>
        {
            var path=c.Request.Path.Value??"";
            if(!path.StartsWith("/api/",StringComparison.Ordinal)){await next();return;}
            var tenant=BusinessWorkspaces.BusinessId(c);
            foreach(var name in new[]{"X-Tenant-Id","X-Business-Id","X-Source-Tenant-Id","X-Target-Tenant-Id"})
                if(c.Request.Headers.TryGetValue(name,out var header)&&(header.Count!=1||header.ToString()!=tenant)){c.Response.StatusCode=403;return;}
            var control=path is "/api/access/users" or "/api/access/switch-tenant" or "/api/tenancy/tenants" or "/api/tenancy/memberships";
            if(!control)foreach(var query in c.Request.Query)if(TenantFields.Contains(query.Key)&&(query.Value.Count!=1||query.Value.ToString()!=tenant)){c.Response.StatusCode=403;return;}
            if(c.User.Identity?.IsAuthenticated==true && !TenantRoles.Allowed(c)){c.Response.StatusCode=403;return;}
            if(!control && c.Request.HasJsonContentType()&&c.Request.Method is not ("GET" or "HEAD"))
            {
                c.Request.EnableBuffering(64*1024,8*1024*1024);
                try
                {
                    using var body=await JsonDocument.ParseAsync(c.Request.Body,new JsonDocumentOptions{MaxDepth=32},c.RequestAborted);
                    if(!Matches(body.RootElement,tenant)){c.Response.StatusCode=403;return;}
                }
                catch(JsonException){c.Response.StatusCode=400;return;}
                catch(IOException){c.Response.StatusCode=413;return;}
                finally{c.Request.Body.Position=0;}
            }
            await next();
        });
    }
    public static void MapTenantSecurity(this WebApplication app)
    {
        app.MapGet("/api/tenancy",(HttpContext c,TenantAccessDirectory directory)=>Results.Ok(directory.Snapshot(c)));
        app.MapGet("/api/access/memberships",(HttpContext c,TenantAccessDirectory directory)=>Results.Ok(directory.ForUser(c.User.FindFirstValue(ClaimTypes.NameIdentifier)??"")));
        app.MapPost("/api/tenancy/tenants",(TenantRegistration input,HttpContext c,TenantAccessDirectory directory)=>
        {
            if(!TenantRoles.IsPlatform(c))return Results.StatusCode(403);
            try{return Results.Ok(directory.Register(input));}catch(ArgumentException){return Results.BadRequest(new{error="İşletme kaydı geçersiz."});}
        });
        app.MapPost("/api/tenancy/memberships",(Membership input,HttpContext c,TenantAccessDirectory directory,LocalAccountStore accounts)=>
        {
            if(!TenantRoles.CanGrant(c,input.RoleId,input.TenantId))return Results.StatusCode(403);
            directory.Synchronize(accounts.Users());
            try{return Results.Ok(directory.SetMembership(input,accounts));}catch(ArgumentException){return Results.BadRequest(new{error="Üyelik geçersiz veya yetki sınırı dışında."});}
        });
        app.MapPost("/api/access/switch-tenant",async(TenantSwitch input,HttpContext c,TenantAccessDirectory directory,LocalAccountStore accounts)=>
        {
            directory.Synchronize(accounts.Users());var id=c.User.FindFirstValue(ClaimTypes.NameIdentifier)??"";
            var membership=directory.Find(id,input.TenantId);var user=accounts.Users().SingleOrDefault(u=>u.Id==id&&u.Enabled);
            if(membership==null||user==null)return Results.StatusCode(403);
            await LocalAccess.SignIn(c,user with{BusinessId=membership.TenantId,Role=membership.RoleId},int.Parse(c.User.FindFirstValue("version")!));
            return Results.Ok(new{tenantId=membership.TenantId,role=membership.RoleId});
        });
        var connections=app.MapGroup("/api/security/connections");
        connections.AddEndpointFilter(async(ctx,next)=>
        {
            if(!ctx.HttpContext.User.IsInRole("admin"))return Results.StatusCode(403);
            try{return await next(ctx);}catch(ArgumentException){return Results.BadRequest(new{error="Bağlantı/kimlik referansı geçersiz."});}
        });
        connections.MapGet("",(CredentialReferenceStore store)=>Results.Ok(store.Snapshot()));
        connections.MapPut("/credentials",(CredentialReference input,CredentialReferenceStore store)=>Results.Ok(store.Put(input)));
        connections.MapPut("/erp",(ErpConnection input,CredentialReferenceStore store)=>Results.Ok(store.PutErp(input)));
        connections.MapPut("/wms",(WmsConnection input,CredentialReferenceStore store)=>Results.Ok(store.PutWms(input)));
    }
}
public sealed class CredentialReferenceData
{
    public List<CredentialReference> References{get;set;}=[];
    public List<ErpConnection> Erp{get;set;}=[];
    public List<WmsConnection> Wms{get;set;}=[];
}
public sealed class CredentialReferenceStore
{
    private readonly object gate=new();private readonly string file,tenant;private readonly IConfiguration config;
    private CredentialReferenceData data;private readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    public CredentialReferenceStore(IWebHostEnvironment env,IConfiguration config)
    {
        this.config=config;tenant=config["Workspace:BusinessId"]??"default";file=Path.Combine(env.ContentRootPath,"Data","credential-references.json");
        data=File.Exists(file)?JsonSerializer.Deserialize<CredentialReferenceData>(File.ReadAllText(file),json)??throw new InvalidOperationException("Credential references unreadable"):new();
    }
    private T Save<T>(Func<T> action){lock(gate){var before=JsonSerializer.Serialize(data,json);try{var result=action();Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllText(file+".tmp",JsonSerializer.Serialize(data,json));File.Move(file+".tmp",file,true);return result;}catch{data=JsonSerializer.Deserialize<CredentialReferenceData>(before,json)!;throw;}}}
    public object Snapshot(){lock(gate)return new{references=data.References.Where(r=>r.TenantId==tenant).Select(r=>new{r.Id,r.TenantId,r.System,r.Provider,configured=r.Provider=="mock"||!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(r.KeyName))}).ToArray(),erp=data.Erp.Where(r=>r.TenantId==tenant).ToArray(),wms=data.Wms.Where(r=>r.TenantId==tenant).ToArray()};}
    public CredentialReference Put(CredentialReference input)=>Save(()=>
    {
        var prefix="MARKETPILOT__"+tenant.Replace('-','_').ToUpperInvariant()+"__"+input.System.ToUpperInvariant()+"__";
        if(input.TenantId!=tenant||!BusinessWorkspaces.ValidId(input.Id)||input.System is not ("erp" or "wms")||input.Provider is not ("mock" or "environment")||
            input.Provider=="environment"&&(!input.KeyName.StartsWith(prefix,StringComparison.Ordinal)||!System.Text.RegularExpressions.Regex.IsMatch(input.KeyName,"^[A-Z0-9_]{1,160}$"))||
            input.Provider=="mock"&&!System.Text.RegularExpressions.Regex.IsMatch(input.KeyName,"^MOCK_[A-Z0-9_]{1,80}$"))throw new ArgumentException("Invalid reference");
        data.References.RemoveAll(r=>r.Id==input.Id);data.References.Add(input);return input;
    });
    private void Validate(string id,string reference,string inputTenant,string system,bool enabled)
    {
        if(inputTenant!=tenant||!BusinessWorkspaces.ValidId(id)||!data.References.Any(r=>r.Id==reference&&r.TenantId==tenant&&r.System==system)||enabled&&!config.GetValue<bool>("IntegrationSecurity:AllowRealConnections")&&data.References.Any(r=>r.Id==reference&&r.Provider!="mock"))throw new ArgumentException("Connection denied");
    }
    public ErpConnection PutErp(ErpConnection input)=>Save(()=>{Validate(input.Id,input.CredentialReferenceId,input.TenantId,"erp",input.Enabled);data.Erp.RemoveAll(x=>x.Id==input.Id);data.Erp.Add(input);return input;});
    public WmsConnection PutWms(WmsConnection input)=>Save(()=>{Validate(input.Id,input.CredentialReferenceId,input.TenantId,"wms",input.Enabled);data.Wms.RemoveAll(x=>x.Id==input.Id);data.Wms.Add(input);return input;});
    internal string? Resolve(string id,string system)
    {lock(gate){var r=data.References.SingleOrDefault(r=>r.Id==id&&r.TenantId==tenant&&r.System==system);return r==null?null:r.Provider=="mock"?"mock-reference:"+r.Id:Environment.GetEnvironmentVariable(r.KeyName);}}
}
