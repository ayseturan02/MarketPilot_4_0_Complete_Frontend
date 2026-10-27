using System.Text.RegularExpressions;

namespace MarketPilot.Web.Integrations.Trendyol;

public enum TrendyolBodyKind { None, Json, Multipart }

/// <summary>One Trendyol endpoint. Path uses {name} placeholders; {sellerId} is filled from server configuration.</summary>
public sealed record TrendyolOperation(string Key, string Panel, string Title, string Method, string Path)
{
    private static readonly Regex Placeholder = new(@"\{([A-Za-z]+)\}", RegexOptions.CultureInvariant);

    public string[] Query { get; init; } = [];
    public bool Write { get; init; }
    public TrendyolBodyKind Body { get; init; }
    public string? SampleBody { get; init; }
    public string[] FormFields { get; init; } = [];
    public string[] FileFields { get; init; } = [];
    public bool StageOnly { get; init; }
    public bool StoreFrontHeader { get; init; }
    public bool SellerIdHeader { get; init; }
    public string Note { get; init; } = "";
    public string Doc { get; init; } = "";

    public IReadOnlyList<string> PathParameters => Placeholder.Matches(Path).Select(m => m.Groups[1].Value).Where(x => x != "sellerId").ToArray();
}

public sealed record TrendyolPanel(string Key, string Title, string Description, IReadOnlyList<TrendyolOperation> Operations);
