using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiCliFeishu.Bridge.Adapters.Storage;

public abstract class ExtensibleStoreObject
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class BindingStoreDocument : ExtensibleStoreObject
{
    public Dictionary<string, BindingStoreRecord> Users { get; set; } = [];
    public string? OwnerOpenId { get; set; }
    public string? PairingCode { get; set; }
}

public sealed class BindingStoreRecord : ExtensibleStoreObject
{
    public string OpenId { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public string ChatType { get; set; } = string.Empty;
    public string BoundAt { get; set; } = string.Empty;
}

public sealed class SessionStoreDocument : ExtensibleStoreObject
{
    public Dictionary<string, SessionStoreRecord> Sessions { get; set; } = [];
}

public sealed class SessionStoreRecord : ExtensibleStoreObject
{
    public string SessionId { get; set; } = string.Empty;
    public string? ShortId { get; set; }
    public string Cwd { get; set; } = string.Empty;
    public string? ProjectName { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Runtime { get; set; }
    public string? OpenedAt { get; set; }
    public string LastSeenAt { get; set; } = string.Empty;
    public string? EndedAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class RouteStoreDocument : ExtensibleStoreObject
{
    public Dictionary<string, MessageRouteStoreRecord> Messages { get; set; } = [];
    public Dictionary<string, string> ProcessedInbound { get; set; } = [];
}

public sealed class MessageRouteStoreRecord : ExtensibleStoreObject
{
    public string MessageId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string? RequestId { get; set; }
}

public sealed class ApprovalStoreDocument : ExtensibleStoreObject
{
    public Dictionary<string, ApprovalStoreRecord> Requests { get; set; } = [];
}

public sealed class ApprovalStoreRecord : ExtensibleStoreObject
{
    public string RequestId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string TurnId { get; set; } = string.Empty;
    public string Cwd { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string ToolPreview { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string ExpiresAt { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public List<string> MessageIds { get; set; } = [];
    public string? Resolution { get; set; }
    public string? ResolvedAt { get; set; }
}

public sealed class SettingsStoreDocument : ExtensibleStoreObject
{
    public string? WorkspaceRoot { get; set; }
    public bool? NotifyActivity { get; set; }
    public bool? NotifyUserPrompts { get; set; }
    public bool? AutoRetryErrors { get; set; }
    public int? RetryMaxAttempts { get; set; }
    public int? RetryIntervalSeconds { get; set; }
    public int? RetryJitterSeconds { get; set; }
    public bool? AutoApprove { get; set; }
    public bool? NotifyAutoApprovals { get; set; }
}

public sealed class ControlTokenStoreDocument : ExtensibleStoreObject
{
    public string? Token { get; set; }
}

public sealed record BridgeStoreSnapshot(
    BindingStoreDocument Bindings,
    SessionStoreDocument Sessions,
    RouteStoreDocument Routes,
    ApprovalStoreDocument Approvals,
    SettingsStoreDocument Settings,
    ControlTokenStoreDocument ControlToken);
