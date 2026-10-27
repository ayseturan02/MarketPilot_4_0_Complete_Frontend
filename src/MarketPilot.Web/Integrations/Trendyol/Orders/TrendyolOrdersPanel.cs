namespace MarketPilot.Web.Integrations.Trendyol;

// Sipariş paketleri: çekme, statü, tedarik edememe, bölme, kargo/desi/depo, alternatif teslimat ve işçilik bedeli.
public static class TrendyolOrdersPanel
{
    public const string Key = "orders";
    private const string Seller = "order/sellers/{sellerId}";
    private const string Package = Seller + "/shipment-packages/{packageId}";

    public static readonly TrendyolPanel Panel = new(Key, "Siparişler", "Sipariş paketlerini çekme, statü bildirimi, paket bölme, kargo ve teslimat işlemleri.",
    [
        new("orders.packages", Key, "Sipariş paketleri (V2)", "GET", $"{Seller}/v2/orders")
            { Query = ["startDate", "endDate", "page", "size", "orderNumber", "status", "orderByField", "orderByDirection", "shipmentPackageIds", "invoiceStatus"], Note = "Tarihler milisaniye cinsinden; aralık en fazla 2 hafta.", Doc = "sipariş-paketlerini-çekme-getshipmentpackages" },
        new("orders.packagesV1", Key, "Sipariş paketleri (V1)", "GET", $"{Seller}/orders")
            { Query = ["startDate", "endDate", "page", "size", "orderNumber", "status", "orderByField", "orderByDirection", "shipmentPackageIds"], Note = "Eski V1 servisidir.", Doc = "sipariş-paketlerini-çekme-getshipmentpackages" },
        new("orders.stream", Key, "Sipariş paketleri akışı (cursor)", "GET", $"{Seller}/orders/stream")
            { Query = ["lastModifiedStartDate", "lastModifiedEndDate", "packageItemStatuses", "size", "nextCursor"], Note = "Yüksek hacim için; tarih aralığı en fazla 14 gün.", Doc = "sipariş-paketlerini-akış-ile-çekme" },
        new("orders.updatePackage", Key, "Paket statüsü bildir (Picking / Invoiced)", "PUT", Package)
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"lines":[{"lineId":0,"quantity":1}],"params":{},"status":"Picking"}""", Note = "Önce Picking, sonra Invoiced (params.invoiceNumber) gönderilir.", Doc = "paket-statü-bildirimi-updatepackage" },
        new("orders.unsupplied", Key, "Tedarik edememe bildir", "PUT", $"{Package}/items/unsupplied")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"lines":[{"lineId":0,"quantity":1}],"reasonId":0}""", Note = "Paket bozulur ve yeni ID'li paket oluşur.", Doc = "tedarik-edememe-bildirimi-updatepackage" },
        new("orders.changeCargoProvider", Key, "Paket kargo firmasını değiştir", "PUT", $"{Package}/cargo-providers")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"cargoProvider":"YKMP"}""", Doc = "paket-kargo-firması-değiştirmechangecargoprovider" },
        new("orders.boxInfo", Key, "Desi ve koli bilgisi bildir", "PUT", $"{Package}/box-info")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"boxQuantity":1,"deci":1.0}""", Doc = "desi-ve-koli-bilgisi-bildirimi-updateboxinfo" },
        new("orders.warehouse", Key, "Depo bilgisini güncelle", "PUT", $"{Package}/warehouse")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"warehouseId":0}""", Doc = "depo-bilgisi-güncelleme" },
        new("orders.laborCosts", Key, "İşçilik bedeli tutarı gönder", "PUT", $"{Package}/labor-costs")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """[{"orderLineId":0,"laborCostPerItem":0}]""", Doc = "i̇şçilik-bedeli-tutarı-gönderme" },
        new("orders.splitMultiPackageByQuantity", Key, "Paketi birden fazla barkodla böl", "POST", $"{Package}/split-packages")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"splitPackages":[{"packageDetails":[{"orderLineId":0,"quantities":1}]}]}""", Doc = "sipariş-paketlerini-bölme-splitshipmentpackage" },
        new("orders.split", Key, "Paketi böl (orderLineIds)", "POST", $"{Package}/split")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"orderLineIds":[0]}""", Doc = "sipariş-paketlerini-bölme-splitshipmentpackage" },
        new("orders.multiSplit", Key, "Paketi tek istekte birden fazla pakete böl", "POST", $"{Package}/multi-split")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"splitGroups":[{"orderLineIds":[0]}]}""", Doc = "sipariş-paketlerini-bölme-splitshipmentpackage" },
        new("orders.quantitySplit", Key, "Paketi barkod/adet bazlı böl", "POST", $"{Package}/quantity-split")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"quantitySplit":[{"orderLineId":0,"quantities":[1]}]}""", Doc = "sipariş-paketlerini-bölme-splitshipmentpackage" },
        new("orders.alternativeDelivery", Key, "Alternatif teslimat ile gönder", "PUT", $"{Package}/alternative-delivery")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"isPhoneNumber":false,"trackingInfo":"https://","params":{}}""", Note = "Kargo linki, telefon numarası veya dijital ürün teslimatı için kullanılır.", Doc = "alternatif-teslimat-i̇le-gönderim" },
        new("orders.manualDeliverByTracking", Key, "Alternatif teslimatı teslim et (takip no)", "PUT", $"{Seller}/manual-deliver/{{cargoTrackingNumber}}")
            { Write = true, Doc = "alternatif-teslimat-i̇le-gönderim" },
        new("orders.manualDeliverByPackage", Key, "Alternatif teslimatı teslim et (paket no)", "PUT", $"{Package}/manual-deliver")
            { Write = true, Doc = "alternatif-teslimat-i̇le-gönderim" },
        new("orders.manualReturnByTracking", Key, "Alternatif teslimatı iade et (takip no)", "PUT", $"{Seller}/manual-return/{{cargoTrackingNumber}}")
            { Write = true, Doc = "alternatif-teslimat-i̇le-gönderim" },
        new("orders.manualReturnByPackage", Key, "Alternatif teslimatı iade et (paket no)", "PUT", $"{Package}/manual-return")
            { Write = true, Doc = "alternatif-teslimat-i̇le-gönderim" },
        new("orders.deliveredByService", Key, "Yetkili servis ile teslim edildi", "PUT", $"{Package}/delivered-by-service")
            { Write = true, Doc = "yetkili-servis-i̇le-gönderim" },
    ]);
}
