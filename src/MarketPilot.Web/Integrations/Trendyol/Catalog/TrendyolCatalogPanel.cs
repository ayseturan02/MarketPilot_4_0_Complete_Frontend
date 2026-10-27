namespace MarketPilot.Web.Integrations.Trendyol;

// Marka, kategori, kategori özellikleri ve kargo firması listeleri.
public static class TrendyolCatalogPanel
{
    public const string Key = "catalog";

    public static readonly TrendyolPanel Panel = new(Key, "Marka & Kategori", "Marka listesi/yaratma, kategori ağacı, kategori özellikleri ve özellik değerleri.",
    [
        new("catalog.brands", Key, "Marka listesi", "GET", "product/brands") { Query = ["page", "size"], Doc = "trendyol-marka-listesi-getbrands" },
        new("catalog.brandsByName", Key, "Marka ara (isim)", "GET", "product/brands/by-name") { Query = ["name"], Doc = "trendyol-marka-listesi-getbrands" },
        new("catalog.categories", Key, "Kategori ağacı", "GET", "product/product-categories") { Query = ["name"], Note = "Yanıt büyüktür; isimle filtrelemek için name kullanın.", Doc = "trendyol-kategori-listesi-getcategorytree" },
        new("catalog.categoryAttributes", Key, "Kategori özellikleri (V2)", "GET", "product/categories/{categoryId}/attributes") { Doc = "kategori-özellik-listesi-v2" },
        new("catalog.categoryAttributeValues", Key, "Kategori özellik değerleri (V2)", "GET", "product/categories/{categoryId}/attributes/{attributeId}/values") { Query = ["page", "size"], Doc = "kategori-özellik-değerleri-listesi-v2" },
        new("catalog.categoryAttributesV1", Key, "Kategori özellikleri (V1)", "GET", "product/product-categories/{categoryId}/attributes") { Note = "Eski V1 servisidir.", Doc = "trendyol-kategori-özellik-listesi-getcategoryattributes" },
        new("catalog.cargoProviders", Key, "Kargo firmaları (lookup)", "GET", "product/lookup/cargo-providers") { Doc = "kargo-firması-filtreleme-servisi" },
        new("catalog.createBrand", Key, "Marka yarat", "POST", "product/sellers/{sellerId}/brands")
            { Write = true, Body = TrendyolBodyKind.Multipart, FormFields = ["name"], FileFields = ["images"], Note = "En az 1, en fazla 3 marka görseli gönderilir.", Doc = "marka-yaratma-servisi" },
    ]);
}
