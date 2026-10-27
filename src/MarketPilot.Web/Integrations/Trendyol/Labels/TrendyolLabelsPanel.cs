namespace MarketPilot.Web.Integrations.Trendyol;

// Ortak etiket (kargo barkodu) talebi ve alma.
public static class TrendyolLabelsPanel
{
    public const string Key = "labels";
    private const string Seller = "sellers/{sellerId}";

    public static readonly TrendyolPanel Panel = new(Key, "Ortak Etiket", "Ortak etiket barkod talebi, oluşan barkodun alınması ve AB ürün etiketli çıktı.",
    [
        new("labels.create", Key, "Ortak etiket barkodu talep et", "POST", $"{Seller}/common-label/{{cargoTrackingNumber}}")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"format":"ZPL","boxQuantity":1,"volumetricHeight":1.0}""", Doc = "ortak-etiket-barkod-talebi-createcommonlabel" },
        new("labels.get", Key, "Oluşan ortak etiketi al", "GET", $"{Seller}/common-label/{{cargoTrackingNumber}}") { Doc = "ortak-etiket-oluşan-barkodun-alınması-getcommonlabel" },
        new("labels.withProductLabels", Key, "AB ürün etiketiyle ortak etiket", "GET", $"{Seller}/common-labels/{{cargoTrackingNumber}}/with-product-labels")
            { Query = ["productLabelType"], Note = "productLabelType: ZPL veya PDF.", Doc = "ab-ürün-etiketi-ile-birlikte-ortak-etiket-barkodun-alınması" },
    ]);
}
