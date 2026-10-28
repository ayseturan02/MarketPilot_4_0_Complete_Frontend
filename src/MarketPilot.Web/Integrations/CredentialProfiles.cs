using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MarketPilot.Web.Integrations.Amazon;
using MarketPilot.Web.Integrations.Boyner;
using MarketPilot.Web.Integrations.Hepsiburada;
using MarketPilot.Web.Integrations.N11;
using MarketPilot.Web.Integrations.Ticimax;
using MarketPilot.Web.Integrations.Trendyol;

namespace MarketPilot.Web.Integrations;

// Profiles are server configuration, never persisted with connection records or returned as credentials.
public static class CredentialProfiles
{
    public const string Unassigned = "_unassigned";
    public static readonly string[] Markets = ["trendyol", "hepsiburada", "n11", "amazon", "boyner", "ticimax"];
    public static IConfiguration For(IConfiguration root, ConnectionSettings s)
    {
        if (s.Mode == "demo" || s.CredentialProfile is null) return root; // Existing single-account configuration remains readable.
        if (!Regex.IsMatch(s.CredentialProfile, "^[a-zA-Z0-9_-]{1,64}$") || s.CredentialProfile == Unassigned)
            throw new IntegrationException("credential_profile_missing", "Bu mağazaya sunucuda tanımlı bir kimlik profili seçilmeli.", 409);
        var p = root.GetSection("MarketplaceAccounts:" + s.CredentialProfile);
        if (!p.Exists() || !string.Equals(p["Marketplace"], s.Marketplace, StringComparison.OrdinalIgnoreCase) || p["Environment"] != s.Environment)
            throw new IntegrationException("credential_profile_mismatch", "Kimlik profili bulunamadı veya mağazanın pazaryeri/ortamıyla eşleşmiyor.", 409);
        var values = p.GetSection("Credentials").AsEnumerable(true).Where(x => x.Value != null)
            .ToDictionary(x => $"Marketplaces:{s.Marketplace}:{s.Environment}:{x.Key}", x => x.Value, StringComparer.OrdinalIgnoreCase);
        values[$"Marketplaces:{s.Marketplace}:{s.Environment}:AllowWrites"] = p.GetValue<bool>("AllowWrites").ToString();
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
    public static bool Configured(IConfiguration c, string m, string e) => m switch
    {
        "trendyol" => TrendyolApiClient.IsConfigured(c, e), "hepsiburada" => HepsiburadaAdapter.IsConfigured(c, e),
        "n11" => N11Adapter.IsConfigured(c, e), "amazon" => AmazonApiClient.IsConfigured(c, e),
        "boyner" => BoynerAdapter.IsConfigured(c, e), "ticimax" => TicimaxSoap.IsConfigured(c, e), _ => false
    };
    public static bool Writes(IConfiguration c, ConnectionSettings s) =>
        Configured(c, s.Marketplace, s.Environment) && c.GetValue<bool>($"Marketplaces:{s.Marketplace}:{s.Environment}:AllowWrites");
    public static string Identity(IConfiguration root, ConnectionSettings s)
    {
        if (s.Mode == "demo") return "";
        IConfiguration scoped;
        try { scoped = For(root, s); } catch (IntegrationException) { return "unavailable:" + s.CredentialProfile; }
        return Fingerprint(scoped,s);
    }
    public static string Fingerprint(IConfiguration scoped,ConnectionSettings s)
    {
        var values = scoped.GetSection($"Marketplaces:{s.Marketplace}:{s.Environment}").AsEnumerable(true)
            .Where(x => x.Value != null && !x.Key.Equals("AllowWrites", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Marketplace + "|" + s.Environment + "|" + s.CredentialProfile + "|" +
            string.Join("\n", values.Select(v => v.Key.ToLowerInvariant() + "=" + v.Value)))));
    }
    public static object Readiness(IConfiguration root,ConnectionState store)
    {
        try {
            var c=For(root,store.Settings);
            return new{id=store.Id,credentialProfile=store.Settings.CredentialProfile,configured=store.Settings.Mode=="demo"||Configured(c,store.Settings.Marketplace,store.Settings.Environment),
                writes=store.Settings.Mode=="demo"||root.GetValue<bool>("Synchronization:AllowRealWrites")&&Writes(c,store.Settings)};
        }catch(IntegrationException){return new{id=store.Id,credentialProfile=store.Settings.CredentialProfile,configured=false,writes=false};}
    }
    public static object[] List(IConfiguration root) => root.GetSection("MarketplaceAccounts").GetChildren()
        .Where(p => p.Key != Unassigned && Regex.IsMatch(p.Key, "^[a-zA-Z0-9_-]{1,64}$") && Markets.Contains(p["Marketplace"]?.ToLowerInvariant()) && p["Environment"] is "stage" or "production")
        .Select(p =>
        {
            var s = new ConnectionSettings("", p["Marketplace"]!.ToLowerInvariant(), "real", p["Environment"]!, p.Key);
            var c = For(root, s);
            return (object)new { id = p.Key, label = p["Label"] ?? p.Key, marketplace = s.Marketplace, environment = s.Environment,
                configured = Configured(c, s.Marketplace, s.Environment), writes = Writes(c, s) };
        }).ToArray();
}

// Each immutable bundle has its own OAuth token and WSDL caches. No shared ambient/mutable credentials.
public sealed class MarketplaceClients(IConfiguration root, IHttpClientFactory http, IServiceProvider services)
{
    private readonly ConcurrentDictionary<string, Lazy<Bundle>> bundles = new();
    public sealed record Bundle(IMarketplaceAdapter Adapter, IConfiguration Config, TrendyolOperationRunner? Runner, TicimaxSoap? Soap, AmazonApiClient? Amazon);
    public Bundle Get(ConnectionSettings s)
    {
        var config = CredentialProfiles.For(root, s);
        var key = s.Marketplace + "|" + s.Environment + "|" + CredentialProfiles.Fingerprint(config, s) + "|" + CredentialProfiles.Writes(config,s);
        return bundles.GetOrAdd(key, _ => new Lazy<Bundle>(() =>
        {
            var client = http.CreateClient("MarketplaceScoped");
            return s.Marketplace switch
            {
                "trendyol" => new(new TrendyolAdapter(client, config), config, new(new TrendyolApiClient(client, config), config), null, null),
                "hepsiburada" => new(new HepsiburadaAdapter(client, config), config, null, null, null),
                "n11" => new(new N11Adapter(client, config), config, null, null, null),
                "boyner" => new(new BoynerAdapter(client, config), config, null, null, null),
                "amazon" => AmazonBundle(config),
                "ticimax" => TicimaxBundle(config),
                _ => throw new IntegrationException("unsupported_marketplace", "Pazaryeri desteklenmiyor.", 409)
            };
        })).Value;
    }
    private Bundle AmazonBundle(IConfiguration c) { var api = new AmazonApiClient(http, c); return new(new AmazonSpApiAdapter(api), c, null, null, api); }
    private Bundle TicimaxBundle(IConfiguration c) { var soap = new TicimaxSoap(http, c); return new(new TicimaxAdapter(soap), c, null, soap, null); }
    public Bundle Session(ConnectionSettings s) => s.CredentialProfile is null
        ? new(Adapter(s),root,s.Marketplace=="trendyol"?Trendyol(s):null,s.Marketplace=="ticimax"?Ticimax(s):null,s.Marketplace=="amazon"?Amazon(s):null) : Get(s);
    public IMarketplaceAdapter Adapter(ConnectionSettings s) => s.CredentialProfile is null
        ? services.GetServices<IMarketplaceAdapter>().First(a => a.Marketplace == s.Marketplace) : Get(s).Adapter;
    public T Adapter<T>(ConnectionSettings s) where T : class, IMarketplaceAdapter => (T)Adapter(s);
    public TrendyolOperationRunner Trendyol(ConnectionSettings s) => s.CredentialProfile is null ? services.GetRequiredService<TrendyolOperationRunner>() : Get(s).Runner!;
    public TicimaxSoap Ticimax(ConnectionSettings s) => s.CredentialProfile is null ? services.GetRequiredService<TicimaxSoap>() : Get(s).Soap!;
    public AmazonApiClient Amazon(ConnectionSettings s) => s.CredentialProfile is null ? services.GetRequiredService<AmazonApiClient>() : Get(s).Amazon!;
    public IConfiguration Configuration(ConnectionSettings s) => CredentialProfiles.For(root, s);
    public static IMarketplaceAdapter Resolve(IServiceProvider sp, ConnectionSettings s) =>
        sp.GetService<MarketplaceClients>()?.Adapter(s) ?? sp.GetServices<IMarketplaceAdapter>().First(a => a.Marketplace == s.Marketplace);
}
