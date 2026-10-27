namespace MarketPilot.Web.Integrations.Trendyol;

// Müşteri soruları: filtreleme, tekil soru ve cevaplama.
public static class TrendyolQuestionsPanel
{
    public const string Key = "questions";
    private const string Seller = "qna/sellers/{sellerId}";

    public static readonly TrendyolPanel Panel = new(Key, "Müşteri Soruları", "Müşteri sorularını filtreleme, tekil soru detayı ve cevap gönderme.",
    [
        new("questions.filter", Key, "Soruları filtrele", "GET", $"{Seller}/questions/filter")
            { Query = ["barcode", "page", "size", "startDate", "endDate", "status", "orderByField", "orderByDirection"], Note = "status: WAITING_FOR_ANSWER, ANSWERED, REPORTED, REJECTED, UNANSWERED.", Doc = "müşteri-sorularını-çekme" },
        new("questions.get", Key, "Soru detayı", "GET", $"{Seller}/questions/{{id}}") { Doc = "müşteri-sorularını-çekme" },
        new("questions.answer", Key, "Soruyu cevapla", "POST", $"{Seller}/questions/{{id}}/answers")
            { Write = true, Body = TrendyolBodyKind.Json, SampleBody = """{"text":""}""", Doc = "müşteri-sorularını-cevaplama" },
    ]);
}
