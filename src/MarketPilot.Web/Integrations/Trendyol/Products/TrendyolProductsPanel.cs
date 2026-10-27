namespace MarketPilot.Web.Integrations.Trendyol;

// Ürün V2 servisleri: filtreleme, yaratma, güncelleme, arşiv, silme, kilit, buybox ve toplu işlem sonucu.
public static class TrendyolProductsPanel
{
    public const string Key = "products";
    private const string Seller = "product/sellers/{sellerId}";

    public static readonly TrendyolPanel Panel = new(Key, "Ürünler", "Product V2 filtreleme, ürün yaratma/güncelleme, arşiv, silme, kilit kaldırma ve toplu işlem kontrolü.",
    [
        new("products.approved", Key, "Onaylı ürünleri filtrele (V2)", "GET", $"{Seller}/products/approved")
            { Query = ["barcode", "stockCode", "productMainId", "contentId", "status", "page", "size", "nextPageToken"], Doc = "ürün-filtreleme-onaylı-ürün-v2" },
        new("products.approvedInventoryPrice", Key, "Onaylı ürün stok ve fiyatları (V2)", "GET", $"{Seller}/products/approved/inventory-and-price")
            { Query = ["barcode", "barcodes", "stockCode", "productMainId", "contentId", "status", "page", "size", "nextPageToken", "orderByDirection"], Doc = "ürün-filtreleme-onaylı-ürün-v2-stok-ve-fiyat" },
        new("products.unapproved", Key, "Onaysız ürünleri filtrele (V2)", "GET", $"{Seller}/products/unapproved")
            { Query = ["barcode", "stockCode", "productMainId", "status", "page", "size", "nextPageToken"], Doc = "ürün-filtreleme-onaysız-ürün-v2" },
        new("products.byBarcode", Key, "Barkoda göre temel bilgiler (V2)", "GET", $"{Seller}/product/{{barcode}}") { Doc = "ürün-filtreleme-temel-bilgiler-v2" },
        new("products.filterV1", Key, "Ürün filtreleme (V1)", "GET", $"{Seller}/products")
            { Query = ["approved", "barcode", "stockCode", "startDate", "endDate", "dateQueryType", "page", "size", "archived", "onSale", "productMainId"], Note = "Eski V1 servisidir; yeni geliştirmelerde V2 servislerini kullanın.", Doc = "ürün-filtreleme-filterproducts" },
        new("products.updateAudits", Key, "Ürün güncelleme sonucu (content)", "GET", $"{Seller}/products/{{contentId}}/update-audits") { Doc = "ürün-bilgileri-güncelleme-sonucu-kontrol-servisi" },
        new("products.batchResult", Key, "Toplu işlem sonucu (batchRequestId)", "GET", $"{Seller}/products/batch-requests/{{batchRequestId}}")
            { Note = "createProducts, stok/fiyat, güncelleme, arşiv, silme ve kilit işlemleri batchRequestId döndürür.", Doc = "toplu-i̇şlem-kontrolü-getbatchrequestresult-1" },
        new("products.buybox", Key, "Buybox bilgisi sorgula", "POST", $"{Seller}/products/buybox-information")
            { Body = TrendyolBodyKind.Json, SampleBody = """{"barcodes":["1111111111111"]}""", Note = "Veri değiştirmez; barkod listesi POST gövdesinde gönderilir.", Doc = "ürün-buybox-kontrol-servisi-1" },
        new("products.createV2", Key, "Ürün yarat (V2)", "POST", $"{Seller}/v2/products")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"items":[{"barcode":"","title":"","productMainId":"","brandId":0,"categoryId":0,"quantity":0,"stockCode":"","dimensionalWeight":1,"description":"","currencyType":"TRY","listPrice":0,"salePrice":0,"vatRate":20,"cargoCompanyId":0,"images":[{"url":"https://"}],"attributes":[]}]}""", Doc = "ürün-yaratma-v2" },
        new("products.createV1", Key, "Ürün aktar (V1)", "POST", $"{Seller}/products")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"items":[]}""", Note = "Eski V1 servisidir.", Doc = "ürün-aktarma-v2createproducts" },
        new("products.updateUnapproved", Key, "Onaysız ürün güncelle (V2)", "POST", $"{Seller}/products/unapproved-bulk-update")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"items":[{"barcode":""}]}""", Doc = "ürün-güncelleme-onaysız-ürün-v2" },
        new("products.updateContent", Key, "Onaylı ürün content güncelle (V2)", "POST", $"{Seller}/products/content-bulk-update")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"items":[{"contentId":0}]}""", Doc = "ürün-güncelleme-onaylı-ürün-v2" },
        new("products.updateVariant", Key, "Onaylı ürün varyant güncelle (V2)", "POST", $"{Seller}/products/variant-bulk-update")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"items":[{"barcode":""}]}""", Doc = "ürün-güncelleme-onaylı-ürün-v2" },
        new("products.updateDeliveryInfo", Key, "Onaylı ürün teslimat bilgisi güncelle (V2)", "POST", $"{Seller}/products/delivery-info-bulk-update")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"items":[{"barcode":""}]}""", Doc = "ürün-güncelleme-onaylı-ürün-v2" },
        new("products.archive", Key, "Ürün arşivle / arşivden çıkar", "PUT", $"{Seller}/products/archive-state")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"items":[{"barcode":"","archived":true}]}""", Doc = "ürün-arşivleme" },
        new("products.unlock", Key, "Ürün kilidini kaldır", "PUT", $"{Seller}/products/unlock")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"items":[{"barcode":""}]}""", Doc = "ürün-kilit-kaldırma-servisi" },
        new("products.delete", Key, "Ürün sil", "DELETE", $"{Seller}/products")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"items":[{"barcode":""}]}""", Note = "Onay bekleyen ve arşivde bir günden fazla duran ürünler silinebilir.", Doc = "ürün-silme" },
    ]);
}
