namespace MarketPilot.Web.Workspace;

public sealed class OperationalState
{
    public OperationalPolicy Policy {get;set;}=new();
    public List<InventoryHold> Holds {get;set;}=[];
    public List<OperationalRequest> Requests {get;set;}=[];
    public List<OperationalFinding> Findings {get;set;}=[];
    public List<OperationalDelivery> Deliveries {get;set;}=[];
    public List<SettlementRecord> Settlements {get;set;}=[];
    public DateTimeOffset? LastScanAt {get;set;}
    public int FindingCount {get;set;}
    public string LastAlertSignature {get;set;}="";public long NotificationEpisode {get;set;}
}
public sealed record SettlementDraft(string RequestId,string OrderId,string ExternalReference,string Currency,decimal Commission,decimal Shipping,decimal Other,decimal Refund,decimal NetPaid);
public sealed record SettlementRecord(string RequestId,string Fingerprint,string OrderId,string StoreId,string Source,string ExternalReference,string Currency,decimal Gross,decimal Fees,decimal Refund,decimal ExpectedNet,decimal NetPaid,decimal Difference,DateTimeOffset RecordedAt);
public sealed record OperationalPolicy(bool ReservationsEnabled=false,string SourceStockKind="available",bool AutoScan=false,int ScanIntervalSeconds=300,int StaleMinutes=60,bool NotificationsEnabled=false);
public sealed class InventoryHold
{
    public string Id {get;set;}=Guid.NewGuid().ToString("N");public string RequestId {get;set;}="";public string Fingerprint {get;set;}="";
    public string ProductId {get;set;}="";public string StoreId {get;set;}="";public string Source {get;set;}="";public string PackageId {get;set;}="";
    public string OrderNumber {get;set;}="";
    public string AccountIdentity {get;set;}="";
    public int Quantity {get;set;}public string State {get;set;}="held";public DateTimeOffset? ExpiresAt {get;set;}public DateTimeOffset CreatedAt {get;set;}=DateTimeOffset.UtcNow;
}
public sealed record HoldDraft(string RequestId,string ProductId,int Quantity,int Minutes=30);
public sealed record StockAcknowledgement(string StoreId,string Source,string PackageId,string? AccountIdentity=null);
public sealed record OperationQuantity(string LineId,int Quantity);
public sealed record OperationPackage(string Code,OperationQuantity[] Lines);
public sealed record OperationDraft(string RequestId,string OrderId,string Kind,OperationQuantity[]? Lines=null,OperationPackage[]? Packages=null,string? Note=null,decimal? Amount=null);
public sealed class OperationalRequest
{
    public string Id {get;set;}=Guid.NewGuid().ToString("N");public string RequestId {get;set;}="";public string Fingerprint {get;set;}="";
    public string OrderId {get;set;}="";public string StoreId {get;set;}="";public string Source {get;set;}="";public string OrderFingerprint {get;set;}="";
    public string AccountIdentity {get;set;}="";
    public string Kind {get;set;}="";public string State {get;set;}="draft";public string? Error {get;set;}public string? ReceiptId {get;set;}
    public OperationQuantity[] Lines {get;set;}=[];public OperationPackage[] Packages {get;set;}=[];public string Note {get;set;}="";public decimal? Amount {get;set;}public string Currency {get;set;}="";
    public DateTimeOffset CreatedAt {get;set;}=DateTimeOffset.UtcNow;
}
public sealed record OperationalFinding(string Id,string Kind,string StoreId,string Reference,string Message,DateTimeOffset ObservedAt);
public sealed class OperationalDelivery
{
    public string Connector {get;set;}="notification";public string Kind {get;set;}="notification";public string Reference {get;set;}="";public string StoreId {get;set;}="";public string Source {get;set;}="";public string AccountIdentity {get;set;}="";public string? ExternalReference {get;set;}
    public string Id {get;set;}=Guid.NewGuid().ToString("N");public string Fingerprint {get;set;}="";public string ConnectorIdentity {get;set;}="";
    public string State {get;set;}="queued";public string? Error {get;set;}public string Payload {get;set;}="{}";public int Attempts {get;set;}public DateTimeOffset NextAttemptAt {get;set;}=DateTimeOffset.UtcNow;
}
