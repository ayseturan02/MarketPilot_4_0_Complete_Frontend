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
}
public sealed record UserSummary(string Id, string Name, string Role, bool Enabled);
public sealed record LocalCredentials(string Name, string Password);
public sealed record NewLocalUser(string Name, string Password, string Role);
public sealed record ChangeLocalUser(string Role, bool Enabled);
public sealed record ChangePasswordInput(string CurrentPassword,string NewPassword);

public sealed class LocalAccountStore
{
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
        try { var tmp = file + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(users, json)); File.Move(tmp, file, true); }
        catch { users = JsonSerializer.Deserialize<List<LocalUser>>(previous, json)!; throw; }
    }
    private static byte[] Hash(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA256, 32);
    private static UserSummary Summary(LocalUser user) => new(user.Id, user.Name, user.Role, user.Enabled);
    public UserSummary Create(string name, string password, string role, bool initial = false)
    {
        lock (gate)
        {
            if (initial && users.Count > 0) throw new ArgumentException("Yönetici hesabı zaten oluşturulmuş.");
            name = (name ?? "").Trim().ToLowerInvariant();
            if (!Regex.IsMatch(name, @"^[a-z0-9][a-z0-9._@-]{2,79}$")) throw new ArgumentException("Hesap adı 3–80 karakter olmalı; harf, rakam, nokta, tire veya @ kullanılabilir.");
            if (password is null || password.Length is < 12 or > 128) throw new ArgumentException("Parola 12–128 karakter olmalı.");
            if (!new[] { "admin", "operator", "viewer" }.Contains(role)) throw new ArgumentException("Geçersiz yetki.");
            if (users.Any(u => u.Name == name)) throw new ArgumentException("Bu hesap adı zaten kullanılıyor.");
            var before = JsonSerializer.Serialize(users, json); var salt = RandomNumberGenerator.GetBytes(16);
            var user = new LocalUser { Name = name, Salt = Convert.ToBase64String(salt), PasswordHash = Convert.ToBase64String(Hash(password, salt)), Role = initial ? "admin" : role };
            users.Add(user); Save(before); return Summary(user);
        }
    }
    public (UserSummary User, int Version)? Authenticate(string name, string password)
    {
        lock (gate)
        {
            if (password is null || password.Length > 128) return null;
            var user = users.FirstOrDefault(u => u.Name == (name ?? "").Trim().ToLowerInvariant());
            var actual = Hash(password, user is null ? new byte[16] : Convert.FromBase64String(user.Salt));
            if (user is null || !user.Enabled || !CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(user.PasswordHash))) return null;
            return (Summary(user), user.Version);
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
            if (!new[] { "admin", "operator", "viewer" }.Contains(role)) throw new ArgumentException("Geçersiz yetki.");
            var user = users.FirstOrDefault(u => u.Id == id) ?? throw new ArgumentException("Kullanıcı bulunamadı.");
            if (id == actor && (!enabled || role != "admin")) throw new ArgumentException("Kendi yönetici erişimini bu ekrandan kapatamazsın.");
            if (user.Role == "admin" && user.Enabled && (!enabled || role != "admin") && users.Count(u => u.Role == "admin" && u.Enabled) <= 1) throw new ArgumentException("Son yönetici hesabı kapatılamaz.");
            var before = JsonSerializer.Serialize(users, json); user.Role = role; user.Enabled = enabled; user.Version++; Save(before);
        }
    }
}

public static class LocalAccess
{
    public static void AddLocalAccess(this WebApplicationBuilder builder)
    {
        var keys = Path.Combine(builder.Environment.ContentRootPath, "Data", "keys"); Directory.CreateDirectory(keys);
        builder.Services.AddDataProtection().SetApplicationName("InternetTekstil.Local").PersistKeysToFileSystem(new DirectoryInfo(keys));
        builder.Services.AddSingleton<LocalAccountStore>();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = "it_session"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = false;
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
            o.Events.OnValidatePrincipal = async c =>
            {
                var store = c.HttpContext.RequestServices.GetRequiredService<LocalAccountStore>();
                if (!store.ValidateSession(c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), c.Principal?.FindFirstValue("version"))) { c.RejectPrincipal(); await c.HttpContext.SignOutAsync(); }
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
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api")) { await next(); return; }
            var store = context.RequestServices.GetRequiredService<LocalAccountStore>();
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api/access") || path.StartsWithSegments("/api/system"))
            {
                if (context.Connection.RemoteIpAddress is not { } ip || !System.Net.IPAddress.IsLoopback(ip)) { context.Response.StatusCode = 403; return; }
            }
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
        app.MapGet("/api/access/status", (HttpContext c, LocalAccountStore store) => Results.Ok(new { configured = store.Configured, authenticated = c.User.Identity?.IsAuthenticated == true, name = c.User.Identity?.Name, role = c.User.FindFirstValue(ClaimTypes.Role) }));
        app.MapPost("/api/access/setup", async (LocalCredentials credentials, HttpContext c, LocalAccountStore store) =>
        {
            try { var user = store.Create(credentials.Name, credentials.Password, "admin", true); await SignIn(c, user, 0); return Results.Ok(new { ok = true }); }
            catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        }).RequireRateLimiting("local-login");
        app.MapPost("/api/access/login", async (LocalCredentials credentials, HttpContext c, LocalAccountStore store) =>
        {
            var result = store.Authenticate(credentials.Name, credentials.Password);
            if (result is null) return Results.Json(new { error = "Hesap adı veya parola hatalı." }, statusCode: 401);
            await SignIn(c, result.Value.User, result.Value.Version); return Results.Ok(new { ok = true });
        }).RequireRateLimiting("local-login");
        app.MapPost("/api/access/password",async(ChangePasswordInput input,HttpContext c,LocalAccountStore store)=>
        {
            var id=c.User.FindFirstValue(ClaimTypes.NameIdentifier);if(id is null)return Results.StatusCode(401);
            try{var updated=store.ChangePassword(id,input.CurrentPassword,input.NewPassword);await SignIn(c,updated.User,updated.Version);return Results.Ok(new{ok=true});}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
        }).RequireRateLimiting("local-login");
        app.MapPost("/api/access/logout", async (HttpContext c) => { await c.SignOutAsync(); return Results.Ok(new { ok = true }); });
        app.MapGet("/api/access/users", (HttpContext c, LocalAccountStore store) => c.User.IsInRole("admin") ? Results.Ok(store.Users()) : Results.StatusCode(403));
        app.MapPost("/api/access/users", (NewLocalUser input, HttpContext c, LocalAccountStore store) =>
        {
            if (!c.User.IsInRole("admin")) return Results.StatusCode(403);
            try { return Results.Ok(store.Create(input.Name, input.Password, input.Role)); } catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        });
        app.MapPost("/api/access/users/{id}", (string id, ChangeLocalUser input, HttpContext c, LocalAccountStore store) =>
        {
            if (!c.User.IsInRole("admin")) return Results.StatusCode(403);
            try { store.Change(id, input.Role, input.Enabled, c.User.FindFirstValue(ClaimTypes.NameIdentifier)!); return Results.Ok(new { ok = true }); } catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
        });
    }
    private static bool ReadOnlyPost(HttpContext c)
    {
        var path=c.Request.Path.Value??"";
        if(path is "/api/access/logout" or "/api/access/login" or "/api/access/password")return true;
        if(System.Text.RegularExpressions.Regex.IsMatch(path,@"^/api/integrations/connections/[^/]+/(test|account|products|orders)$"))return true;
        var match=System.Text.RegularExpressions.Regex.Match(path,@"^/api/integrations/trendyol/connections/[^/]+/operations/([^/]+)$");
        return match.Success&&MarketPilot.Web.Integrations.Trendyol.TrendyolCatalog.Operations.TryGetValue(match.Groups[1].Value,out var op)&&!op.Write;
    }
    private static Task SignIn(HttpContext c, UserSummary user, int version)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.Name), new Claim(ClaimTypes.Role, user.Role), new Claim("version", version.ToString())], CookieAuthenticationDefaults.AuthenticationScheme);
        return c.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
    }
}
