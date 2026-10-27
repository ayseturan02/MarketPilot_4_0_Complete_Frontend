using MarketPilot.Web.Integrations;
using MarketPilot.Web.Integrations.Demo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; } throw new Exception("Invalid action was accepted"); }
var folder = Path.Combine(Path.GetTempPath(), "marketpilot-demo-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
try
{
    var env = new TestEnvironment { ContentRootPath = folder };
    var registry = new IntegrationRegistry(env);
    registry.Connections["saved"] = new("saved", new("Saved demo", "trendyol", "demo", "stage"), "demo", null, null, null, "", null); registry.Save();
    Check(new IntegrationRegistry(env).Connections.ContainsKey("saved"), "Store persistence failed");
    var demo = new DemoWorkspace(env); var settings = new ConnectionSettings("Main", "trendyol", "demo", "stage");
    var initial = demo.Snapshot(settings); Check(initial.Products.Count == 8 && initial.Orders.Count == 6 && initial.Claims.Count == 2 && initial.Questions.Count == 3, "Seed coverage failed");
    var barcode = initial.Products[0].Barcode; var orderId = initial.Orders[0].PackageId;
    var pending = demo.Enqueue(settings, new("stock", "stock-1", barcode, 41, 499m));
    Check(pending.State == "pending" && demo.Snapshot(settings).Products[0].Stock == 36, "Pending job changed inventory early");
    Check(demo.Enqueue(settings, new("stock", "stock-1", barcode, 99, 999m)).Id == pending.Id, "Duplicate command was not idempotent");
    demo.Enqueue(settings, new("order-next", "order-1", Reference: orderId));
    Reject(() => demo.Enqueue(settings, new("order-next", "order-duplicate", Reference: orderId)));
    Reject(() => demo.Enqueue(settings, new("stock", "invalid-stock", barcode, -1, 100m)));
    Reject(() => demo.Enqueue(settings, new("answer", "short-answer", Reference: "demo-question-1", Text: "short")));
    Reject(() => demo.Snapshot(settings with { Mode = "real" }));
    await Task.Delay(2200);
    var done = demo.Snapshot(settings); Check(done.Products[0].Stock == 41 && done.Products[0].SalePrice == 499m && done.Orders[0].Status == "Picking", "Job application failed");
    var restored = new DemoWorkspace(env); Check(restored.Snapshot(settings).Products[0].Stock == 41, "Inventory did not survive service restart");
    Check(restored.Snapshot(settings with { Name = "Outlet" }).Products[0].Stock == 12, "Store isolation failed");
    demo.Enqueue(settings, new("answer", "answer-1", Reference: "demo-question-1", Text: "Merhaba, ürünümüz yüzde yüz pamuktur."));
    demo.Enqueue(settings, new("claim-approve", "claim-1", Reference: "DEMO-IADE-01"));
    var failed = done.Jobs.First(j => j.State == "failed"); demo.Enqueue(settings, new("retry", "retry-1", Reference: failed.Id));
    await Task.Delay(2200); var finished = demo.Snapshot(settings);
    Check(finished.Questions[0].Status == "ANSWERED", "Answer was not saved");
    Check(finished.Claims[0].Status == "Onaylandı" && finished.Products[5].Stock == 24, "Return stock update failed");
    Check(finished.Jobs.Count(j => j.State == "completed") >= 6 && finished.Jobs.Any(j => j.Id == failed.Id && j.State == "retried"), "Retry lifecycle failed");
    Console.WriteLine("PASS: persistent stores/demo inventory, isolated outlets, pending/completed/retry states, duplicate protection, stock validation, live-store rejection, order transitions, answers and return stock.");
}
finally
{
    var absolute = Path.GetFullPath(folder); var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!absolute.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("marketpilot-demo-check-")) throw new Exception("Unsafe test cleanup path");
    Directory.Delete(absolute, true);
}

sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "MarketPilot.Web";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
