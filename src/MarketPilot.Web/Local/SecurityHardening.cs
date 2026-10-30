using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration.Json;

namespace MarketPilot.Web.Local;

public static class SecurityHardening
{
    public static bool SecretKey(string key)
    {
        var leaf=key.Split(':').Last().Replace("_","").ToLowerInvariant();
        return leaf is "password" or "apikey" or "apisecret" or "clientsecret" or "refreshtoken" or "accesstoken" or
            "inboundkey" or "previousapikey" or "signingkey" or "uyekodu" or "secret" ||
            leaf.EndsWith("secret",StringComparison.Ordinal)||leaf.EndsWith("password",StringComparison.Ordinal)||leaf.EndsWith("token",StringComparison.Ordinal);
    }
    public static string Mask(string? message,IConfiguration? config=null)
    {
        var text=message??"";
        if(text.Length>16384)text=text[..16384];
        if(config!=null)foreach(var value in config.AsEnumerable().Where(pair=>SecretKey(pair.Key)&&pair.Value?.Length>=4)
            .Select(pair=>pair.Value!).Distinct().OrderByDescending(value=>value.Length))text=text.Replace(value,"[masked]",StringComparison.Ordinal);
        text=Regex.Replace(text,@"(?i)\b(?:Bearer|Basic)\s+[a-z0-9+/=_\-.]+","[authorization masked]",RegexOptions.None,TimeSpan.FromMilliseconds(100));
        text=Regex.Replace(text,@"(?i)\b(?:password|api[_-]?key|api[_-]?secret|client[_-]?secret|token)\s*[:=]\s*[^\s,;&]+","[credential masked]",RegexOptions.None,TimeSpan.FromMilliseconds(100));
        text=Regex.Replace(text,@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b","[email masked]",RegexOptions.None,TimeSpan.FromMilliseconds(100));
        text=Regex.Replace(text,@"(?<!\d)(?:\+?90[ -]?)?0?5\d{2}[ -]?\d{3}[ -]?\d{2}[ -]?\d{2}(?!\d)","[phone masked]",RegexOptions.None,TimeSpan.FromMilliseconds(100));
        text=text.Replace('\r',' ').Replace('\n',' ');
        return text.Length>4000?text[..4000]:text;
    }
    public static void ConfigureSecurity(this WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(server=>
        {
            server.AddServerHeader=false;
            server.Limits.MaxRequestBodySize=8*1024*1024;
            server.Limits.MaxRequestHeaderCount=64;
            server.Limits.MaxRequestHeadersTotalSize=32*1024;
            server.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(15);
            server.Limits.MaxConcurrentConnections=256;
            server.Limits.MaxConcurrentUpgradedConnections=16;
            server.Limits.KeepAliveTimeout=TimeSpan.FromSeconds(60);
        });
        builder.Logging.AddFilter("System.Net.Http.HttpClient",LogLevel.None);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics",LogLevel.Warning);
        // The app has its own audit store; Windows Event Log must not require administrator access for sign-in.
        builder.Services.Configure<LoggerFilterOptions>(options=>options.Rules.Add(new LoggerFilterRule(
            "Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider",null,LogLevel.None,null)));
        // Secrets can come from environment/key-per-file providers, never a JSON configuration file.
        foreach(var provider in ((IConfigurationRoot)builder.Configuration).Providers.OfType<JsonConfigurationProvider>())
        foreach(var pair in builder.Configuration.AsEnumerable().Where(pair=>SecretKey(pair.Key)))
            if(provider.TryGet(pair.Key,out var value)&&!string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("Kimlik bilgisi JSON yapılandırmasında saklanamaz; environment veya secret-file referansı kullanın.");
    }
    public static void SecurityHeaders(HttpContext context)
    {
        var h=context.Response.Headers;
        h.XContentTypeOptions="nosniff";h.XFrameOptions="DENY";
        h["Referrer-Policy"]="no-referrer";h["Cross-Origin-Opener-Policy"]="same-origin";
        h["Cross-Origin-Resource-Policy"]="same-origin";
        h["Permissions-Policy"]="camera=(), microphone=(), geolocation=()";
        if(context.Request.IsHttps&&LocalAccess.IsPublicDeployment(context.RequestServices.GetRequiredService<IConfiguration>()))
            h.StrictTransportSecurity="max-age=31536000";
        h.ContentSecurityPolicy="default-src 'self'; script-src 'self' 'unsafe-inline'; script-src-elem 'self'; script-src-attr 'unsafe-inline'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; img-src 'self' https: data:; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
        if(context.Request.Path.StartsWithSegments("/api")||context.Request.Path.StartsWithSegments("/hooks"))h.CacheControl="no-store";
    }
}
