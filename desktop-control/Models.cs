using System.Text.Json.Serialization;

namespace CodexFeishuControl;

internal sealed class BridgeStatus
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("bindings")]
    public int Bindings { get; set; }

    [JsonPropertyName("ownerConfigured")]
    public bool OwnerConfigured { get; set; }

    [JsonPropertyName("pairingCode")]
    public string PairingCode { get; set; } = "";

    [JsonPropertyName("bindingCommand")]
    public string BindingCommand { get; set; } = "";

    [JsonPropertyName("activeSessions")]
    public int ActiveSessions { get; set; }

    [JsonPropertyName("pendingApprovals")]
    public int PendingApprovals { get; set; }

    [JsonPropertyName("pendingInputs")]
    public int PendingInputs { get; set; }

    [JsonPropertyName("queuedPrompts")]
    public int QueuedPrompts { get; set; }

    [JsonPropertyName("runningResumes")]
    public int RunningResumes { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("startedAt")]
    public string StartedAt { get; set; } = "";

    [JsonPropertyName("feishu")]
    public FeishuStatus Feishu { get; set; } = new();

    [JsonPropertyName("sessions")]
    public List<CodexSession> Sessions { get; set; } = [];

    [JsonPropertyName("approvals")]
    public List<BridgeApproval> Approvals { get; set; } = [];

    [JsonPropertyName("settings")]
    public BridgeSettings Settings { get; set; } = new();
}

internal sealed class BridgeSettings
{
    [JsonPropertyName("notifyActivity")]
    public bool NotifyActivity { get; set; }

    [JsonPropertyName("autoRetryErrors")]
    public bool AutoRetryErrors { get; set; }

    [JsonPropertyName("autoApprove")]
    public bool AutoApprove { get; set; }
}

internal sealed class SettingsUpdateResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("settings")]
    public BridgeSettings Settings { get; set; } = new();

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";
}

internal sealed class FeishuStatus
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "idle";

    [JsonPropertyName("lastConnectTime")]
    public long? LastConnectTime { get; set; }

    [JsonPropertyName("nextConnectTime")]
    public long? NextConnectTime { get; set; }

    [JsonPropertyName("reconnectAttempts")]
    public int ReconnectAttempts { get; set; }
}

internal sealed class CodexSession
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("shortId")]
    public string ShortId { get; set; } = "";

    [JsonPropertyName("alias")]
    public string Alias { get; set; } = "";

    [JsonPropertyName("projectName")]
    public string ProjectName { get; set; } = "";

    [JsonPropertyName("cwd")]
    public string Cwd { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("statusLabel")]
    public string StatusLabel { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("lastSeenAt")]
    public string LastSeenAt { get; set; } = "";

    [JsonPropertyName("openedAt")]
    public string OpenedAt { get; set; } = "";

    [JsonPropertyName("remoteResumeRunning")]
    public bool RemoteResumeRunning { get; set; }

    [JsonPropertyName("managedTerminal")]
    public bool ManagedTerminal { get; set; }

    [JsonPropertyName("managedTerminalElevated")]
    public bool ManagedTerminalElevated { get; set; }

    [JsonPropertyName("managedTerminalOnline")]
    public bool ManagedTerminalOnline { get; set; }

    [JsonPropertyName("managedTerminalReady")]
    public bool ManagedTerminalReady { get; set; }

    [JsonPropertyName("queuedPrompts")]
    public int QueuedPrompts { get; set; }
}

internal sealed class AliasUpdateResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";
}

internal sealed class BridgeApproval
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("sessionLabel")]
    public string SessionLabel { get; set; } = "";

    [JsonPropertyName("projectName")]
    public string ProjectName { get; set; } = "";

    [JsonPropertyName("cwd")]
    public string Cwd { get; set; } = "";

    [JsonPropertyName("toolName")]
    public string ToolName { get; set; } = "";

    [JsonPropertyName("toolPreview")]
    public string ToolPreview { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("expiresAt")]
    public string ExpiresAt { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = "";

    [JsonPropertyName("resolvedAt")]
    public string ResolvedAt { get; set; } = "";
}

internal sealed class ApprovalResolveResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("alreadyResolved")]
    public bool AlreadyResolved { get; set; }

    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";
}
