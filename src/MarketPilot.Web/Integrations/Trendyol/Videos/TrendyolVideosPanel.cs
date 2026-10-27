namespace MarketPilot.Web.Integrations.Trendyol;

// Ürün videoları: oluşturma ve listeleme.
public static class TrendyolVideosPanel
{
    public const string Key = "videos";
    private const string Videos = "video/sellers/{sellerId}/videos";

    public static readonly TrendyolPanel Panel = new(Key, "Videolar", "Ürün içeriklerine URL üzerinden video ekleme ve video işlem durumlarını sorgulama.",
    [
        new("videos.list", Key, "Videoları listele / sorgula", "GET", Videos)
            { Query = ["id", "sellerIntegrationStatus", "page", "size"], Note = "sellerIntegrationStatus: IN_PROGRESS, SUCCESS, FAILED.", Doc = "seller-integration-video-api" },
        new("videos.create", Key, "Video oluştur", "POST", Videos)
        {
            Write = true, Body = TrendyolBodyKind.Json,
            SampleBody = """{"title":"","description":"","videoUrl":"https://","productContentIds":[""],"videoContentType":"PRODUCT_PROMOTION"}""",
            Note = "Dönen videoId ile listeleme servisinden işlem durumunu kontrol edin.", Doc = "seller-integration-video-api"
        },
    ]);
}
