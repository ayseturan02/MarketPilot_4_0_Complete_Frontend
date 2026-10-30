using System.Text.Json;

namespace MarketPilot.Web.Wms;

public sealed record WmsLogisticsEvent(string EventId, long Sequence, string ShipmentId, string PackageId,
    string OrderId, string WarehouseId, string Channel, string Carrier, string? TrackingNumber,
    string State, decimal Quantity, decimal OrderQuantity, DateTimeOffset At,
    DateTimeOffset? ShippedAt = null, bool WmsRestockApproved = false, string ExternalAccountId = "mock-default");
public sealed record WmsChannelMapping(string Channel, string WmsState, string ChannelState, bool Approved);
public sealed record WmsLabelPolicy(string Channel, string Authority, bool Approved);
public sealed record WmsLogisticsPackage(string Key, WmsLogisticsEvent Latest, string PickingStatus,
    string PackingStatus, bool PartialShipment, decimal ResellableReturnQuantity,
    string? ChannelStatus, string MappingStatus, string LabelAuthority,
    string LabelStatus, WmsLogisticsEvent[] History);
public sealed class WmsLogisticsData
{
    public List<WmsLogisticsEvent> Events { get; set; } = [];
    public List<WmsChannelMapping> Mappings { get; set; } = [];
    public List<WmsLabelPolicy> LabelPolicies { get; set; } = [];
}

// This inbox is mock-only. No marketplace writes, label generation or inventory mutation.
public sealed class WmsLogisticsService
{
    public static readonly IReadOnlyDictionary<string, string[]> Transitions = new Dictionary<string, string[]>
    {
        ["received"] = ["picking"], ["picking"] = ["packed"],
        ["packed"] = ["shipped", "partially-shipped"],
        ["shipped"] = ["delivered", "delivery-failed"],
        ["partially-shipped"] = ["delivered", "delivery-failed"],
        ["delivery-failed"] = ["shipped", "return-accepted"],
        ["delivered"] = ["return-accepted"],
        ["return-accepted"] = ["quality-check"],
        ["quality-check"] = ["quality-passed", "quarantined"],
        ["quality-passed"] = ["restocked"], ["quarantined"] = [], ["restocked"] = []
    };
    private readonly object gate = new();
    private readonly string file;
    private WmsLogisticsData data;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public WmsLogisticsService(IWebHostEnvironment environment)
    {
        file = Path.Combine(environment.ContentRootPath, "Data", "wms-logistics.json");
        data = File.Exists(file) ? JsonSerializer.Deserialize<WmsLogisticsData>(File.ReadAllText(file), Json)
            ?? throw new InvalidOperationException("WMS logistics state unreadable") : new();
    }
    private T Commit<T>(Func<T> action)
    {
        lock (gate)
        {
            var before = JsonSerializer.Serialize(data, Json);
            try
            {
                var result = action();
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var temporary = file + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(data, Json));
                File.Move(temporary, file, true);
                return result;
            }
            catch { data = JsonSerializer.Deserialize<WmsLogisticsData>(before, Json)!; throw; }
        }
    }
    public WmsChannelMapping SetMapping(WmsChannelMapping mapping) => Commit(() =>
    {
        if (string.IsNullOrWhiteSpace(mapping.Channel) || !Transitions.ContainsKey(mapping.WmsState) ||
            string.IsNullOrWhiteSpace(mapping.ChannelState) || mapping.ChannelState.Length > 100)
            throw new ArgumentException("Kanal durum eşlemesi geçersiz.");
        data.Mappings.RemoveAll(x => x.Channel == mapping.Channel && x.WmsState == mapping.WmsState);
        data.Mappings.Add(mapping); return mapping;
    });
    public WmsLabelPolicy SetLabelPolicy(WmsLabelPolicy policy) => Commit(() =>
    {
        if (string.IsNullOrWhiteSpace(policy.Channel) || policy.Authority is not ("wms" or "carrier" or "marketplace"))
            throw new ArgumentException("Etiket yetkisi WMS, taşıyıcı veya pazaryeri olmalıdır.");
        data.LabelPolicies.RemoveAll(x => x.Channel == policy.Channel);
        data.LabelPolicies.Add(policy); return policy;
    });
    public object Policies() { lock (gate) return new { mappings = data.Mappings.ToArray(), labelPolicies = data.LabelPolicies.ToArray(), transitions = Transitions, mockOnly = true }; }
    public WmsLogisticsPackage[] List() { lock (gate) return data.Events.GroupBy(Key).Select(g => Project(g.ToArray())).ToArray(); }
    private static string Key(WmsLogisticsEvent e) => JsonSerializer.Serialize(new[] { e.Channel, e.ExternalAccountId, e.ShipmentId, e.PackageId });
    private WmsLogisticsPackage Project(WmsLogisticsEvent[] history)
    {
        var e = history[^1];
        var mapping = data.Mappings.SingleOrDefault(x => x.Channel == e.Channel && x.WmsState == e.State && x.Approved);
        var label = data.LabelPolicies.SingleOrDefault(x => x.Channel == e.Channel && x.Approved);
        return new(Key(e), e,
            history.Any(x => x.State == "packed") ? "completed" : e.State == "picking" ? "picking" : "pending",
            history.Any(x => x.State == "packed") ? "completed" : "pending",
            history.Any(x => x.State == "partially-shipped"),
            e.State == "restocked" && e.WmsRestockApproved ? e.Quantity : 0,
            mapping?.ChannelState, mapping == null ? "blocked-unmapped" : "mapped-mock-only",
            label?.Authority ?? "unknown", label == null ? "blocked-policy" : "awaiting-authority-label", history);
    }
    public WmsLogisticsPackage Receive(WmsLogisticsEvent e) => Commit(() => ReceiveCore(e));
    private WmsLogisticsPackage ReceiveCore(WmsLogisticsEvent e)
    {
        if (e is null || new[] { e.EventId, e.ShipmentId, e.PackageId, e.OrderId, e.WarehouseId, e.Channel, e.Carrier, e.ExternalAccountId }
                .Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 160) ||
            e.TrackingNumber?.Length > 200 || e.Quantity <= 0 || e.Quantity > 1000000 || e.OrderQuantity > 1000000 ||
            e.OrderQuantity < e.Quantity || e.At == default || e.State is null || !Transitions.ContainsKey(e.State) ||
            e.WmsRestockApproved && e.State != "restocked" ||
            e.ShippedAt is not null && (e.State is "received" or "picking" or "packed" || e.ShippedAt > e.At))
            throw new ArgumentException("WMS olay kimliği, miktarı veya durumu geçersiz.");
        var duplicate = data.Events.SingleOrDefault(x => x.EventId == e.EventId && x.Channel == e.Channel && x.ExternalAccountId == e.ExternalAccountId);
        if (duplicate != null && duplicate != e) throw new ArgumentException("Event ID farklı içerikle kullanılmış.");
        var history = data.Events.Where(x => Key(x) == Key(e)).ToArray();
        if (duplicate != null) return Project(history);
        var last = history.LastOrDefault();
        if (last == null ? e.State != "received" || e.Sequence != 1 :
            e.Sequence != last.Sequence + 1 || e.At < last.At || !Transitions[last.State].Contains(e.State) ||
            e.OrderId != last.OrderId || e.WarehouseId != last.WarehouseId || e.Channel != last.Channel ||
            e.Quantity != last.Quantity || e.OrderQuantity != last.OrderQuantity || e.Carrier != last.Carrier ||
            last.TrackingNumber != null && e.TrackingNumber != last.TrackingNumber ||
            last.ShippedAt != null && e.ShippedAt != last.ShippedAt)
            throw new ArgumentException("Sırasız olay, değişen paket kimliği veya geçersiz durum geçişi; inceleme gerekli.");
        var shipped = e.State is "shipped" or "partially-shipped";
        if (shipped && (string.IsNullOrWhiteSpace(e.TrackingNumber) || e.ShippedAt == null || e.ShippedAt > e.At ||
            last?.ShippedAt is null && e.ShippedAt < last?.At ||
            (e.State == "partially-shipped") != (e.Quantity < e.OrderQuantity)))
            throw new ArgumentException("Sevk için takip numarası, sevk zamanı ve doğru kısmi sevk durumu gerekli.");
        if (e.State == "restocked" && (!e.WmsRestockApproved || last?.State != "quality-passed"))
            throw new ArgumentException("Satılabilir iade için başarılı kalite kontrolü ve WMS onayı gerekli.");
        var orderEvents = data.Events.Where(x => x.OrderId == e.OrderId && x.Channel == e.Channel && x.ExternalAccountId == e.ExternalAccountId).ToArray();
        if (orderEvents.Any(x => x.OrderQuantity != e.OrderQuantity))
            throw new ArgumentException("Sipariş miktarı paketler arasında değiştirilemez.");
        if (last == null && orderEvents.GroupBy(Key).Sum(g => g.First().Quantity) + e.Quantity > e.OrderQuantity)
            throw new ArgumentException("Paket toplamı sipariş miktarını aşıyor.");
        data.Events.Add(e); return Project([.. history, e]);
    }
    public WmsLogisticsPackage[] SeedMock() => Commit(() =>
    {
        // Explicit fictional fixtures; stable IDs make repeated clicks safe.
        foreach (var (id, channel, quantity, total, states) in new[]
        {
            ("MOCK-A", "trendyol", 2m, 2m, new[] { "received", "picking", "packed", "shipped", "delivered" }),
            ("MOCK-B", "amazon", 1m, 2m, new[] { "received", "picking", "packed", "partially-shipped", "delivery-failed", "return-accepted", "quality-check", "quality-passed", "restocked" }),
            ("MOCK-C", "n11", 1m, 1m, new[] { "received", "picking", "packed", "shipped", "delivered", "return-accepted", "quality-check", "quarantined" })
        })
        {
            if (data.Events.Any(e => e.ShipmentId == id)) continue;
            DateTimeOffset? shippedAt = null;
            var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < states.Length; i++)
            {
                var at = start.AddMinutes(i);
                if (states[i] is "shipped" or "partially-shipped") shippedAt = at;
                ReceiveCore(new($"{id}-{i}", i + 1, id, id + "-P1", id + "-ORDER", "mock-main", channel,
                    "MOCK Taşıyıcı", shippedAt == null ? null : id + "-TRACK", states[i], quantity, total, at,
                    shippedAt, states[i] == "restocked"));
            }
        }
        // Same fictional order, two packages shipped from independent depots.
        for (var package = 1; package <= 2; package++)
        {
            if (data.Events.Any(e => e.ShipmentId == "MOCK-D" && e.PackageId == "MOCK-D-P" + package)) continue;
            var states = new[] { "received", "picking", "packed", "partially-shipped", "delivered" };
            var start = new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < states.Length; i++)
                ReceiveCore(new($"MOCK-D-P{package}-{i}", i + 1, "MOCK-D", "MOCK-D-P" + package,
                    "MOCK-D-ORDER", package == 1 ? "mock-main" : "mock-outlet", "hepsiburada", "MOCK Taşıyıcı",
                    i >= 3 ? $"MOCK-D-TRACK-{package}" : null, states[i], 1, 2, start.AddMinutes(i),
                    i >= 3 ? start.AddMinutes(3) : null));
        }
        return List();
    });
}
