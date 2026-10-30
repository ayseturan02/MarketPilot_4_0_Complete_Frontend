using MarketPilot.Web.Wms;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

var root = Path.Combine(Path.GetTempPath(), "marketpilot-logistics-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
void Reject(Action action, string message) { try { action(); } catch (ArgumentException) { checks++; return; } throw new Exception(message); }
var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
WmsLogisticsEvent Event(string id, string state, long seq, bool partial = false) =>
    new(id + ":" + seq, seq, id, "P1", id + "-order", "warehouse", "channel", "MOCK carrier", seq >= 4 ? "MOCK-track" : null,
        state, partial ? 1 : 2, 2, at.AddMinutes(seq), seq >= 4 ? at.AddMinutes(4) : null, state == "restocked");
string[] PathTo(string target)
{
    var queue = new Queue<string[]>(); queue.Enqueue(["received"]);
    var visited = new HashSet<string>();
    while (queue.Count > 0)
    {
        var path = queue.Dequeue(); var last = path[^1];
        if (last == target) return path;
        if (!visited.Add(last)) continue;
        foreach (var next in WmsLogisticsService.Transitions[last]) queue.Enqueue([.. path, next]);
    }
    throw new Exception("Unreachable state: " + target);
}
try
{
    var env = new TestEnvironment(root);
    var service = new WmsLogisticsService(env);
    // Every allowed and disallowed pair, including terminal states and cycles.
    foreach (var from in WmsLogisticsService.Transitions.Keys)
    foreach (var to in WmsLogisticsService.Transitions.Keys)
    {
        var id = from + "_" + to;
        var path = PathTo(from);
        var partial = path.Contains("partially-shipped") || from == "packed" && to == "partially-shipped";
        for (int i = 0; i < path.Length; i++) service.Receive(Event(id, path[i], i + 1, partial));
        var input = Event(id, to, path.Length + 1, partial);
        if (WmsLogisticsService.Transitions[from].Contains(to))
            Check(service.Receive(input).Latest.State == to, "Allowed edge failed " + id);
        else Reject(() => service.Receive(input), "Invalid edge accepted " + id);
    }
    var first = Event("duplicate", "received", 1);
    service.Receive(first); service.Receive(first);
    Check(service.List().Single(x=>x.Latest.ShipmentId=="duplicate").History.Length == 1, "Duplicate event appended");
    Reject(()=>service.Receive(first with { Quantity = 1 }), "Conflicting event accepted");
    Reject(()=>service.Receive(Event("duplicate","picking",3)), "Out-of-order event accepted");
    Reject(()=>service.Receive(Event("duplicate","picking",2) with { WarehouseId="other" }), "Identity changed");
    service.Receive(Event("guard", "received", 1)); service.Receive(Event("guard", "picking", 2)); service.Receive(Event("guard", "packed", 3));
    Reject(()=>service.Receive(Event("guard","shipped",4) with { TrackingNumber=null }),"Tracking missing");
    Reject(()=>service.Receive(Event("guard","shipped",4) with { ShippedAt=null }),"Time missing");
    Reject(()=>service.Receive(Event("guard","partially-shipped",4)),"Incorrect partial quantity");
    foreach (var (s,i) in new[] {("shipped",4),("delivered",5),("return-accepted",6),("quality-check",7),("quality-passed",8)})
        Check(service.Receive(Event("guard",s,i)).ResellableReturnQuantity==0,"Premature return stock");
    Reject(()=>service.Receive(Event("guard","restocked",9) with { WmsRestockApproved=false }), "No WMS approval");
    Check(service.Receive(Event("guard","restocked",9)).ResellableReturnQuantity==2,"Approved return unavailable");
    Check(service.List().All(x=>x.ChannelStatus==null),"Unmapped channel was inferred");
    service.SetMapping(new("channel","restocked","AwaitingERP",false));
    Check(service.List().Single(x=>x.Latest.ShipmentId=="guard").ChannelStatus==null,"Unapproved mapping used");
    service.SetMapping(new("channel","restocked","ReturnReceived",true));
    Check(service.List().Single(x=>x.Latest.ShipmentId=="guard").ChannelStatus=="ReturnReceived","Approved mapping ignored");
    foreach (var authority in new[]{"wms","carrier","marketplace"})
    {
        service.SetLabelPolicy(new("channel",authority,true));
        Check(service.List().All(x=>x.LabelAuthority==authority && x.LabelStatus=="awaiting-authority-label"),"Label authority lost or fabricated label");
    }
    Reject(()=>service.SetLabelPolicy(new("channel","unknown",true)),"Unknown label authority accepted");
    service.SetLabelPolicy(new("channel","wms",false));
    Check(service.List().All(x=>x.LabelStatus=="blocked-policy"),"Unapproved label policy used");
    // Same order split into two packages, including a second warehouse.
    var split = Event("split", "received", 1, true);
    service.Receive(split);
    service.Receive(split with {EventId="split-other", PackageId="P2",WarehouseId="other"});
    Reject(()=>service.Receive(split with {EventId="split-over",PackageId="P3"}),"Over-shipment accepted");
    var seed = service.SeedMock();
    service.SetMapping(new("trendyol","delivered","Delivered",true));
    service.SetMapping(new("amazon","restocked","ReturnPendingERP",true));
    Check(service.List().Single(x=>x.Latest.ShipmentId=="MOCK-A").ChannelStatus=="Delivered" &&
        service.List().Single(x=>x.Latest.ShipmentId=="MOCK-B").ChannelStatus=="ReturnPendingERP" &&
        service.List().Single(x=>x.Latest.ShipmentId=="MOCK-C").ChannelStatus==null,"Channel mappings leaked across channels");
    Check(service.SeedMock().Length==seed.Length,"Mock seed duplicates packages");
    Check(seed.Any(x=>x.Latest.State=="quarantined" && x.ResellableReturnQuantity==0),"Quarantine stock released");
    Check(seed.Any(x=>x.PartialShipment && x.History.Any(e=>e.State=="delivery-failed")),"Partial/failure fixture missing");
    Check(new WmsLogisticsService(env).List().Length==seed.Length,"Restart lost inbox");
    Console.WriteLine($"PASS {checks} WMS logistics checks: every state pair, replay/conflict/order, tracking, partial quantities, multi-package/depot, channel approval, label authority, return QC/WMS approval, mock scenarios and persistence.");
}
finally
{
    if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(root).StartsWith("marketpilot-logistics-")) throw new Exception("Unsafe cleanup");
    Directory.Delete(root,true);
}
sealed class TestEnvironment(string root) : IWebHostEnvironment
{
    public string EnvironmentName {get;set;}="Production";
    public string ApplicationName {get;set;}="LogisticsChecks";
    public string ContentRootPath {get;set;}=root;
    public IFileProvider ContentRootFileProvider {get;set;}=new NullFileProvider();
    public string WebRootPath {get;set;}=root;
    public IFileProvider WebRootFileProvider {get;set;}=new NullFileProvider();
}
