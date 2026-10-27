namespace MarketPilot.Web.Integrations.Demo;

public sealed class DemoMarketplaceAdapter(DemoWorkspace? workspace = null) : IMarketplaceAdapter
{
    public string Marketplace => "demo";
    private static MarketplaceAccount Account(ConnectionSettings s) => new(s.Marketplace, "DEMO-2738", s.Environment, "TR",
        [new("demo-address", "Shipment", "Türkiye", "İstanbul", "Kadıköy", "34710", "Demo sevkiyat adresi", true)], DateTimeOffset.UtcNow);
    public Task<MarketplaceAccount> TestAsync(ConnectionSettings s, CancellationToken ct) => Task.FromResult(Account(s));
    public Task<MarketplaceAccount> AccountAsync(ConnectionSettings s, CancellationToken ct) => Task.FromResult(Account(s));
    public Task<MarketplacePage<MarketplaceProduct>> ProductsAsync(ConnectionSettings s, ProductQuery q, CancellationToken ct)
    {
        if (workspace is not null) { var result = workspace.Products(s, q); if(s.Marketplace=="amazon")result=result with {Items=result.Items.Select(p=>MarketPilot.Web.Integrations.Amazon.AmazonDemo.IsAmazonFulfilled(p.Barcode)?p with {Stock=null}:p).ToArray()}; return Task.FromResult(result); }
        var all = new[]
        {
            new MarketplaceProduct("701", "501", "DEMO-MAIN-1", "DEMO-001", "8690000000001", "Kablosuz Demo Kulaklık", "1001", "Elektronik", "İnternet Tekstil", 2299m, 1999m, 1899m, 12, "TRY", true, "https://www.trendyol.com/", ["https://cdn.dsmcdn.com/web/logo/ty-web.svg"], "approved", []),
            new MarketplaceProduct("702", "502", "DEMO-MAIN-2", "DEMO-002", "8690000000002", "Demo Seramik Vazo", "1002", "Ev ve Yaşam", "İnternet Tekstil", 849m, 749m, 749m, 4, "TRY", true, "https://www.trendyol.com/", [], "approved", [])
        };
        var source = q.Approval == "unapproved" ? [new MarketplaceProduct("703", "", "DEMO-DRAFT", "DEMO-DRAFT-1", "8690000000003", "Onay bekleyen demo ürün", "1001", "Elektronik", "İnternet Tekstil", 500m, 450m, null, 2, "TRY", false, "", [], "pendingApproval", [])] : all;
        var filtered = source.Where(x => string.IsNullOrWhiteSpace(q.Barcode) || x.Barcode.Contains(q.Barcode, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(q.StockCode) || x.Sku.Contains(q.StockCode, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(q.Status) || x.ApprovalStatus == q.Status || q.Status == "onSale" && x.OnSale == true || q.Status == "notOnSale" && x.OnSale == false).ToArray();
        var items = filtered.Skip(q.Page * q.Size).Take(q.Size).ToArray();
        return Task.FromResult(new MarketplacePage<MarketplaceProduct>(items, q.Page, q.Size, filtered.Length, (int)Math.Ceiling((double)filtered.Length / q.Size), null));
    }
    public Task<MarketplacePage<MarketplaceOrder>> OrdersAsync(ConnectionSettings s, OrderQuery q, CancellationToken ct)
    {
        if (workspace is not null) return Task.FromResult(workspace.Orders(s, q));
        var order = new MarketplaceOrder("92087137", "DEMO-1001", "Created", DateTimeOffset.UtcNow.AddHours(-2), "Demo Müşteri", "demo@example.com", 1999m, 2299m, 300m, "TRY", "Kredi Kartı", "Shipment",
            new("shipment", "Shipment", "Türkiye", "İstanbul", "Kadıköy", "34710", "Demo teslimat adresi", false),
            [new("line-1", "DEMO-001", "8690000000001", "Kablosuz Demo Kulaklık", 1, 1999m, 2299m, 20m, "TRY", "Created", "1001")],
            new("Trendyol Express", "7280027504000001", "https://tracking.trendyol.com/", "210090000001", "606404425", 4.4m, 1),
            new("FTR-DEMO-1", "Invoiced", "https://www.trendyol.com/", false, "", "", "", false, []));
        var matches = (string.IsNullOrWhiteSpace(q.Status) || order.Status == q.Status) &&
            (string.IsNullOrWhiteSpace(q.OrderNumber) || order.OrderNumber.Contains(q.OrderNumber, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(q.Barcode) || order.Lines.Any(x => x.Barcode.Contains(q.Barcode, StringComparison.OrdinalIgnoreCase)));
        var items = matches && q.Page == 0 ? new[] { order } : [];
        return Task.FromResult(new MarketplacePage<MarketplaceOrder>(items, q.Page, q.Size, q.Stream ? null : matches ? 1 : 0, q.Stream ? null : matches ? 1 : 0, null, q.Stream ? false : null, null));
    }
}
