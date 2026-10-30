using MarketPilot.Web.Integrations;
namespace MarketPilot.Web.Synchronization;
public static class MarketplaceOrderReader
{
    public static async Task<IReadOnlyList<MarketplaceOrder>> ReadAllAsync(IMarketplaceAdapter adapter, ConnectionSettings settings, DateTimeOffset from, DateTimeOffset until, CancellationToken ct,bool includeHepsiburadaPackages=false)
    {
        var orders = new Dictionary<string, MarketplaceOrder>();
        var requests = 0;
        var start=from;
        foreach(var status in includeHepsiburadaPackages&&settings.Marketplace=="hepsiburada"?new string?[]{null,"Picking"}:new string?[]{null})
        {
            from=start;
            // Package reads have a separate 24-hour/10-row native limit.
            var packageRead=status=="Picking";
            while (from < until)
            {
                var window=from.AddDays(packageRead?1:2);
                var end = window < until ? window : until;
                var page = 0; string? cursor = null;
                var tokens = new HashSet<string>(); var fingerprints = new HashSet<string>();
                while (true)
                {
                    if (++requests > 1000) throw new IntegrationException("scan_limit", "Sipariş taraması sınırı aşıldı; hiçbir sayfa tamamlanmış sayılmadı.", 409);
                    var result = await adapter.OrdersAsync(settings, new(Page: page, Size: packageRead?10:50,Status:status, StartDate: from.ToUnixTimeMilliseconds(), EndDate: end.ToUnixTimeMilliseconds(),
                        OrderByDirection: "ASC", Stream: settings.Marketplace == "trendyol", NextCursor: cursor), ct);
                    if (result.Items.Count > 0)
                    {
                        var signature = string.Join("|", result.Items.Select(o => o.PackageId + ":" + o.OrderNumber + ":" + o.LastModifiedDate));
                        if (!fingerprints.Add(signature)) throw new IntegrationException("repeated_page", "Sipariş servisi aynı sayfayı tekrarladı; dağıtım durduruldu.", 409);
                    }
                    foreach (var order in result.Items)
                    {
                        if(settings.Marketplace=="hepsiburada"&&order.Lines.Any(line=>string.IsNullOrWhiteSpace(line.Id)))
                            throw new IntegrationException("order_line_identity_missing","Hepsiburada kalem kimliği eksik; stok tahmin edilmedi.",409);
                        var identity=order.PackageId.Length>0?order.PackageId:order.OrderNumber;
                        orders[(settings.Marketplace=="hepsiburada"?(packageRead?"package:":"order:"):"")+identity]=order;
                    }
                    if (orders.Count > 200000) throw new IntegrationException("scan_limit", "Sipariş taraması kayıt sınırını aştı.", 409);
                    var next = !string.IsNullOrWhiteSpace(result.NextCursor) ? result.NextCursor : result.NextPageToken;
                    if (!string.IsNullOrWhiteSpace(next))
                    {
                        if (next.Length > 8192 || !tokens.Add(next)) throw new IntegrationException("repeated_cursor", "Sipariş imleci geçersiz veya tekrarlanıyor.", 409);
                        cursor = next; page++; continue;
                    }
                    if (result.HasMore == true) throw new IntegrationException("missing_cursor", "Sipariş devam bilgisi eksik.", 409);
                    if (result.HasMore == false || result.TotalPages.HasValue && page + 1 >= result.TotalPages.Value ||
                        !result.TotalPages.HasValue && result.Items.Count < result.Size) break;
                    if (result.Items.Count == 0) throw new IntegrationException("incomplete_page", "Sipariş sayfası beklenmedik şekilde boş.", 409);
                    page++;
                }
                from = end;
            }
        }
        return orders.Values.ToArray();
    }

}
