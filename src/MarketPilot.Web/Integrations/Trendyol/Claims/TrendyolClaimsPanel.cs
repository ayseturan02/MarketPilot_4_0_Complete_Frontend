namespace MarketPilot.Web.Integrations.Trendyol;

// İade süreçleri: iadeleri çekme, iade yaratma, onay, ret talebi, red sebepleri ve audit.
public static class TrendyolClaimsPanel
{
    public const string Key = "claims";
    private const string Seller = "order/sellers/{sellerId}";

    public static readonly TrendyolPanel Panel = new(Key, "İadeler", "İadesi oluşturulan siparişler, iade onayı, ret talebi, red sebepleri ve iade audit bilgileri.",
    [
        new("claims.list", Key, "İade siparişlerini çek", "GET", $"{Seller}/claims")
            { Query = ["claimIds", "claimItemStatus", "startDate", "endDate", "orderNumber", "page", "size"], Doc = "i̇adesi-oluşturulan-siparişleri-çekme-getclaims" },
        new("claims.issueReasons", Key, "İade red sebepleri", "GET", "order/claim-issue-reasons") { Doc = "i̇ade-red-sebeplerini-çekme-getclaimsissuereasons" },
        new("claims.audit", Key, "İade audit bilgisi", "GET", $"{Seller}/claims/items/{{claimItemsId}}/audit") { Doc = "i̇ade-audit-bilgilerini-çekme" },
        new("claims.create", Key, "İade talebi oluştur", "POST", $"{Seller}/claims/create")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"claimItems":[{"barcode":"","customerNote":"","quantity":1,"reasonId":401}],"customerId":0,"excludeListing":true,"forcePackageCreation":true,"orderNumber":"","shipmentCompanyId":4}""", Doc = "i̇ade-talebi-oluşturma-createclaim" },
        new("claims.approve", Key, "İade kalemlerini onayla", "PUT", $"{Seller}/claims/{{claimId}}/items/approve")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"claimLineItemIdList":[""],"params":{}}""", Note = "Sadece WaitingInAction statüsündeki iadeler onaylanabilir.", Doc = "i̇ade-siparişleri-onaylama-approveclaimlineitems" },
        new("claims.createIssue", Key, "İade ret talebi oluştur", "POST", $"{Seller}/claims/{{claimId}}/issue")
            { Write = true, Body = TrendyolBodyKind.Multipart, Query = ["claimIssueReasonId", "claimItemIdList", "description"], FileFields = ["files"], Note = "1651, 451 ve 2101 sebepleri dışında dosya eki zorunludur.", Doc = "i̇ade-siparişlerinde-ret-talebi-oluşturma-createclaimissue" },
    ]);
}
