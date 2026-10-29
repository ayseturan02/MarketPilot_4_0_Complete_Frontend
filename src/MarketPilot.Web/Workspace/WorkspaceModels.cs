using MarketPilot.Web.Catalog;

namespace MarketPilot.Web.Workspace;

public sealed class WorkspaceRecord
{
    public List<ExternalReference> ExternalReferences { get; set; } = [];
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "invoice";
    public string OrderId { get; set; } = "";
    public string Source { get; set; } = "";
    public string Number { get; set; } = "";
    public string Note { get; set; } = "";
    public string Status { get; set; } = "draft";
    public decimal? Amount { get; set; }
    public string Currency { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed record WorkspaceRecordDraft(string Kind, string OrderId, string RequestId, string? Number = null, string? Note = null, decimal? Amount = null, string? Source = null);
public sealed record WorkspaceAction(string Action, string RequestId, string? Text = null, int? Version = null, string? Source = null);
public sealed record LocalOrderLine(string ProductId, int Quantity);
public sealed record LocalOrderDraft(string Customer, string RequestId, LocalOrderLine[] Lines, string? Channel = null,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ReservationId=null);
public sealed record WorkspaceRow(string Id, string Kind, string Reference, string Title, string Channel, string StoreId,
    string OrderId, string ProductId, string Status, decimal? Amount, string Currency, DateTimeOffset? Date,
    string Detail = "", string Action = "", int Version = 0, string NativeReference = "", string Origin = "local", string Source = "");
