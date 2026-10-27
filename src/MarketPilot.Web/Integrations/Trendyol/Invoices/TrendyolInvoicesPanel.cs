namespace MarketPilot.Web.Integrations.Trendyol;

// Fatura linki gönderme/silme, fatura dosyası ve garanti belgesi yükleme.
public static class TrendyolInvoicesPanel
{
    public const string Key = "invoices";
    private const string Seller = "sellers/{sellerId}";

    public static readonly TrendyolPanel Panel = new(Key, "Fatura & Garanti", "Fatura linki gönderme/silme, fatura dosyası yükleme ve garanti belgesi gönderimi.",
    [
        new("invoices.sendLink", Key, "Fatura linki gönder", "POST", $"{Seller}/seller-invoice-links")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"invoiceLink":"https://","shipmentPackageId":0,"invoiceDateTime":0,"invoiceNumber":""}""", Doc = "fatura-linki-gönderme-sendinvoicelink" },
        new("invoices.deleteLink", Key, "Fatura linki sil", "POST", $"{Seller}/seller-invoice-links/delete")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"serviceSourceId":0,"channelId":1,"customerId":0}""", Note = "serviceSourceId = shipmentPackageId; channelId her zaman 1.", Doc = "fatura-linki-silme" },
        new("invoices.sendFile", Key, "Fatura dosyası yükle", "POST", $"{Seller}/seller-invoice-file")
            { Write = true, Body = TrendyolBodyKind.Multipart, FormFields = ["shipmentPackageId", "invoiceDateTime", "invoiceNumber"], FileFields = ["file"], Note = "PDF/JPEG/PNG, en fazla 10 MB. invoiceDateTime ve invoiceNumber yalnızca mikro ihracatta zorunlu.", Doc = "fatura-dosyası-gönderme" },
        new("invoices.warrantyDocument", Key, "Garanti belgesi gönder", "POST", $"{Seller}/warranty-documents")
            { Write = true, Body = TrendyolBodyKind.Multipart, FormFields = ["shipmentPackageId", "lineId"], FileFields = ["file"], Note = "Yalnızca PDF.", Doc = "garanti-belgesi-gönderimi" },
    ]);
}
