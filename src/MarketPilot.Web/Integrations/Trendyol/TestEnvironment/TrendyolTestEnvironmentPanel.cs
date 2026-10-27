namespace MarketPilot.Web.Integrations.Trendyol;

// Stage test servisleri ve stage erişimi için IP whitelist yönetimi.
public static class TrendyolTestEnvironmentPanel
{
    public const string Key = "test";
    private const string Whitelist = "product/sellers/{sellerId}/ip-whitelists";

    public static readonly TrendyolPanel Panel = new(Key, "Test Ortamı", "Stage test siparişi, test statü güncellemesi, test müşteri sorusu ve stage IP whitelist yönetimi.",
    [
        new("test.createOrder", Key, "Test siparişi oluştur", "POST", "test/order/orders/core")
            { Write = true, StageOnly = true, SellerIdHeader = true, Body = TrendyolBodyKind.Json, SampleBody = """{"customer":{"customerFirstName":"John","customerLastName":"Doe"},"invoiceAddress":{"addressText":"test deneme adresi","city":"İzmir","district":"Bornova","invoiceFirstName":"John","invoiceLastName":"Doe","phone":"333333333","email":"test@example.com"},"lines":[{"barcode":"","quantity":1}],"seller":{"sellerId":0},"shippingAddress":{"addressText":"test deneme adresi","city":"İzmir","district":"Bornova","shippingFirstName":"John","shippingLastName":"Doe","phone":"333333333","email":"test@example.com"},"commercial":false}""", Note = "Kurumsal fatura için commercial=true; mikro ihracat için microRegion (AZ / GULF).", Doc = "test-siparişi-oluşturma" },
        new("test.updatePackageStatus", Key, "Test paket statüsü güncelle", "PUT", "test/order/sellers/{sellerId}/shipment-packages/{packageId}/status")
            { Write = true, StageOnly = true, Body = TrendyolBodyKind.Json, SampleBody = """{"lines":[{"lineId":0,"quantity":1}],"params":{},"status":"Shipped"}""", Note = "Shipped, Delivered, AtCollectionPoint, UnDelivered, Returned.", Doc = "test-siparişlerinde-statü-güncellemeleri" },
        new("test.claimsWaitingInAction", Key, "Test iadesini WaitingInAction yap", "PUT", "test/order/sellers/{sellerId}/claims/waiting-in-action")
            { Write = true, StageOnly = true, Body = TrendyolBodyKind.Json, SampleBody = """{"shipmentPackageId":0}""", Note = "shipmentPackageId = getClaims yanıtındaki orderShipmentPackageId.", Doc = "test-siparişlerinde-statü-güncellemeleri" },
        new("test.createQuestion", Key, "Test müşteri sorusu oluştur", "POST", "qna/sellers/{sellerId}/questions")
            { Write = true, StageOnly = true, Body = TrendyolBodyKind.Json, SampleBody = """{"text":"","contentId":0,"userId":0,"userFullName":"Test Kullanıcı","showUserName":false,"channelId":1}""", Doc = "stage-ortamda-müşteri-sorusu-oluşturma" },
        new("test.ipWhitelists", Key, "Stage IP whitelist listesi", "GET", Whitelist) { Note = "Canlı (production) hesabıyla çağrılır.", Doc = "test-ortam-ip-whitelist-yönetimi" },
        new("test.addIpWhitelist", Key, "Stage IP whitelist ekle", "POST", Whitelist)
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"ip":"203.0.113.10"}""", Note = "Yalnızca IPv4; en fazla 7 IP, günde en fazla 7 ekleme.", Doc = "test-ortam-ip-whitelist-yönetimi" },
        new("test.deleteIpWhitelist", Key, "Stage IP whitelist sil", "DELETE", $"{Whitelist}/{{ip}}") { Write = true, Doc = "test-ortam-ip-whitelist-yönetimi" },
    ]);
}
