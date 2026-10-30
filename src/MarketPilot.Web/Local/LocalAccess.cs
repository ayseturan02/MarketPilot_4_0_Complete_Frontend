using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;

namespace MarketPilot.Web.Local;

public sealed class LocalUser
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Salt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "viewer";
    public bool Enabled { get; set; } = true;
    public int Version { get; set; }
    public string BusinessId { get; set; } = "default";
}
public sealed class LoginThrottledException() : Exception("Çok fazla giriş denemesi. Bir süre sonra tekrar dene.");
public sealed record UserSummary(string Id, string Name, string Role, bool Enabled, string BusinessId = "default");
public sealed record LocalCredentials(string Name, string Password, string? SetupToken = null);

// In public mode the first administrator needs a one-time code from Data/setup-token.txt. Reaching the bootstrap path
// (another user on the host, or a host proxy that forwards as localhost) is then not enough to claim the server.
public sealed class SetupToken(IWebHostEnvironment env)
{
    private readonly object gate = new();
    private readonly string file = Path.Combine(env.ContentRootPath, "Data", "setup-token.txt");
    public void Ensure() { lock (gate) if (!File.Exists(file)) File.WriteAllText(file, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()); }
    public bool Matches(string? value)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(value) || !File.Exists(file)) return false;
            return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(file).Trim()), System.Text.Encoding.UTF8.GetBytes(value.Trim()));
        }
    }
    public void Remove() { lock (gate) File.Delete(file); }
}
public sealed record NewLocalUser(string Name, string Password, string Role, string? BusinessId = null);
public sealed record ChangeLocalUser(string Role, bool Enabled);
public sealed record ChangePasswordInput(string CurrentPassword,string NewPassword);

public sealed class LocalAccountStore
{
    private long revision;
    public long Revision => Interlocked.Read(ref revision);
    private readonly object gate = new();
    private readonly string file;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private List<LocalUser> users;
    public LocalAccountStore(IWebHostEnvironment env)
    {
        var folder = Path.Combine(env.ContentRootPath, "Data"); Directory.CreateDirectory(folder); file = Path.Combine(folder, "local-accounts.json");
        users = File.Exists(file) ? JsonSerializer.Deserialize<List<LocalUser>>(File.ReadAllText(file), json) ?? throw new InvalidOperationException("Account file cannot be read.") : new();
    }
    public bool Configured { get { lock (gate) return users.Count > 0; } }
    private void Save(string previous)
    {
        try { var tmp = file + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(users, json)); File.Move(tmp, file, true); Interlocked.Increment(ref revision); }
        catch { users = JsonSerializer.Deserialize<List<LocalUser>>(previous, json)!; throw; }
    }
    private static byte[] Hash(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA256, 32);
    private static UserSummary Summary(LocalUser user) => new(user.Id, user.Name, user.Role, user.Enabled,user.BusinessId);
    public UserSummary Create(string name, string password, string role, bool initial = false,string businessId="default")
    {
        lock (gate)
        {
            if (initial && users.Count > 0) throw new ArgumentException("Yönetici hesabı zaten oluşturulmuş.");
            name = (name ?? "").Trim().ToLowerInvariant();
            if (!Regex.IsMatch(name, @"^[a-z0-9][a-z0-9._@-]{2,79}$")) throw new ArgumentException("Hesap adı 3–80 karakter olmalı; harf, rakam, nokta, tire veya @ kullanılabilir.");
            if (password is null || password.Length is < 12 or > 128) throw new ArgumentException("Parola 12–128 karakter olmalı.");
            if (!TenantRoles.Valid(role)||TenantRoles.Normalize(role,businessId)=="platform-admin"&&businessId!="default") throw new ArgumentException("Geçersiz yetki.");
            if(!BusinessWorkspaces.ValidId(businessId))throw new ArgumentException("İşletme kodu 1–64 küçük harf, rakam veya tire olmalı.");
            if(!users.Any(u=>u.BusinessId==businessId)&&users.Select(u=>u.BusinessId).Distinct().Count()>=100)throw new ArgumentException("En fazla 100 işletme tanımlanabilir.");
            if (users.Any(u => u.Name == name)) throw new ArgumentException("Bu hesap adı zaten kullanılıyor.");
            var before = JsonSerializer.Serialize(users, json); var salt = RandomNumberGenerator.GetBytes(16);
            var user = new LocalUser { Name = name, Salt = Convert.ToBase64String(salt), PasswordHash = Convert.ToBase64String(Hash(password, salt)), Role = initial ? "admin" : role,BusinessId=initial?"default":businessId };
            users.Add(user); Save(before); return Summary(user);
        }
    }
    // Every authenticated request takes the account lock (session validation), so the slow PBKDF2 runs outside it,
    // with bounded concurrency, and each account allows a limited number of failures per window from any IP.
    private static readonly SemaphoreSlim hashing = new(4, 4);
    private const int MaxFailures = 10;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    private readonly Dictionary<string, (int Count, DateTimeOffset Since)> failures = new();
    public (UserSummary User, int Version)? Authenticate(string name, string password)
    {
        if (password is null || password.Length > 128) return null;
        var key = (name ?? "").Trim().ToLowerInvariant();
        string? id; byte[] salt, expected;
        lock (gate)
        {
            if (failures.TryGetValue(key, out var seen) && seen.Count >= MaxFailures && DateTimeOffset.UtcNow - seen.Since < FailureWindow) throw new LoginThrottledException();
            var user = users.FirstOrDefault(u => u.Name == key);
            id = user?.Id; salt = user is null ? new byte[16] : Convert.FromBase64String(user.Salt); expected = user is null ? new byte[32] : Convert.FromBase64String(user.PasswordHash);
        }
        if (!hashing.Wait(TimeSpan.FromSeconds(5))) throw new LoginThrottledException();
        byte[] actual;
        try { actual = Hash(password, salt); } finally { hashing.Release(); }
        lock (gate)
        {
            // The account may have changed while hashing; only the verified password hash may sign in.
            var current = users.FirstOrDefault(u => u.Id == id);
            if (current is null || !current.Enabled || current.PasswordHash != Convert.ToBase64String(expected) || !CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                var now = DateTimeOffset.UtcNow;
                if (failures.Count > 10000) foreach (var old in failures.Where(f => now - f.Value.Since >= FailureWindow).Select(f => f.Key).ToArray()) failures.Remove(old);
                failures[key] = failures.TryGetValue(key, out var previous) && now - previous.Since < FailureWindow ? (previous.Count + 1, previous.Since) : (1, now);
                return null;
            }
            failures.Remove(key);
            return (Summary(current), current.Version);
        }
    }
    public bool ValidateSession(string? id, string? version)
    {
        lock (gate) return users.Any(u => u.Id == id && u.Enabled && u.Version.ToString() == version);
    }
    public (UserSummary User,int Version) ChangePassword(string id,string current,string password)
    {
        lock(gate)
        {
            var user=users.FirstOrDefault(u=>u.Id==id&&u.Enabled)??throw new ArgumentException("Hesap bulunamadı.");
            if(current is null||current.Length>128||!CryptographicOperations.FixedTimeEquals(Hash(current,Convert.FromBase64String(user.Salt)),Convert.FromBase64String(user.PasswordHash)))throw new ArgumentException("Mevcut parola hatalı.");
            if(password is null||password.Length is <12 or >128)throw new ArgumentException("Parola 12–128 karakter olmalı.");
            var before=JsonSerializer.Serialize(users,json);var salt=RandomNumberGenerator.GetBytes(16);user.Salt=Convert.ToBase64String(salt);user.PasswordHash=Convert.ToBase64String(Hash(password,salt));user.Version++;Save(before);return(Summary(user),user.Version);
        }
    }
    public IReadOnlyList<UserSummary> Users() { lock (gate) return users.Select(Summary).ToArray(); }
    public void Change(string id, string role, bool enabled, string actor)
    {
        lock (gate)
        {
            if (!TenantRoles.Valid(role)) throw new ArgumentException("Geçersiz yetki.");
            var user = users.FirstOrDefault(u => u.Id == id) ?? throw new ArgumentException("Kullanıcı bulunamadı.");
            var acting=users.FirstOrDefault(u=>u.Id==actor);
            if(acting==null||acting.BusinessId!="default"&&acting.BusinessId!=user.BusinessId)throw new ArgumentException("Bu işletmenin kullanıcısına erişim yok.");
            if (id == actor && (!enabled || TenantRoles.Alias(TenantRoles.Normalize(role,user.BusinessId)) != "admin")) throw new ArgumentException("Kendi yönetici erişimini bu ekrandan kapatamazsın.");
            if(TenantRoles.Normalize(role,user.BusinessId)=="platform-admin"&&user.BusinessId!="default")throw new ArgumentException("Platform rolü bu işletmeye atanamaz.");
            if (TenantRoles.Alias(TenantRoles.Normalize(user.Role,user.BusinessId))=="admin" && user.Enabled && (!enabled || TenantRoles.Alias(TenantRoles.Normalize(role,user.BusinessId))!="admin") && users.Count(u => u.BusinessId==user.BusinessId&&TenantRoles.Alias(TenantRoles.Normalize(u.Role,u.BusinessId))=="admin"&&u.Enabled)<=1) throw new ArgumentException("Son yönetici hesabı kapatılamaz.");
            var before = JsonSerializer.Serialize(users, json); user.Role = role; user.Enabled = enabled; user.Version++; Save(before);
        }
    }
}

public static class LocalAccess
{
    public static bool IsPublicDeployment(IConfiguration configuration) =>
        string.Equals(configuration["Deployment:Mode"], "public", StringComparison.OrdinalIgnoreCase);

    public static bool IsLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip);

    private static bool IsTrustedBootstrapGatewayRequest(HttpContext context, IConfiguration configuration)
    {
        if (!System.Net.IPAddress.TryParse(configuration["Deployment:BootstrapGatewayIp"], out var gateway) ||
            context.Connection.RemoteIpAddress is not { } remote ||
            !remote.MapToIPv6().Equals(gateway.MapToIPv6()))
            return false;

        var host = context.Request.Host.Host;
        var localHost = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
        return localHost && !context.Request.Headers.ContainsKey("X-Forwarded-For") &&
            !context.Request.Headers.ContainsKey("X-Forwarded-Host") &&
            !context.Request.Headers.ContainsKey("X-Forwarded-Proto") &&
            !context.Request.Headers.ContainsKey("X-Original-For") &&
            !context.Request.Headers.ContainsKey("X-Original-Host") &&
            !context.Request.Headers.ContainsKey("X-Original-Proto");
    }

    public static void AddLocalAccess(this WebApplicationBuilder builder)
    {
        var keys = Path.Combine(builder.Environment.ContentRootPath, "Data", "keys"); Directory.CreateDirectory(keys);
        builder.Services.AddDataProtection().SetApplicationName("InternetTekstil.Local").PersistKeysToFileSystem(new DirectoryInfo(keys));
        builder.Services.AddSingleton<LocalAccountStore>();
        builder.Services.AddSingleton<TenantAccessDirectory>();
        builder.Services.AddSingleton<SetupToken>();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = "it_session"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = IsPublicDeployment(builder.Configuration) ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = false;
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
            o.Events.OnValidatePrincipal = async c =>
            {
                var store = c.HttpContext.RequestServices.GetRequiredService<LocalAccountStore>();
                var directory=c.HttpContext.RequestServices.GetRequiredService<TenantAccessDirectory>();directory.Synchronize(store.Users());
                var tenant=c.Principal?.FindFirstValue("business_id")??"default";
                var membership=directory.Find(c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier)??"",tenant);
                var role=c.Principal?.FindFirstValue("tenant_role")??TenantRoles.Normalize(c.Principal?.FindFirstValue(ClaimTypes.Role)??"viewer",tenant);
                if (!store.ValidateSession(c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), c.Principal?.FindFirstValue("version"))||membership?.RoleId!=role) { c.RejectPrincipal(); await c.HttpContext.SignOutAsync(); }
            };
        });
        builder.Services.AddRateLimiter(o =>
        {
            o.AddPolicy("local-login", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.OnRejected = async (c, ct) => { c.HttpContext.Response.StatusCode = 429; await c.HttpContext.Response.WriteAsJsonAsync(new { error = "Çok fazla giriş denemesi. Bir dakika sonra tekrar dene." }, ct); };
        });
    }
    public static void UseLocalAccess(this WebApplication app)
    {
        app.UseAuthentication(); app.UseRateLimiter();
        if (IsPublicDeployment(app.Configuration) && !app.Services.GetRequiredService<LocalAccountStore>().Configured)
        {
            app.Services.GetRequiredService<SetupToken>().Ensure();
            app.Logger.LogWarning("İlk yönetici için tek kullanımlık kurulum kodu Data/setup-token.txt dosyasına yazıldı.");
        }
        app.Use(async (context, next) =>
        {
            var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
            var publicDeployment = IsPublicDeployment(configuration);
            var isLoopback = IsLoopback(context);
            var isLocalBootstrapRequest = isLoopback || publicDeployment && IsTrustedBootstrapGatewayRequest(context, configuration);
            if (publicDeployment && !context.Request.IsHttps && !isLocalBootstrapRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (!context.Request.Path.StartsWithSegments("/api")) { await next(); return; }
            var store = context.RequestServices.GetRequiredService<LocalAccountStore>();
            var path = context.Request.Path;
            if (!publicDeployment && (!store.Configured || path.StartsWithSegments("/api/access") || path.StartsWithSegments("/api/system")) && !isLoopback)
            {
                context.Response.StatusCode = 403; return;
            }
            if (publicDeployment && (!store.Configured || path == "/api/access/setup") && !isLocalBootstrapRequest &&
                path != "/api/health" && path != "/api/access/status")
            { context.Response.StatusCode = 403; return; }
            var publicAccess = path == "/api/health" || path == "/api/access/status" || path == "/api/access/login" || path == "/api/access/setup";
            if (store.Configured && !publicAccess && context.User.Identity?.IsAuthenticated != true) { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { error = "Önce giriş yap." }); return; }
            if (store.Configured && context.User.IsInRole("viewer") && context.Request.Method is not ("GET" or "HEAD") && !ReadOnlyPost(context)) { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "Bu hesap yalnızca görüntüleme yetkisine sahip." }); return; }
            if (context.Request.Method is not ("GET" or "HEAD"))
            {
                var origin = context.Request.Headers.Origin.ToString(); var expected = $"{context.Request.Scheme}://{context.Request.Host}";
                if ((origin.Length > 0 && origin != expected) || context.Request.Headers["Sec-Fetch-Site"] == "cross-site" || !context.Request.HasJsonContentType()) { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "Geçersiz istek kaynağı veya biçimi." }); return; }
            }
            context.Response.Headers.CacheControl = "no-store"; await next();
        });
        app.MapGet("/api/access/status", (HttpContext c, LocalAccountStore store) => Results.Ok(new { configured = store.Configured, setupTokenRequired = IsPublicDeployment(app.Configuration) && !store.Configured, authenticated = c.User.Identity?.IsAuthenticated == true, name = c.User.Identity?.Name, role = c.User.FindFirstValue(ClaimTypes.Role),tenantRole=TenantRoles.Current(c), businessId = BusinessWorkspaces.BusinessId(c), canManageBusinesses = TenantRoles.IsPlatform(c) }));
        app.MapPost("/api/access/setup", async (LocalCredentials credentials, HttpContext c, LocalAccountStore store, SetupToken token) =>
        {
            var publicDeployment = IsPublicDeployment(app.Configuration);
            if (publicDeployment && !token.Matches(credentials.SetupToken)) return Results.Json(new { error = "Kurulum kodu geçersiz. Sunucudaki Data/setup-token.txt dosyasındaki kodu girin." }, statusCode: 403);
            try
            {
                var user = store.Create(credentials.Name, credentials.Password, "admin", true);
                if (publicDeployment) token.Remove();
                await SignIn(c, user, 0); return Results.Ok(new { ok = true });
            }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        }).RequireRateLimiting("local-login");
        app.MapPost("/api/access/login", async (LocalCredentials credentials, HttpContext c, LocalAccountStore store) =>
        {
            (UserSummary User, int Version)? result;
            try { result = store.Authenticate(credentials.Name, credentials.Password); }
            catch (LoginThrottledException e) { return Results.Json(new { error = e.Message }, statusCode: 429); }
            if (result is null) return Results.Json(new { error = "Hesap adı veya parola hatalı." }, statusCode: 401);
            await SignIn(c, result.Value.User, result.Value.Version); return Results.Ok(new { ok = true });
        }).RequireRateLimiting("local-login");
        app.MapPost("/api/access/password",async(ChangePasswordInput input,HttpContext c,LocalAccountStore store)=>
        {
            var id=c.User.FindFirstValue(ClaimTypes.NameIdentifier);if(id is null)return Results.StatusCode(401);
            try{var updated=store.ChangePassword(id,input.CurrentPassword,input.NewPassword);await SignIn(c,updated.User,updated.Version);return Results.Ok(new{ok=true});}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
        }).RequireRateLimiting("local-login");
        app.MapPost("/api/access/logout", async (HttpContext c) => { await c.SignOutAsync(); return Results.Ok(new { ok = true }); });
        app.MapGet("/api/access/users", (HttpContext c, LocalAccountStore store) => c.User.IsInRole("admin") ? Results.Ok(store.Users().Where(u=>TenantRoles.IsPlatform(c)||u.BusinessId==BusinessWorkspaces.BusinessId(c))) : Results.StatusCode(403));
        app.MapPost("/api/access/users", (NewLocalUser input, HttpContext c, LocalAccountStore store) =>
        {
            if (!c.User.IsInRole("admin")) return Results.StatusCode(403);
            var business=input.BusinessId??BusinessWorkspaces.BusinessId(c);
            if(!TenantRoles.CanGrant(c,input.Role,business))return Results.StatusCode(403);
            try { return Results.Ok(store.Create(input.Name, input.Password, input.Role,businessId:business)); } catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        });
        app.MapPost("/api/access/users/{id}", (string id, ChangeLocalUser input, HttpContext c, LocalAccountStore store) =>
        {
            if (!c.User.IsInRole("admin")) return Results.StatusCode(403);
            var target=store.Users().FirstOrDefault(u=>u.Id==id);if(target==null)return Results.NotFound();
            if(!TenantRoles.CanGrant(c,input.Role,target.BusinessId)||!TenantRoles.IsPlatform(c)&&TenantRoles.Normalize(target.Role,target.BusinessId) is "platform-admin" or "business-owner")return Results.StatusCode(403);
            try { store.Change(id, input.Role, input.Enabled, c.User.FindFirstValue(ClaimTypes.NameIdentifier)!); return Results.Ok(new { ok = true }); } catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        });
    }
    internal static bool ReadOnlyPost(HttpContext c)
    {
        var path=c.Request.Path.Value??"";
        if(path=="/api/catalog/preview")return true;
        if(path is "/api/access/logout" or "/api/access/login" or "/api/access/password" or "/api/access/switch-tenant")return true;
        if(System.Text.RegularExpressions.Regex.IsMatch(path,@"^/api/integrations/connections/[^/]+/(test|account|products|orders)$"))return true;
        var match=System.Text.RegularExpressions.Regex.Match(path,@"^/api/integrations/trendyol/connections/[^/]+/operations/([^/]+)$");
        return match.Success&&MarketPilot.Web.Integrations.Trendyol.TrendyolCatalog.Operations.TryGetValue(match.Groups[1].Value,out var op)&&!op.Write;
    }
    internal static Task SignIn(HttpContext c, UserSummary user, int version)
    {
        var role=TenantRoles.Normalize(user.Role,user.BusinessId);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.Name), new Claim(ClaimTypes.Role, TenantRoles.Alias(role)),new Claim(ClaimTypes.Role,role),new Claim("tenant_role",role), new Claim("version", version.ToString()),new Claim("business_id",user.BusinessId)], CookieAuthenticationDefaults.AuthenticationScheme);
        return c.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
    }
}
