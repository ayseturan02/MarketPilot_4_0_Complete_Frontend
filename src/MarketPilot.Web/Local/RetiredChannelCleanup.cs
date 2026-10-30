using System.Text.RegularExpressions;

namespace MarketPilot.Web.Local;

// Old local workspaces and restored backups must not reintroduce a retired channel.
public static class RetiredChannelCleanup
{
    private static readonly Regex RetiredName = new(@"\bwoo[\s_-]*commerce\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsRetired(string? value) => value is not null &&
        RetiredName.Match(value.Trim()) is { Success: true } match && match.Length == value.Trim().Length;

    public static void Apply(Database data)
    {
        var orderIds = data.Orders.Where(o => IsRetired(o.Channel))
            .SelectMany(o => new[] { o.Id, o.ChannelOrderNumber, o.PackageId })
            .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);

        data.Orders.RemoveAll(o => IsRetired(o.Channel));
        data.Connections.RemoveAll(c => IsRetired(c.Name) || IsRetired(c.Id));
        foreach (var product in data.Products)
            product.Channels.RemoveAll(IsRetired);
        data.Shipments.RemoveAll(s => orderIds.Contains(s.OrderId));
        data.Activity.RemoveAll(a => RetiredName.IsMatch(a.Title) || RetiredName.IsMatch(a.Detail));
    }
}
