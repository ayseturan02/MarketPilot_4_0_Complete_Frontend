namespace MarketPilot.Web.Integrations.Trendyol;

// Yeni bir Trendyol paneli eklemek için: kendi klasöründe TrendyolXxxPanel sınıfını yazıp bu listeye ekleyin.
public static class TrendyolCatalog
{
    public static readonly IReadOnlyList<TrendyolPanel> Panels =
    [
        TrendyolProductsPanel.Panel,
        TrendyolCatalogPanel.Panel,
        TrendyolInventoryPanel.Panel,
        TrendyolOrdersPanel.Panel,
        TrendyolClaimsPanel.Panel,
        TrendyolInvoicesPanel.Panel,
        TrendyolLabelsPanel.Panel,
        TrendyolQuestionsPanel.Panel,
        TrendyolFinancePanel.Panel,
        TrendyolWebhooksPanel.Panel,
        TrendyolAddressesPanel.Panel,
        TrendyolVideosPanel.Panel,
        TrendyolTestEnvironmentPanel.Panel,
    ];

    public static readonly IReadOnlyDictionary<string, TrendyolOperation> Operations =
        Panels.SelectMany(p => p.Operations).ToDictionary(o => o.Key, StringComparer.Ordinal);
}
