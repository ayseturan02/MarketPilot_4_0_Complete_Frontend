namespace MarketPilot.Web.Integrations.Trendyol;

// Cari hesap ekstresi (settlements, otherfinancials, payment-order) ve kargo faturası detayları.
public static class TrendyolFinancePanel
{
    public const string Key = "finance";
    private const string Seller = "finance/che/sellers/{sellerId}";

    public static readonly TrendyolPanel Panel = new(Key, "Finans", "Cari hesap ekstresi, diğer finansal hareketler, ödeme talimatları ve kargo faturası kalemleri.",
    [
        new("finance.settlements", Key, "Satış/iade/komisyon hareketleri (settlements)", "GET", $"{Seller}/settlements")
            { Query = ["transactionType", "startDate", "endDate", "paymentDate", "paymentOrderId", "page", "size"], Note = "transactionType zorunlu (Sale, Return, Discount, Coupon, CommissionPositive …). Tarih aralığı en fazla 15 gün; size 500 veya 1000.", Doc = "cari-hesap-ekstresi-entegrasyonu" },
        new("finance.otherFinancials", Key, "Diğer finansal hareketler", "GET", $"{Seller}/otherfinancials")
            { Query = ["transactionType", "transactionTypes", "transactionSubType", "startDate", "endDate", "paymentDate", "paymentOrderId", "page", "size"], Note = "transactionType: CashAdvance, WireTransfer, IncomingTransfer, ReturnInvoice, CommissionAgreementInvoice, PaymentOrder, DeductionInvoices, FinancialItem, Stoppage.", Doc = "cari-hesap-ekstresi-entegrasyonu" },
        new("finance.paymentOrders", Key, "Ödeme talimatları (payment-order)", "GET", $"{Seller}/payment-order")
            { Query = ["startDate", "endDate", "page", "size"], Doc = "cari-hesap-ekstresi-entegrasyonu" },
        new("finance.cargoInvoiceItems", Key, "Kargo faturası kalemleri", "GET", $"{Seller}/cargo-invoice/{{invoiceSerialNumber}}/items")
            { Query = ["page", "size"], Note = "invoiceSerialNumber, DeductionInvoices hareketlerindeki fatura seri numarasıdır.", Doc = "kargo-faturası-detayları" },
    ]);
}
