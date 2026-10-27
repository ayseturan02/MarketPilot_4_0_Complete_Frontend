namespace MarketPilot.Web.Integrations.Trendyol;

// Stok ve fiyat güncelleme. Sonucu Ürünler panelindeki "Toplu işlem sonucu" ile kontrol edilir.
public static class TrendyolInventoryPanel
{
    public const string Key = "inventory";

    public static readonly TrendyolPanel Panel = new(Key, "Stok & Fiyat", "updatePriceAndInventory ile toplu stok ve fiyat güncelleme.",
    [
        new("inventory.updatePriceAndInventory", Key, "Stok ve fiyat güncelle", "POST", "inventory/sellers/{sellerId}/products/price-and-inventory")
        {
            Write = true, Body = TrendyolBodyKind.Json,
            SampleBody = """{"items":[{"barcode":"8680000000","quantity":100,"salePrice":112.85,"listPrice":113.85}]}""",
            Note = "Dönen batchRequestId ile Ürünler → Toplu işlem sonucu servisini kontrol edin.", Doc = "stok-ve-fiyat-güncelleme-updatepriceandinventory"
        },
    ]);
}
