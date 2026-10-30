using System.Text.Json;
using System.Text.RegularExpressions;

namespace MarketPilot.Web.Enterprise;

public sealed class IntegrationTrace
{
    public string Id { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string SourceSystem { get; set; } = "unknown";
    public string TargetSystem { get; set; } = "unknown";
    public string OperationType { get; set; } = "unknown";
    public string Status { get; set; } = "queued";
    public int AttemptCount { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? SafeErrorCode { get; set; }
    public string Channel { get; set; } = "";
}

public sealed class AuditLog
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TraceId { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string SourceSystem { get; set; } = "unknown";
    public string TargetSystem { get; set; } = "unknown";
    public string OperationType { get; set; } = "unknown";
    public string Status { get; set; } = "";
    public int AttemptCount { get; set; }
    public DateTimeOffset At { get; set; }
    public string? SafeErrorCode { get; set; }
    public string Channel { get; set; } = "";
}

public sealed class OperationTimeline
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TraceId { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string SourceSystem { get; set; } = "unknown";
    public string TargetSystem { get; set; } = "unknown";
    public string OperationType { get; set; } = "unknown";
    public string Status { get; set; } = "";
    public int AttemptCount { get; set; }
    public DateTimeOffset At { get; set; }
    public string? SafeErrorCode { get; set; }
    public string Channel { get; set; } = "";
}

public sealed class SyncCheckpoint
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string SourceSystem { get; set; } = "unknown";
    public string TargetSystem { get; set; } = "unknown";
    public string OperationType { get; set; } = "unknown";
    public string Status { get; set; } = "idle";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LastSuccessfulAt { get; set; }
    public string? SafeErrorCode { get; set; }
}

internal sealed class IntegrationMonitoringState
{
    public List<IntegrationTrace> Traces { get; set; } = [];
    public List<AuditLog> AuditLogs { get; set; } = [];
    public List<OperationTimeline> Timeline { get; set; } = [];
    public Dictionary<string, SyncCheckpoint> Checkpoints { get; set; } = [];
}

public sealed class IntegrationMonitoring
{
    private static readonly Regex SafeToken = new("^[a-zA-Z0-9._:-]{1,120}$", RegexOptions.Compiled);
    private static readonly Regex SafeCode = new("^[a-z0-9_-]{1,64}$", RegexOptions.Compiled);
    private static readonly HashSet<string> Systems = ["erp", "wms", "marketplace", "catalog", "audit", "cargo", "webhook"];
    private static readonly HashSet<string> TerminalStates = ["completed", "failed", "blocked", "ignored", "intervention-required", "reconciliation-required", "superseded"];
    private readonly object gate = new();
    private readonly string file;
    private readonly string tenantId;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private IntegrationMonitoringState state;
    private string persisted;

    public IntegrationMonitoring(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var folder = Path.Combine(environment.ContentRootPath, "Data");
        Directory.CreateDirectory(folder);
        file = Path.Combine(folder, "integration-monitoring.json");
        tenantId = Token(configuration["Workspace:BusinessId"] ?? "default");
        state = File.Exists(file)
            ? JsonSerializer.Deserialize<IntegrationMonitoringState>(File.ReadAllText(file), json)
                ?? throw new InvalidOperationException("İzleme kayıtları okunamadı.")
            : new();
        state.Traces ??= [];
        state.AuditLogs ??= [];
        state.Timeline ??= [];
        state.Checkpoints ??= [];
        persisted = JsonSerializer.Serialize(state, json);
    }

    public void Record(string id, string correlationId, string tenant, string source, string target,
        string operation, string status, int attempts, string? safeErrorCode = null, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("İzleme işlem kimliği boş olamaz.", nameof(id));
        var timestamp = at ?? DateTimeOffset.UtcNow;
        lock (gate)
        {
            var trace = state.Traces.FirstOrDefault(item => item.Id == Token(id));
            var isNew = trace is null;
            if (trace is null)
            {
                trace = new IntegrationTrace
                {
                    Id = Token(id),
                    CorrelationId = Token(correlationId),
                    TenantId = Token(tenant),
                    SourceSystem = SystemName(source),
                    TargetSystem = SystemName(target),
                    OperationType = Token(operation),
                    StartedAt = timestamp,
                    Channel = ChannelName(source)
                };
                state.Traces.Insert(0, trace);
            }

            var safeCode = ErrorCode(safeErrorCode);
            if (!isNew && trace.Status == status && trace.AttemptCount == Math.Max(0, attempts) &&
                trace.SafeErrorCode == safeCode)
                return;

            trace.Status = Token(status);
            trace.AttemptCount = Math.Max(0, attempts);
            trace.SafeErrorCode = safeCode;
            if (TerminalStates.Contains(status))
                trace.CompletedAt = timestamp;

            state.Timeline.Insert(0, new OperationTimeline
            {
                TraceId = trace.Id,
                CorrelationId = trace.CorrelationId,
                TenantId = trace.TenantId,
                SourceSystem = trace.SourceSystem,
                TargetSystem = trace.TargetSystem,
                OperationType = trace.OperationType,
                Status = trace.Status,
                AttemptCount = trace.AttemptCount,
                At = timestamp,
                SafeErrorCode = safeCode,
                Channel = trace.Channel
            });
            state.AuditLogs.Insert(0, new AuditLog
            {
                TraceId = trace.Id,
                CorrelationId = trace.CorrelationId,
                TenantId = trace.TenantId,
                SourceSystem = trace.SourceSystem,
                TargetSystem = trace.TargetSystem,
                OperationType = trace.OperationType,
                Status = trace.Status,
                AttemptCount = trace.AttemptCount,
                At = timestamp,
                SafeErrorCode = safeCode,
                Channel = trace.Channel
            });
            Trim();
            Save();
        }
    }

    public void RecordCheckpoint(string operation, string source, string target, string status,
        DateTimeOffset at, string? safeErrorCode = null)
    {
        var key = Token(operation);
        lock (gate)
        {
            if (!state.Checkpoints.TryGetValue(key, out var checkpoint))
            {
                checkpoint = new SyncCheckpoint
                {
                    Id = key,
                    TenantId = tenantId,
                    SourceSystem = SystemName(source),
                    TargetSystem = SystemName(target),
                    OperationType = key
                };
                state.Checkpoints[key] = checkpoint;
            }
            checkpoint.Status = Token(status);
            checkpoint.SafeErrorCode = ErrorCode(safeErrorCode);
            if (status == "running")
            {
                checkpoint.StartedAt = at;
                checkpoint.CompletedAt = null;
            }
            else
            {
                checkpoint.CompletedAt = at;
                if (status == "completed")
                    checkpoint.LastSuccessfulAt = at;
            }
            Save();
        }
    }

    public object Status(string? source = null, string? target = null)
    {
        lock (gate)
        {
            bool Matches(IntegrationTrace trace) =>
                (string.IsNullOrWhiteSpace(source) || trace.SourceSystem.Equals(SystemName(source), StringComparison.OrdinalIgnoreCase) ||
                    trace.Channel.Equals(ChannelName(source), StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(target) || trace.TargetSystem.Equals(SystemName(target), StringComparison.OrdinalIgnoreCase));
            var traces = state.Traces.Where(Matches).OrderByDescending(trace => trace.StartedAt).Take(500).ToArray();
            var ids = traces.Select(trace => trace.Id).ToHashSet(StringComparer.Ordinal);
            var timeline = state.Timeline.Where(entry => ids.Contains(entry.TraceId)).Take(1000).ToArray();
            var audit = state.AuditLogs.Where(entry => ids.Contains(entry.TraceId)).Take(500).ToArray();
            var checkpoints = state.Checkpoints.Values.Where(checkpoint =>
                (string.IsNullOrWhiteSpace(source) || checkpoint.SourceSystem.Equals(SystemName(source), StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(target) || checkpoint.TargetSystem.Equals(SystemName(target), StringComparison.OrdinalIgnoreCase)))
                .OrderBy(checkpoint => checkpoint.OperationType).ToArray();
            return new
            {
                traces,
                timeline,
                auditLogs = audit,
                checkpoints,
                pendingCount = traces.Count(trace => trace.Status is "queued" or "waiting" or "sending" or "uncertain" or "processing"),
                failedCount = traces.Count(trace => trace.Status is "failed" or "blocked" or "intervention-required" or "reconciliation-required"),
                retryCount = traces.Count(trace => trace.AttemptCount > 1),
                lastSuccessfulAt = traces.Where(trace => trace.Status == "completed").Select(trace => (DateTimeOffset?)trace.CompletedAt).Max(),
                erpWmsStatus = new
                {
                    erp = traces.Where(trace => trace.TargetSystem == "erp").OrderByDescending(trace => trace.StartedAt).Take(1).Select(trace => trace.Status).FirstOrDefault() ?? "idle",
                    wms = traces.Where(trace => trace.TargetSystem == "wms").OrderByDescending(trace => trace.StartedAt).Take(1).Select(trace => trace.Status).FirstOrDefault() ?? "idle"
                }
            };
        }
    }

    private void Trim()
    {
        if (state.Traces.Count > 3000)
        {
            var keep = state.Traces.OrderByDescending(trace => trace.StartedAt).Take(3000).ToList();
            var ids = keep.Select(trace => trace.Id).ToHashSet(StringComparer.Ordinal);
            state.Traces = keep;
            state.Timeline.RemoveAll(entry => !ids.Contains(entry.TraceId));
            state.AuditLogs.RemoveAll(entry => !ids.Contains(entry.TraceId));
        }
        if (state.Timeline.Count > 10000)
            state.Timeline.RemoveRange(10000, state.Timeline.Count - 10000);
        if (state.AuditLogs.Count > 10000)
            state.AuditLogs.RemoveRange(10000, state.AuditLogs.Count - 10000);
    }

    private void Save()
    {
        try
        {
            var candidate = JsonSerializer.Serialize(state, json);
            File.WriteAllText(file + ".tmp", candidate);
            File.Move(file + ".tmp", file, true);
            persisted = candidate;
        }
        catch
        {
            state = JsonSerializer.Deserialize<IntegrationMonitoringState>(persisted, json)!;
            throw;
        }
    }

    private static string Token(string? value) =>
        !string.IsNullOrWhiteSpace(value) && SafeToken.IsMatch(value) ? value : "redacted";

    private static string SystemName(string? value)
    {
        if (value is "mock-wms" or "wms") return "wms";
        if (value is "mock-erp" or "nebim-v3" or "erp") return "erp";
        if (value is "marketplace" or "catalog" or "audit" or "cargo" or "webhook") return value;
        if (!string.IsNullOrWhiteSpace(value) && value.StartsWith("channel-", StringComparison.Ordinal))
            return "marketplace";
        if (!string.IsNullOrWhiteSpace(value) && (value.StartsWith("demo|", StringComparison.Ordinal) || value.StartsWith("real|", StringComparison.Ordinal)))
            return "marketplace";
        var system = Token(value).ToLowerInvariant();
        if (system.StartsWith("channel-", StringComparison.Ordinal))
            return "marketplace";
        return Systems.Contains(system) ? system : "unknown";
    }

    private static string ChannelName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var parts = value.Split('|');
        var channel = value.StartsWith("channel-", StringComparison.Ordinal) ? value["channel-".Length..] :
            parts.Length > 1 && parts[0] is "demo" or "real" ? parts[1] : "";
        return channel is "trendyol" or "hepsiburada" or "n11" or "amazon" or "boyner" or "ticimax"
            ? channel : "";
    }

    private static string? ErrorCode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null :
        SafeCode.IsMatch(value) ? value : "operation-error";
}
