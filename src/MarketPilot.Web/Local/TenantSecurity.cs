using System.Security.Claims;
using System.Text.Json;
using MarketPilot.Web.Integrations;

namespace MarketPilot.Web.Local;

public sealed record Tenant(string Id,string Name,bool Enabled=true);
public sealed record Organization(string Id,string TenantId,string Name);
public sealed record User(string Id,string Name,bool Enabled);
public sealed record Membership(string UserId,string TenantId,string OrganizationId,string RoleId,bool Enabled=true);
public sealed record Role(string Id,string Name);
public sealed record MarketplaceAccount(string Id,string TenantId,string Channel,string? CredentialReferenceId);
public sealed record ErpConnection(string Id,string TenantId,string Name,string CredentialReferenceId,bool Enabled=false);
public sealed record WmsConnection(string Id,string TenantId,string Name,string CredentialReferenceId,bool Enabled=false);
public sealed record CredentialReference(string Id,string TenantId,string System,string Provider,string KeyName);
public sealed record TenantRegistration(string Id,string Name);
public sealed class TenantDirectoryData
{
    public List<Tenant> Tenants{get;set;}=[new("default","Varsayılan işletme"),new("demo-a","Demo işletme A"),new("demo-b","Demo işletme B")];
    public List<Organization> Organizations{get;set;}=[new("default-org","default","Varsayılan organizasyon"),new("demo-a-org","demo-a","Demo A"),new("demo-b-org","demo-b","Demo B")];
    public List<Membership> Memberships{get;set;}=[];
}
public static class TenantRoles
{
    public static readonly Role[] All=[new("platform-admin","Platform yöneticisi"),new("business-owner","İşletme sahibi"),
        new("business-admin","İşletme yöneticisi"),new("operations","Operasyon personeli"),new("warehouse","Depo operasyon kullanıcısı"),new("read-only","Salt okunur kullanıcı")];
    public static bool Valid(string role)=>All.Any(r=>r.Id==role)||role is "admin" or "operator" or "viewer";
    public static string Normalize(string role,string tenant)=>role switch{"admin"=>tenant=="default"?"platform-admin":"business-admin","operator"=>"operations","viewer"=>"read-only",_=>role};
    public static string Alias(string role)=>role switch{"platform-admin" or "business-owner" or "business-admin"=>"admin","operations" or "warehouse"=>"operator",_=>"viewer"};
    public static string Current(HttpContext c)=>c.User.FindFirstValue("tenant_role")??Normalize(c.User.FindFirstValue(ClaimTypes.Role)??"viewer",BusinessWorkspaces.BusinessId(c));
    public static bool IsPlatform(HttpContext c)=>Current(c)=="platform-admin";
    public static bool CanGrant(HttpContext c,string role,string tenant)
    {
        var normalized=Normalize(role,tenant);var actor=Current(c);
        if(normalized=="platform-admin")return actor=="platform-admin"&&tenant=="default";
        if(tenant!=BusinessWorkspaces.BusinessId(c))return actor=="platform-admin";
        return actor switch{"platform-admin" or "business-owner"=>true,"business-admin"=>normalized is "operations" or "warehouse" or "read-only",_=>false};
    }
    public static bool WarehouseAction(HttpContext c)=>Current(c) is "warehouse" or "operations" &&
        (c.Request.Path=="/api/wms/logistics/mock/events" || System.Text.RegularExpressions.Regex.IsMatch(c.Request.Path.Value??"",@"^/api/wms/fulfillment/workflows/[^/]+/advance$"));
    public static bool Allowed(HttpContext c)
    {
        var role=Current(c);var path=(c.Request.Path.Value??"").ToLowerInvariant();
        if(role is "platform-admin" or "business-owner" or "business-admin")return true;
        if(path.StartsWith("/api/access/",StringComparison.Ordinal))return path is "/api/access/status" or "/api/access/logout" or "/api/access/password" or "/api/access/switch-tenant" or "/api/access/memberships";
        var read=c.Request.Method is "GET" or "HEAD";
        if(role=="warehouse")return read && (path.StartsWith("/api/wms",StringComparison.Ordinal)||path.StartsWith("/api/orders",StringComparison.Ordinal)||path.StartsWith("/api/shipments",StringComparison.Ordinal)||path=="/api/health"||path.StartsWith("/api/observability",StringComparison.Ordinal)) || WarehouseAction(c);
        if(read)return !path.StartsWith("/api/tenancy",StringComparison.Ordinal)&&!path.Contains("credential",StringComparison.OrdinalIgnoreCase);
        if(role=="read-only")return LocalAccess.ReadOnlyPost(c);
        if(role=="operations")return WarehouseAction(c) || path.StartsWith("/api/orders/",StringComparison.Ordinal) ||
            path.StartsWith("/api/operations/preparation/orders/",StringComparison.Ordinal) || LocalAccess.ReadOnlyPost(c);
        return false;
    }
}
public sealed class TenantAccessDirectory
{
    private readonly object gate=new();private readonly string file;private TenantDirectoryData data;private readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    public TenantAccessDirectory(IWebHostEnvironment env,LocalAccountStore accounts)
    {
        file=Path.Combine(env.ContentRootPath,"Data","tenant-directory.json");
        data=File.Exists(file)?JsonSerializer.Deserialize<TenantDirectoryData>(File.ReadAllText(file),json)??throw new InvalidOperationException("Tenant directory unreadable"):new();
        Synchronize(accounts.Users());
        if(!File.Exists(file))Change(()=>true);
    }
    private T Change<T>(Func<T> action)
    {
        lock(gate){var before=JsonSerializer.Serialize(data,json);try{var result=action();Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllText(file+".tmp",JsonSerializer.Serialize(data,json));File.Move(file+".tmp",file,true);return result;}catch{data=JsonSerializer.Deserialize<TenantDirectoryData>(before,json)!;throw;}}
    }
    public void Synchronize(IReadOnlyList<UserSummary> users)
    {
        lock(gate)
        {
            if(users.All(u=>data.Tenants.Any(t=>t.Id==u.BusinessId)&&data.Memberships.Any(m=>m.UserId==u.Id&&m.TenantId==u.BusinessId&&m.RoleId==TenantRoles.Normalize(u.Role,u.BusinessId)&&m.Enabled==u.Enabled)))return;
            Change(()=>{foreach(var u in users){if(!data.Tenants.Any(t=>t.Id==u.BusinessId)){data.Tenants.Add(new(u.BusinessId,u.BusinessId));data.Organizations.Add(new(u.BusinessId+"-org",u.BusinessId,u.BusinessId));}
                data.Memberships.RemoveAll(m=>m.UserId==u.Id&&m.TenantId==u.BusinessId);data.Memberships.Add(new(u.Id,u.BusinessId,u.BusinessId+"-org",TenantRoles.Normalize(u.Role,u.BusinessId),u.Enabled));}return true;});
        }
    }
    public bool Exists(string tenant){lock(gate)return data.Tenants.Any(t=>t.Id==tenant&&t.Enabled);}
    public Membership? Find(string user,string tenant){lock(gate)return Exists(tenant)?data.Memberships.SingleOrDefault(m=>m.UserId==user&&m.TenantId==tenant&&m.Enabled):null;}
    public Membership[] ForUser(string user){lock(gate)return data.Memberships.Where(m=>m.UserId==user&&m.Enabled&&Exists(m.TenantId)).ToArray();}
    public object Snapshot(HttpContext c)
    {lock(gate){var tenant=BusinessWorkspaces.BusinessId(c);return new{tenants=data.Tenants.Where(t=>TenantRoles.IsPlatform(c)||t.Id==tenant).ToArray(),organizations=data.Organizations.Where(o=>TenantRoles.IsPlatform(c)||o.TenantId==tenant).ToArray(),memberships=data.Memberships.Where(m=>TenantRoles.IsPlatform(c)||m.TenantId==tenant).ToArray(),roles=TenantRoles.All};}}
    public Tenant Register(TenantRegistration input)=>Change(()=>
    {
        if(!BusinessWorkspaces.ValidId(input.Id)||string.IsNullOrWhiteSpace(input.Name)||input.Name.Length>100||data.Tenants.Count>=100||Exists(input.Id))throw new ArgumentException("İşletme kaydı geçersiz veya zaten mevcut.");
        var tenant=new Tenant(input.Id,input.Name);data.Tenants.Add(tenant);data.Organizations.Add(new(input.Id+"-org",input.Id,input.Name));return tenant;
    });
    public Membership SetMembership(Membership input,LocalAccountStore accounts)=>Change(()=>
    {
        if(!Exists(input.TenantId)||!accounts.Users().Any(u=>u.Id==input.UserId&&u.Enabled)||!TenantRoles.All.Any(r=>r.Id==input.RoleId)||
            input.RoleId=="platform-admin"&&input.TenantId!="default"||!data.Organizations.Any(o=>o.Id==input.OrganizationId&&o.TenantId==input.TenantId))throw new ArgumentException("Üyelik/organizasyon/işletme geçersiz.");
        // Primary membership is managed by the existing account record so role changes revoke its cookie version.
        if(accounts.Users().Any(u=>u.Id==input.UserId&&u.BusinessId==input.TenantId))throw new ArgumentException("Birincil üyelik mevcut kullanıcı rolü ekranından yönetilir.");
        data.Memberships.RemoveAll(m=>m.UserId==input.UserId&&m.TenantId==input.TenantId);data.Memberships.Add(input);return input;
    });
}
