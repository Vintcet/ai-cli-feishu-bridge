using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiCliFeishuControl;

internal sealed class ModelStringConverter : JsonConverter<string>
{
    public override string Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() ?? "";
        }

        if (reader.TokenType is JsonTokenType.Null or JsonTokenType.None)
        {
            return "";
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String)
            {
                return id.GetString() ?? "";
            }
            return "";
        }

        using var fallback = JsonDocument.ParseValue(ref reader);
        return fallback.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

internal sealed class BridgeStatus
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("hostKind")]
    public string HostKind { get; set; } = "";

    [JsonPropertyName("managementApiVersion")]
    public int ManagementApiVersion { get; set; }

    [JsonPropertyName("instanceName")]
    public string InstanceName { get; set; } = "";

    [JsonPropertyName("ownershipMode")]
    public string OwnershipMode { get; set; } = "";

    [JsonPropertyName("activeOwner")]
    public bool ActiveOwner { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

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

    [JsonPropertyName("pendingDesktopApprovals")]
    public int PendingDesktopApprovals { get; set; }

    [JsonPropertyName("pendingInputs")]
    public int PendingInputs { get; set; }

    [JsonPropertyName("queuedPrompts")]
    public int QueuedPrompts { get; set; }

    [JsonPropertyName("runningResumes")]
    public int RunningResumes { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("startedAt")]
    public string StartedAt { get; set; } = "";

    [JsonPropertyName("feishu")]
    public FeishuStatus Feishu { get; set; } = new();

    [JsonPropertyName("sessions")]
    public List<AssistantSession> Sessions { get; set; } = [];

    [JsonPropertyName("historySessions")]
    public List<AssistantSession> HistorySessions { get; set; } = [];

    [JsonPropertyName("approvals")]
    public List<BridgeApproval> Approvals { get; set; } = [];

    [JsonPropertyName("settings")]
    public BridgeSettings Settings { get; set; } = new();
}

internal sealed class BridgeSettings
{
    [JsonPropertyName("workspaceRoot")]
    public string WorkspaceRoot { get; set; } = "";

    [JsonPropertyName("notifyActivity")]
    public bool NotifyActivity { get; set; }

    [JsonPropertyName("notifyUserPrompts")]
    public bool NotifyUserPrompts { get; set; }

    [JsonPropertyName("autoRetryErrors")]
    public bool AutoRetryErrors { get; set; }

    [JsonPropertyName("retryMaxAttempts")]
    public int RetryMaxAttempts { get; set; } = 3;

    [JsonPropertyName("retryIntervalSeconds")]
    public int RetryIntervalSeconds { get; set; } = 5;

    [JsonPropertyName("retryJitterSeconds")]
    public int RetryJitterSeconds { get; set; } = 3;

    [JsonPropertyName("autoApprove")]
    public bool AutoApprove { get; set; }

    [JsonPropertyName("notifyAutoApprovals")]
    public bool NotifyAutoApprovals { get; set; }
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
    // WebSocket reconnect timestamps may contain fractional milliseconds.
    // Keep these as doubles so one fractional value cannot invalidate the
    // entire /health response in the desktop client.
    public double? LastConnectTime { get; set; }

    [JsonPropertyName("nextConnectTime")]
    public double? NextConnectTime { get; set; }

    [JsonPropertyName("reconnectAttempts")]
    public int ReconnectAttempts { get; set; }
}

internal sealed class AssistantSession
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
    [JsonConverter(typeof(ModelStringConverter))]
    public string Model { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("statusLabel")]
    public string StatusLabel { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("runtime")]
    public string? Runtime { get; set; }

    [JsonPropertyName("lastSeenAt")]
    public string LastSeenAt { get; set; } = "";

    [JsonPropertyName("endedAt")]
    public string EndedAt { get; set; } = "";

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

    [JsonPropertyName("managedByAssistant")]
    public bool ManagedByAssistant { get; set; }

    [JsonPropertyName("feishuChatId")]
    public string FeishuChatId { get; set; } = "";

    [JsonPropertyName("feishuChatName")]
    public string FeishuChatName { get; set; } = "";

    [JsonPropertyName("feishuChatStatus")]
    public string FeishuChatStatus { get; set; } = "not_applicable";

    [JsonPropertyName("feishuChatError")]
    public string FeishuChatError { get; set; } = "";

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

internal sealed class SessionGroupRetryResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("chatName")]
    public string ChatName { get; set; } = "";

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";
}

internal sealed class HistoryHideResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";
}

internal sealed class RuntimeLaunchClaimResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("request")]
    public RuntimeLaunchRequest? Request { get; set; }

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";
}

internal sealed class RuntimeLaunchRequest
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "resume";

    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("runtime")]
    public string Runtime { get; set; } = "";

    [JsonPropertyName("cwd")]
    public string Cwd { get; set; } = "";

    [JsonPropertyName("projectName")]
    public string ProjectName { get; set; } = "";

    [JsonPropertyName("elevated")]
    public bool Elevated { get; set; }

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";
}

internal sealed class RuntimeLaunchCompleteResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";
}

internal sealed class OpenCodeLaunchResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("cwd")]
    public string Cwd { get; set; } = "";

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

    [JsonPropertyName("requiresManualApproval")]
    public bool RequiresManualApproval { get; set; } = true;

    [JsonPropertyName("desktopApprovalRequested")]
    public bool DesktopApprovalRequested { get; set; } = true;

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
