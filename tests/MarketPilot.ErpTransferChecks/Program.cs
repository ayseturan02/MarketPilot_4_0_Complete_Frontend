using MarketPilot.Web.Enterprise;
using MarketPilot.Web.Integrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

var folder = Path.Combine(Path.GetTempPath(), "marketpilot-erp-transfer-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected validation failure"); }
async Task RejectAsync(Func<Task> action) { try { await action(); } catch (ArgumentException) { return; } throw new Exception("Expected receipt rejection"); }
try
{
    using var services = new ServiceCollection().BuildServiceProvider();
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Workspace:BusinessId"] = "default" }).Build();
    var jobs = new EnterpriseSync(new TestEnvironment(folder), services, config);
    jobs.ConfigureErpTransfer(new("default", "seller", true, "MOCK-SALES", "fixed-account", "MOCK-CUSTOMER",
        new(), ["TRY"], [10], [new("SKU", "BC", "ERP-P", "ERP-V")], "trendyol", true));
    var sourceLines = new List<MarketplaceOrderLine> { new("L1", "SKU", "BC", "Mock clothing", 2, 100, 200, 10, "TRY", "Created", "") };
    var promotions = new List<MarketplacePromotion> { new("C1", "Mock campaign", 20, "TRY", "marketplace", "L1") };
    var order = new MarketplaceOrder("P1", "O1", "Created", DateTimeOffset.UtcNow, "Mock", "mock@example.invalid",
        180, 200, 20, "TRY", "mock", "Shipment", null, sourceLines,
        new("", "", "", "", "", null, null), new("", "", "", false, "", "", "", null, []), Promotions: promotions);
    var request = new ErpTransferRequest("default", "seller", "trendyol", "P1", "create", "create-1", order);
    var provider = new MockErpOrderTransferProvider(jobs);
    var service = new ErpOrderTransferService(jobs, provider);
    var plan = service.Prepare(request);
    Check(plan.Lines.Single().VariantCode == "ERP-V" && plan.Discount == 20 && plan.SourceOrder.Promotions!.Single().FundedBy == "marketplace", "Mapping or campaign lost");
    sourceLines[0] = sourceLines[0] with { UnitPrice = 999 };
    promotions.Clear();
    Check(plan.SourceOrder.Lines.Single().UnitPrice == 100 && plan.SourceOrder.Promotions!.Count == 1, "Caller mutation changed frozen preview");
    var accepted = await service.ExecuteAsync(plan, false, default);
    Check(accepted.State == "accepted" && accepted.Reconciled && accepted.ConfirmedAt != null, "Confirmed mock receipt not accepted");
    Check((await service.ExecuteAsync(plan, false, default)).AcceptanceReference == accepted.AcceptanceReference, "Duplicate submission created a new acceptance");
    var unchangedOrder = plan.SourceOrder;
    Reject(()=>service.Prepare(request with { Order = unchangedOrder with { Currency = "USD" } }));
    Reject(()=>service.Prepare(request with { Order = unchangedOrder with { Lines = [null!] } }));
    Reject(()=>service.Prepare(request with { Order = unchangedOrder with { Lines = [unchangedOrder.Lines[0] with { UnitPrice = decimal.MaxValue }] } }));
    Reject(()=>service.Prepare(request with { Order = unchangedOrder with { Promotions = [new("bad", "invalid reference", 20, "TRY", LineId:"missing")] } }));
    Reject(()=>service.Prepare(request with { Order = unchangedOrder, Operation = "cancel" }));
    foreach (var operation in new[] { "correct", "cancel", "return" })
        Check(service.Prepare(request with { Order = unchangedOrder, Operation = operation, OperationId = operation, OriginalAcceptanceReference = accepted.AcceptanceReference }).Operation == operation, "Operation was not separated");
    var mismatchOrder = unchangedOrder with { PackageId = "mismatch", OrderNumber = "mismatch" };
    var mismatchRequest = request with { SourceOrderId = "mismatch", Order = mismatchOrder, MockScenario = "mismatch" };
    var mismatchOutcome = await service.ExecuteAsync(service.Prepare(mismatchRequest), false, default);
    Check(mismatchOutcome.State == "reconciliation-required", "ERP amount difference ignored");
    Reject(()=>service.Prepare(mismatchRequest with { Operation = "cancel", OriginalAcceptanceReference = mismatchOutcome.AcceptanceReference }));

    MockErpTransferReceipt Receipt() => new(plan.RequestKey, "accepted", "MOCK-REF", 180, "TRY", null,
        Fingerprint: IntegrationOrchestrator.Hash(plan with { MockScenario = "success" }), SourceIdentity: plan.SourceIdentity, Operation: "create", ConfirmedAt: DateTimeOffset.UtcNow);
    var fake = new ReceiptProvider(Receipt());
    var guarded = new ErpOrderTransferService(jobs, fake);
    fake.Receipt = Receipt() with { ConfirmedAt = null };
    Check((await guarded.ExecuteAsync(plan, false, default)).State == "reconciliation-required", "Unconfirmed response was treated as saved");
    fake.Receipt = Receipt() with { AcceptanceReference = null };
    Check(!(await guarded.ExecuteAsync(plan, false, default)).Reconciled, "Missing acceptance reference accepted");
    fake.Receipt = Receipt() with { Currency = "USD" };
    Check(!(await guarded.ExecuteAsync(plan, false, default)).Reconciled, "Wrong currency reconciled");
    fake.Receipt = Receipt() with { SourceIdentity = "another-order" };
    await RejectAsync(async()=>await guarded.ExecuteAsync(plan, false, default));
    fake.Receipt = Receipt() with { Operation = "return" };
    await RejectAsync(async()=>await guarded.ExecuteAsync(plan, false, default));
    fake.Receipt = null;
    Check((await guarded.ExecuteAsync(plan, true, default)).State == "uncertain" && fake.Submissions == 0, "Unknown outcome was blindly resubmitted");
    fake.Receipt = Receipt() with { SourceIdentity = "another-order" };
    fake.QueryReturnsNull = true;
    await RejectAsync(async()=>await guarded.ExecuteAsync(plan, false, default));
    Console.WriteLine("PASS ERP transfer preparation: identity/mappings, frozen campaign data, amount limits, null lines, tax/currency, separate operations, duplicate receipts, missing confirmation/reference, wrong-order/operation responses and query-only unknown outcomes. Mock only.");
}
finally
{
    if(Path.GetDirectoryName(Path.GetFullPath(folder)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(folder).StartsWith("marketpilot-erp-transfer-")) throw new Exception("Unsafe cleanup");
    Directory.Delete(folder,true);
}
sealed class ReceiptProvider(MockErpTransferReceipt? receipt) : IErpOrderTransferProvider
{
    public bool IsMock => true;
    public MockErpTransferReceipt? Receipt = receipt;
    public bool QueryReturnsNull;
    public int Submissions;
    public Task<MockErpTransferReceipt?> QueryBySourceIdentityAsync(string key, CancellationToken ct) => Task.FromResult(QueryReturnsNull ? null : Receipt);
    public Task<MockErpTransferReceipt> SubmitAsync(ErpTransferPlan plan, CancellationToken ct) { Submissions++; return Task.FromResult(Receipt!); }
}
sealed class TestEnvironment(string folder) : IWebHostEnvironment
{
    public string EnvironmentName {get;set;} = "Production";
    public string ApplicationName {get;set;} = "ErpTransferChecks";
    public string ContentRootPath {get;set;} = folder;
    public string WebRootPath {get;set;} = folder;
    public IFileProvider ContentRootFileProvider {get;set;} = new NullFileProvider();
    public IFileProvider WebRootFileProvider {get;set;} = new NullFileProvider();
}
