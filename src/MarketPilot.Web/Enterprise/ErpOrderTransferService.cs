using System.Text.Json;
using MarketPilot.Web.Integrations;

namespace MarketPilot.Web.Enterprise;

public sealed record ErpVariantLink(string Sku, string Barcode, string ErpProductCode, string ErpVariantCode);
public sealed record ErpTransferPolicy(string TenantId, string AccountId, bool Approved, string SalesChannelCode,
    string CustomerPolicy, string? FixedCustomerCode, Dictionary<string,string> Customers,
    string[] Currencies, decimal[] VatRates, ErpVariantLink[] Variants, string Channel="", bool? UnitPricesIncludeVat=null);
public sealed record ErpTransferLine(string SourceLineId, string ProductCode, string VariantCode,
    int Quantity, decimal UnitPrice, decimal VatRate, string Currency);
public sealed record ErpTransferRequest(string TenantId, string AccountId, string Channel, string SourceOrderId,
    string Operation, string OperationId, MarketplaceOrder Order, string? CustomerIdentity = null,
    string? OriginalAcceptanceReference = null, string MockScenario = "success",CentralOrderProjection? CentralOrder=null,
    string? RejectedRequestKey=null);
public sealed record ErpTransferPlan(string RequestKey, string SourceIdentity, string Operation, string? OriginalAcceptanceReference,
    string SalesChannelCode, string CustomerCode, string Currency, ErpTransferLine[] Lines,
    decimal GrossTotal, decimal Discount, decimal MarketplaceTotal, MarketplaceOrder SourceOrder, string MockScenario);
public sealed record MockErpTransferReceipt(string RequestKey, string State, string? AcceptanceReference,
    decimal? ErpTotal, string Currency, string? Error, int Submissions = 1, int Queries = 0,
    string Fingerprint = "", string SourceIdentity = "", string Operation = "create",DateTimeOffset? ConfirmedAt=null,
    decimal? MarketplaceTotal = null);
public sealed record ErpTransferOutcome(string State, string? AcceptanceReference, decimal? ErpTotal,
    decimal MarketplaceTotal, decimal? Difference, string Currency, bool Reconciled, string? Error,DateTimeOffset? ConfirmedAt=null);

// Internal preparation contract only. Deliberately has no HttpClient, URL or Nebim payload serializer.
public interface IErpOrderTransferProvider
{
    bool IsMock { get; }
    Task<MockErpTransferReceipt> SubmitAsync(ErpTransferPlan plan, CancellationToken ct);
    Task<MockErpTransferReceipt?> QueryBySourceIdentityAsync(string requestKey, CancellationToken ct);
}
public sealed class MockErpOrderTransferProvider(EnterpriseSync jobs) : IErpOrderTransferProvider
{
    public bool IsMock => true;
    public Task<MockErpTransferReceipt> SubmitAsync(ErpTransferPlan plan, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (plan.MockScenario == "unavailable") throw new IOException("Mock ERP unavailable");
        var receipt = jobs.MockErpSubmit(plan);
        if (plan.MockScenario == "timeout") throw new TimeoutException("Mock response lost after commit");
        return Task.FromResult(receipt);
    }
    public Task<MockErpTransferReceipt?> QueryBySourceIdentityAsync(string key, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(jobs.MockErpQuery(key)); }
}
public sealed class ErpOrderTransferService(EnterpriseSync jobs, IErpOrderTransferProvider provider)
{
    private static readonly HashSet<string> CurrencyCodes = System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.SpecificCultures)
        .Select(culture => { try { return new System.Globalization.RegionInfo(culture.Name).ISOCurrencySymbol; } catch (ArgumentException) { return ""; } })
        .Where(code => code.Length == 3).ToHashSet(StringComparer.Ordinal);
    internal static bool ValidCurrency(string? code) => code is not null && CurrencyCodes.Contains(code);
    public ErpTransferPlan Prepare(ErpTransferRequest input)
    {
        if (input is null || string.IsNullOrWhiteSpace(input.TenantId) || string.IsNullOrWhiteSpace(input.AccountId) ||
            string.IsNullOrWhiteSpace(input.Channel) || string.IsNullOrWhiteSpace(input.SourceOrderId) || string.IsNullOrWhiteSpace(input.OperationId) ||
            input.Operation is not ("create" or "correct" or "cancel" or "return") ||
            input.MockScenario is not ("success" or "rejected" or "pending" or "timeout" or "unavailable" or "mismatch"))
            throw new ArgumentException("ERP kaynak kimliği veya ayrı işlem türü geçersiz.");
        var policy = jobs.GetErpTransferPolicy(input.TenantId, input.AccountId);
        if (policy == null || !policy.Approved || string.IsNullOrWhiteSpace(policy.SalesChannelCode))
            throw new ArgumentException("Onaylı ERP satış kanalı/müşteri eşleme politikası gerekli.");
        if(policy.Channel!=input.Channel || policy.UnitPricesIncludeVat!=true)
            throw new ArgumentException("Kanal veya KDV dahil fiyat esası doğrulanmadı; vergi tahmini yapılmaz.");
        var order = input.Order ?? throw new ArgumentException("Sipariş gerekli.");
        if (order.Lines is null || order.Lines.Count is < 1 or > 1000 || order.Lines.Any(line => line is null) ||
            order.Total is < 0 or > 10000000000000000m || order.GrossTotal is < 0 or > 10000000000000000m ||
            order.Discount is < 0 or > 10000000000000000m ||
            order.Lines.Any(line => line.Quantity is < 1 or > 1000000 || line.UnitPrice is < 0 or > 10000000))
            throw new ArgumentException("Sipariş kalemleri veya tutar sınırları geçersiz.");
        // Freeze the source data; a caller-owned list must not alter an approved preview.
        order = JsonSerializer.Deserialize<MarketplaceOrder>(JsonSerializer.Serialize(order))!;
        if(order.Promotions?.Any(p=>p==null||string.IsNullOrWhiteSpace(p.Id)||p.Amount<0||p.Currency!=order.Currency ||
            p.LineId is not null && !order.Lines.Any(line => line.Id == p.LineId))==true)
            throw new ArgumentException("Kampanya tutarı veya para birimi geçersiz.");
        var central=ChannelContracts.For(input.Channel).Order(input.TenantId,input.AccountId,order,policy.Variants);
        if(central.CanonicalStatus==null || input.Operation=="create" && central.CanonicalStatus is not ("received" or "picking" or "packing"))
            throw new ArgumentException("Sipariş durumu yeni ERP kaydı için uygun/doğrulanmış değil.");
        if (input.SourceOrderId != (string.IsNullOrWhiteSpace(order.PackageId) ? order.OrderNumber : order.PackageId))
            throw new ArgumentException("Kaynak sipariş/paket kimliği uyuşmuyor.");
        var sourceIdentity = IntegrationOrchestrator.Hash(new { input.TenantId,input.AccountId,input.Channel,input.SourceOrderId });
        var correctingRejection=input.Operation=="correct" && input.RejectedRequestKey!=null && jobs.HasErpRejection(sourceIdentity,input.RejectedRequestKey);
        if (input.Operation != "create" && !correctingRejection && (string.IsNullOrWhiteSpace(input.OriginalAcceptanceReference) ||
            !jobs.HasErpAcceptance(sourceIdentity, input.OriginalAcceptanceReference)))
            throw new ArgumentException("Düzeltme, iptal ve iade için aynı siparişin doğrulanmış ERP kabul referansı gerekli.");
        var customer = policy.CustomerPolicy switch
        {
            "fixed-account" => policy.FixedCustomerCode,
            "existing-customer" => input.CustomerIdentity == null ? null : policy.Customers.GetValueOrDefault(input.CustomerIdentity),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(customer)) throw new ArgumentException("Cari/müşteri eşlemesi belirsiz; kullanıcı müdahalesi gerekli.");
        if (!ValidCurrency(order.Currency) || !policy.Currencies.Contains(order.Currency) || order.Total == null || order.GrossTotal == null || order.Discount == null ||
            order.Total < 0 || order.GrossTotal < 0 || order.Discount < 0 || order.Discount > order.GrossTotal ||
            order.GrossTotal - order.Discount != order.Total || order.Lines == null || order.Lines.Count == 0 ||
            order.Lines.Select(x=>x.Id).Distinct().Count()!=order.Lines.Count)
            throw new ArgumentException("Para birimi, indirim veya brüt/net tutar doğrulanamadı; tahmin yapılmaz.");
        var lines = order.Lines.Select(line =>
        {
            var links = policy.Variants.Where(x => x.Sku == line.Sku && x.Barcode == line.Barcode).ToArray();
            if (links.Length != 1 || string.IsNullOrWhiteSpace(links[0].ErpProductCode) || string.IsNullOrWhiteSpace(links[0].ErpVariantCode) ||
                string.IsNullOrWhiteSpace(line.Id) || string.IsNullOrWhiteSpace(line.Sku) || string.IsNullOrWhiteSpace(line.Barcode) ||
                line.Quantity <= 0 || line.UnitPrice == null || line.UnitPrice < 0 || line.VatRate == null ||
                !policy.VatRates.Contains(line.VatRate.Value) || line.Currency != order.Currency ||
                line.GrossAmount == null || line.UnitPrice * line.Quantity != line.GrossAmount)
                throw new ArgumentException("ERP ürün/varyant, miktar, KDV veya kalem tutarı eşleşmedi.");
            return new ErpTransferLine(line.Id, links[0].ErpProductCode, links[0].ErpVariantCode,
                line.Quantity, line.UnitPrice.Value, line.VatRate.Value, line.Currency);
        }).ToArray();
        if (order.Lines.Sum(x=>x.GrossAmount!.Value) != order.GrossTotal)
            throw new ArgumentException("Kalem toplamı sipariş brüt tutarıyla mutabık değil.");
        var key = IntegrationOrchestrator.Hash(new { sourceIdentity, input.Operation,
            operationId = input.Operation == "create" ? "create" : input.OperationId });
        return new(key,sourceIdentity,input.Operation,correctingRejection?"rejected:"+input.RejectedRequestKey:input.OriginalAcceptanceReference,policy.SalesChannelCode,
            customer,order.Currency,lines,order.GrossTotal.Value,order.Discount.Value,order.Total.Value,order,input.MockScenario);
    }
    public async Task<ErpTransferOutcome> ExecuteAsync(ErpTransferPlan plan, bool queryOnly, CancellationToken ct)
    {
        if (!provider.IsMock) throw new ArgumentException("Doğrulanmış Nebim yazma sözleşmesi yok; gerçek aktarım bloke.");
        var receipt = await provider.QueryBySourceIdentityAsync(plan.RequestKey, ct);
        if(receipt!=null && receipt.Fingerprint!=IntegrationOrchestrator.Hash(plan with{MockScenario="success"}))
            throw new IntegrationException("erp_duplicate_conflict","ERP kaynak kimliği farklı içerikle kullanılmış; kullanıcı müdahalesi gerekli.",409);
        if(receipt==null && !queryOnly)receipt=await provider.SubmitAsync(plan,ct);
        if (receipt == null) return new("uncertain",null,null,plan.MarketplaceTotal,null,plan.Currency,false,"ERP kaydı doğrulanamadı; kör tekrar yapılmaz.");
        if (receipt.RequestKey != plan.RequestKey || receipt.SourceIdentity != plan.SourceIdentity || receipt.Operation != plan.Operation ||
            receipt.Fingerprint != IntegrationOrchestrator.Hash(plan with { MockScenario = "success" }))
            throw new ArgumentException("ERP kabul kimliği, kaynak sipariş veya işlem içeriği uyuşmuyor.");
        var difference = receipt.ErpTotal - plan.MarketplaceTotal;
        var reconciled = receipt.State == "accepted" && !string.IsNullOrWhiteSpace(receipt.AcceptanceReference) &&
            receipt.Currency == plan.Currency && difference == 0 && receipt.ConfirmedAt is not null && receipt.ConfirmedAt != default(DateTimeOffset);
        var state = receipt.State switch { "rejected"=>"intervention-required", "accepted" when reconciled=>"accepted",
            "accepted"=>"reconciliation-required", "pending"=>"pending", _=>"uncertain" };
        return new(state,receipt.AcceptanceReference,receipt.ErpTotal,plan.MarketplaceTotal,difference,receipt.Currency,reconciled,receipt.Error,receipt.ConfirmedAt);
    }
}
