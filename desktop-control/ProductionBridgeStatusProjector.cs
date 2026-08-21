using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishuControl;

internal sealed class ProductionBridgeStatusProjector(
    string dataDirectory,
    string defaultWorkspaceRoot,
    string bindCommand)
{
    private static readonly TimeSpan RecentApprovalWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FallbackPresenceLifetime = TimeSpan.FromMinutes(5);

    public async Task<BridgeStatus> ProjectAsync(
        BridgeProductionHealthStatus health,
        BridgeProductionControlStatus control,
        BridgeProductionPresenceStatus presence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(presence);

        var bindingsTask = ReadStoreAsync(
            BridgeStoreFile.Bindings,
            new BindingStoreDocument(),
            cancellationToken);
        var sessionsTask = ReadStoreAsync(
            BridgeStoreFile.Sessions,
            new SessionStoreDocument(),
            cancellationToken);
        var approvalsTask = ReadStoreAsync(
            BridgeStoreFile.Approvals,
            new ApprovalStoreDocument(),
            cancellationToken);
        var settingsTask = ReadStoreAsync(
            BridgeStoreFile.Settings,
            new SettingsStoreDocument(),
            cancellationToken);

        await Task.WhenAll(bindingsTask, sessionsTask, approvalsTask, settingsTask);
        return Project(
            health,
            control,
            presence,
            await bindingsTask,
            await sessionsTask,
            await approvalsTask,
            await settingsTask,
            defaultWorkspaceRoot,
            bindCommand,
            DateTimeOffset.Now);
    }

    internal static BridgeStatus Project(
        BridgeProductionHealthStatus health,
        BridgeProductionControlStatus control,
        BridgeProductionPresenceStatus presence,
        BindingStoreDocument bindings,
        SessionStoreDocument sessions,
        ApprovalStoreDocument approvals,
        SettingsStoreDocument settings,
        string defaultWorkspaceRoot,
        string bindCommand,
        DateTimeOffset now,
        Func<int, string?, bool>? processProbe = null)
    {
        ValidateIdentity(health, control);
        var presenceItems = presence.Ok
            ? presence.Sessions
            : InferFallbackPresence(sessions, now, processProbe);
        var presenceBySessionId = presenceItems
            .Where(item => !string.IsNullOrWhiteSpace(item.SessionId))
            .GroupBy(item => item.SessionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var sessionRecords = sessions.Sessions.Values
            .Where(session => !string.IsNullOrWhiteSpace(session.SessionId))
            .ToArray();
        var sessionById = sessionRecords
            .GroupBy(session => session.SessionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var activeSessions = sessionRecords
            .Where(session => presenceBySessionId.ContainsKey(session.SessionId))
            .OrderBy(session => session.OpenedAt ?? "", StringComparer.Ordinal)
            .ThenBy(session => session.SessionId, StringComparer.Ordinal)
            .Select(session => ProjectSession(
                session,
                presenceBySessionId[session.SessionId]))
            .ToList();
        var historySessions = sessionRecords
            .Where(session =>
                IsVisibleHistorySession(session) &&
                !presenceBySessionId.ContainsKey(session.SessionId))
            .OrderByDescending(SessionHistoryTime)
            .ThenBy(session => session.SessionId, StringComparer.Ordinal)
            .Select(session => ProjectSession(session, null))
            .ToList();
        var approvalViews = approvals.Requests.Values
            .Where(approval => IsVisibleApproval(approval, now))
            .OrderBy(approval => approval.CreatedAt, StringComparer.Ordinal)
            .ThenBy(approval => approval.RequestId, StringComparer.Ordinal)
            .Select(approval => ProjectApproval(approval, sessionById))
            .ToList();
        var pairingCode = bindings.PairingCode?.Trim() ?? "";
        var normalizedBindCommand = string.IsNullOrWhiteSpace(bindCommand)
            ? "绑定"
            : bindCommand.Trim();
        var business = control.BusinessState;

        return new BridgeStatus
        {
            // The production Store status component may be failed while the
            // initialized in-process business owner remains authoritative.
            // Desktop readiness therefore follows lifecycle + business state,
            // not the redacted Store observer's health bit.
            Ok = string.Equals(health.Status, "ready", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(control.Lifecycle, "ready", StringComparison.OrdinalIgnoreCase) &&
                health.ActiveOwner &&
                control.ActiveOwner &&
                business.Initialized,
            HostKind = health.HostKind,
            ManagementApiVersion = health.ManagementApiVersion,
            InstanceName = health.InstanceName,
            OwnershipMode = health.OwnershipMode,
            ActiveOwner = health.ActiveOwner,
            Status = health.Status,
            Bindings = bindings.Users.Count,
            OwnerConfigured = !string.IsNullOrWhiteSpace(bindings.OwnerOpenId),
            PairingCode = pairingCode,
            BindingCommand = pairingCode.Length == 0
                ? ""
                : $"{normalizedBindCommand} {pairingCode}",
            ActiveSessions = activeSessions.Count,
            PendingApprovals = business.PendingApprovals,
            PendingDesktopApprovals = approvalViews.Count(approval =>
                approval.Status == "pending" &&
                approval.RequiresManualApproval &&
                approval.DesktopApprovalRequested),
            PendingInputs = business.PendingInputs,
            QueuedPrompts = 0,
            RunningResumes = 0,
            Version = health.Version,
            ProcessId = health.ProcessId,
            StartedAt = health.StartedAt,
            Feishu = ProjectFeishu(health, control),
            Sessions = activeSessions,
            HistorySessions = historySessions,
            Approvals = approvalViews,
            Settings = ProjectSettings(settings, defaultWorkspaceRoot),
        };
    }

    private async Task<T> ReadStoreAsync<T>(
        BridgeStoreFile file,
        T fallback,
        CancellationToken cancellationToken)
        where T : class
    {
        var path = Path.Combine(dataDirectory, file.FileName);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 64 * 1024,
                    leaveOpen: false);
                var json = await reader.ReadToEndAsync(cancellationToken);
                return BridgeStoreJson.Deserialize<T>(json, file);
            }
            catch (FileNotFoundException)
            {
                return fallback;
            }
            catch (DirectoryNotFoundException)
            {
                return fallback;
            }
            catch (IOException) when (attempt == 0)
            {
                await Task.Yield();
            }
        }
        throw new IOException($"无法读取生产 Store 文件 {file.FileName}。");
    }

    private static void ValidateIdentity(
        BridgeProductionHealthStatus health,
        BridgeProductionControlStatus control)
    {
        if (health.ProcessId <= 0 ||
            health.ProcessId != control.ProcessId ||
            !string.Equals(health.HostKind, control.HostKind, StringComparison.Ordinal) ||
            health.ManagementApiVersion != control.ManagementApiVersion ||
            !string.Equals(health.InstanceName, control.InstanceName, StringComparison.Ordinal) ||
            !string.Equals(health.OwnershipMode, control.OwnershipMode, StringComparison.Ordinal) ||
            health.ActiveOwner != control.ActiveOwner)
        {
            throw new InvalidDataException("生产 Host 的健康与控制状态身份不一致。");
        }
    }

    private static FeishuStatus ProjectFeishu(
        BridgeProductionHealthStatus health,
        BridgeProductionControlStatus control)
    {
        var pump = ComponentStatus(health, "feishu-event-pump");
        var credentials = ComponentStatus(health, "feishu-credentials");
        var adapter = control.Boundaries.FeishuAdapter;
        var state = ComponentReady(pump) &&
            ComponentReady(credentials) &&
            adapter.LiveEventStreamEnabled &&
            adapter.OutboundMessagingEnabled
                ? "connected"
                : pump switch
                {
                    "reconnecting" => "reconnecting",
                    "starting" or "connecting" => "connecting",
                    "failed" or "faulted" => "failed",
                    _ when string.Equals(
                        health.Status,
                        "starting",
                        StringComparison.OrdinalIgnoreCase) => "connecting",
                    _ => "idle",
                };
        return new FeishuStatus { State = state };
    }

    private static string ComponentStatus(
        BridgeProductionHealthStatus health,
        string name) => health.Components
            .FirstOrDefault(component => string.Equals(
                component.Name,
                name,
                StringComparison.Ordinal))?.Status
            .Trim()
            .ToLowerInvariant() ?? "";

    private static bool ComponentReady(string status) =>
        status is "ready" or "healthy";

    private static AssistantSession ProjectSession(
        SessionStoreRecord session,
        BridgeProductionSessionPresence? presence)
    {
        var alias = ExtensionString(session.ExtensionData, "alias");
        var managedTerminalId = ExtensionString(
            session.ExtensionData,
            "managedTerminalId");
        var managedTerminal = managedTerminalId.Length > 0;
        var managedByAssistant = ExtensionBoolean(
            session.ExtensionData,
            "managedByAssistant") == true;
        var feishuChatId = ExtensionString(session.ExtensionData, "feishuChatId");
        var feishuChatError = ExtensionString(
            session.ExtensionData,
            "feishuChatError");
        var status = session.Status ?? "";

        return new AssistantSession
        {
            SessionId = session.SessionId,
            ShortId = string.IsNullOrWhiteSpace(session.ShortId)
                ? ShortSessionId(session.SessionId)
                : session.ShortId,
            Alias = alias,
            ProjectName = string.IsNullOrWhiteSpace(session.ProjectName)
                ? ProjectNameFromCwd(session.Cwd)
                : session.ProjectName,
            Cwd = session.Cwd,
            Model = ExtensionModel(session.ExtensionData, "model"),
            Status = status,
            StatusLabel = StatusLabel(status),
            Source = ExtensionString(session.ExtensionData, "source"),
            Runtime = string.IsNullOrWhiteSpace(session.Runtime) ? "codex" : session.Runtime,
            LastSeenAt = session.LastSeenAt,
            EndedAt = session.EndedAt ?? "",
            OpenedAt = session.OpenedAt ?? "",
            RemoteResumeRunning = false,
            ManagedTerminal = managedTerminal,
            ManagedTerminalElevated = ExtensionBoolean(
                session.ExtensionData,
                "managedTerminalElevated") == true,
            ManagedTerminalOnline = presence?.ManagedTerminalOnline == true,
            ManagedTerminalReady = presence?.ManagedTerminalReady == true,
            ManagedByAssistant = managedByAssistant,
            FeishuChatId = feishuChatId,
            FeishuChatName = ExtensionString(
                session.ExtensionData,
                "feishuChatName"),
            FeishuChatStatus = feishuChatId.Length > 0
                ? "connected"
                : managedByAssistant
                    ? feishuChatError.Length > 0 ? "error" : "pending"
                    : "not_applicable",
            FeishuChatError = feishuChatError,
            QueuedPrompts = 0,
        };
    }

    private static bool IsVisibleHistorySession(SessionStoreRecord session) =>
        ExtensionBoolean(session.ExtensionData, "historyEligible") == true &&
        ExtensionString(session.ExtensionData, "historyHiddenAt").Length == 0 &&
        !session.SessionId.StartsWith("managed-terminal-", StringComparison.Ordinal);

    private static DateTimeOffset SessionHistoryTime(SessionStoreRecord session)
    {
        var value = string.IsNullOrWhiteSpace(session.EndedAt)
            ? session.LastSeenAt
            : session.EndedAt;
        return DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp
            : DateTimeOffset.MinValue;
    }

    private static bool IsVisibleApproval(
        ApprovalStoreRecord approval,
        DateTimeOffset now)
    {
        if (string.Equals(approval.Status, "pending", StringComparison.Ordinal))
        {
            return !DateTimeOffset.TryParse(approval.ExpiresAt, out var expiresAt) ||
                expiresAt > now;
        }
        return DateTimeOffset.TryParse(approval.ResolvedAt, out var resolvedAt) &&
            resolvedAt >= now - RecentApprovalWindow;
    }

    private static BridgeApproval ProjectApproval(
        ApprovalStoreRecord approval,
        IReadOnlyDictionary<string, SessionStoreRecord> sessionById)
    {
        sessionById.TryGetValue(approval.SessionId, out var session);
        var requiresManualApproval = ExtensionBoolean(
            approval.ExtensionData,
            "requiresManualApproval") != false;
        var desktopApprovalRequested = ExtensionBoolean(
            approval.ExtensionData,
            "desktopApprovalRequested") ??
            (requiresManualApproval && approval.MessageIds.Count == 0);
        var projectName = session is null || string.IsNullOrWhiteSpace(session.ProjectName)
            ? ProjectNameFromCwd(approval.Cwd)
            : session.ProjectName;
        var shortId = session is null || string.IsNullOrWhiteSpace(session.ShortId)
            ? ShortSessionId(approval.SessionId)
            : session.ShortId;
        var alias = session is null
            ? ""
            : ExtensionString(session.ExtensionData, "alias");
        var sessionLabel = alias.Length > 0
            ? $"@{alias} · {projectName} #{shortId}"
            : $"{projectName} #{shortId}";

        return new BridgeApproval
        {
            RequestId = approval.RequestId,
            SessionId = approval.SessionId,
            SessionLabel = sessionLabel,
            ProjectName = projectName,
            Cwd = approval.Cwd,
            ToolName = approval.ToolName,
            ToolPreview = approval.ToolPreview,
            CreatedAt = approval.CreatedAt,
            ExpiresAt = approval.ExpiresAt,
            Status = approval.Status,
            RequiresManualApproval = requiresManualApproval,
            DesktopApprovalRequested = desktopApprovalRequested,
            Resolution = approval.Resolution ?? "",
            ResolvedAt = approval.ResolvedAt ?? "",
        };
    }

    private static BridgeSettings ProjectSettings(
        SettingsStoreDocument settings,
        string defaultWorkspaceRoot) => new()
    {
        WorkspaceRoot = string.IsNullOrWhiteSpace(settings.WorkspaceRoot)
            ? defaultWorkspaceRoot
            : Path.GetFullPath(settings.WorkspaceRoot.Trim()),
        NotifyActivity = settings.NotifyActivity == true,
        NotifyUserPrompts = settings.NotifyUserPrompts == true,
        AutoRetryErrors = settings.AutoRetryErrors == true,
        RetryMaxAttempts = IntegerInRange(
            settings.RetryMaxAttempts,
            BridgeSettingsLimits.RetryMaxAttemptsMinimum,
            BridgeSettingsLimits.RetryMaxAttemptsMaximum,
            BridgeSettingsLimits.RetryMaxAttemptsDefault),
        RetryIntervalSeconds = IntegerInRange(
            settings.RetryIntervalSeconds,
            1,
            600,
            5),
        RetryJitterSeconds = IntegerInRange(
            settings.RetryJitterSeconds,
            0,
            120,
            3),
        AutoApprove = settings.AutoApprove == true,
        AutoApproveMode = BridgeAutoApproveModes.Resolve(
            settings.AutoApproveMode,
            settings.AutoApprove),
        NotifyAutoApprovals = settings.NotifyAutoApprovals == true,
    };

    private static int IntegerInRange(int? value, int minimum, int maximum, int fallback) =>
        value is >= 0 && value >= minimum && value <= maximum ? value.Value : fallback;

    private static string ExtensionString(
        IReadOnlyDictionary<string, JsonElement>? extensionData,
        string name)
    {
        if (!TryExtension(extensionData, name, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "";
        }
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static string ExtensionModel(
        IReadOnlyDictionary<string, JsonElement>? extensionData,
        string name)
    {
        if (!TryExtension(extensionData, name, out var value))
        {
            return "";
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "";
        }
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("id", out var id) &&
            id.ValueKind == JsonValueKind.String)
        {
            return id.GetString() ?? "";
        }
        return "";
    }

    private static bool? ExtensionBoolean(
        IReadOnlyDictionary<string, JsonElement>? extensionData,
        string name)
    {
        if (!TryExtension(extensionData, name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static bool TryExtension(
        IReadOnlyDictionary<string, JsonElement>? extensionData,
        string name,
        out JsonElement value)
    {
        value = default;
        if (extensionData is null)
        {
            return false;
        }
        if (extensionData.TryGetValue(name, out value))
        {
            return true;
        }
        foreach (var item in extensionData)
        {
            if (string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = item.Value;
                return true;
            }
        }
        return false;
    }

    private static IReadOnlyList<BridgeProductionSessionPresence> InferFallbackPresence(
        SessionStoreDocument sessions,
        DateTimeOffset now,
        Func<int, string?, bool>? processProbe)
    {
        processProbe ??= IsTrackedAssistantProcess;
        var result = new List<BridgeProductionSessionPresence>();
        foreach (var session in sessions.Sessions.Values
                     .Where(session =>
                         !string.IsNullOrWhiteSpace(session.SessionId) &&
                         !string.Equals(session.Status, "ended", StringComparison.Ordinal))
                     .OrderBy(session => session.SessionId, StringComparer.Ordinal))
        {
            if (string.Equals(session.Runtime, "opencode", StringComparison.Ordinal) ||
                ExtensionString(session.ExtensionData, "managedTerminalId").Length > 0)
            {
                continue;
            }
            if (TryExtensionInt32(session.ExtensionData, "clientProcessId", out var processId))
            {
                if (processProbe(
                        processId,
                        ExtensionString(session.ExtensionData, "clientProcessStartedAt")))
                {
                    result.Add(new BridgeProductionSessionPresence
                    {
                        SessionId = session.SessionId,
                    });
                }
                continue;
            }
            if (DateTimeOffset.TryParse(session.LastSeenAt, out var lastSeenAt) &&
                lastSeenAt <= now + TimeSpan.FromSeconds(1) &&
                now - lastSeenAt <= FallbackPresenceLifetime)
            {
                result.Add(new BridgeProductionSessionPresence
                {
                    SessionId = session.SessionId,
                });
            }
        }
        return result;
    }

    private static bool TryExtensionInt32(
        IReadOnlyDictionary<string, JsonElement>? extensionData,
        string name,
        out int value)
    {
        value = 0;
        return TryExtension(extensionData, name, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out value) &&
            value > 0;
    }

    private static bool IsTrackedAssistantProcess(int processId, string? expectedStartedAt)
    {
        if (processId <= 0)
        {
            return false;
        }
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited ||
                !string.Equals(process.ProcessName, "codex", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(process.ProcessName, "claude", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(expectedStartedAt))
            {
                return true;
            }
            if (!DateTimeOffset.TryParse(expectedStartedAt, out var expected))
            {
                return false;
            }
            var actual = new DateTimeOffset(process.StartTime.ToUniversalTime());
            return (actual - expected.ToUniversalTime()).Duration() <= TimeSpan.FromSeconds(1);
        }
        catch (Exception error) when (
            error is ArgumentException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception or
            UnauthorizedAccessException or
            NotSupportedException)
        {
            return false;
        }
    }

    private static string StatusLabel(string status) => status switch
    {
        "starting" => "正在启动",
        "ready" => "窗口已打开",
        "running" => "运行中",
        "waiting" => "等待回复",
        "pending_approval" => "待审批",
        "pending_input" => "待补充信息",
        "local_approval" => "本机确认中",
        "error" => "异常",
        "ended" => "已结束",
        _ => status,
    };

    private static string ProjectNameFromCwd(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return "";
        }
        var trimmed = Path.TrimEndingDirectorySeparator(cwd.Trim());
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
    }

    private static string ShortSessionId(string sessionId)
    {
        var compact = new string(sessionId.Where(char.IsLetterOrDigit).ToArray());
        var source = compact.Length > 0 ? compact : sessionId;
        return source[^Math.Min(8, source.Length)..].ToLowerInvariant();
    }
}

internal sealed class BridgeProductionHealthStatus
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("hostKind")]
    public string HostKind { get; set; } = "";

    [JsonPropertyName("managementApiVersion")]
    public int ManagementApiVersion { get; set; }

    [JsonPropertyName("instanceName")]
    public string InstanceName { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("startedAt")]
    public string StartedAt { get; set; } = "";

    [JsonPropertyName("ownershipMode")]
    public string OwnershipMode { get; set; } = "";

    [JsonPropertyName("activeOwner")]
    public bool ActiveOwner { get; set; }

    [JsonPropertyName("components")]
    public List<BridgeProductionComponentStatus> Components { get; set; } = [];
}

internal sealed class BridgeProductionComponentStatus
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}

internal sealed class BridgeProductionControlStatus
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("hostKind")]
    public string HostKind { get; set; } = "";

    [JsonPropertyName("managementApiVersion")]
    public int ManagementApiVersion { get; set; }

    [JsonPropertyName("instanceName")]
    public string InstanceName { get; set; } = "";

    [JsonPropertyName("lifecycle")]
    public string Lifecycle { get; set; } = "";

    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("ownershipMode")]
    public string OwnershipMode { get; set; } = "";

    [JsonPropertyName("activeOwner")]
    public bool ActiveOwner { get; set; }

    [JsonPropertyName("businessState")]
    public BridgeProductionBusinessStatus BusinessState { get; set; } = new();

    [JsonPropertyName("boundaries")]
    public BridgeProductionBoundaryStatus Boundaries { get; set; } = new();
}

internal sealed class BridgeProductionBusinessStatus
{
    [JsonPropertyName("initialized")]
    public bool Initialized { get; set; }

    [JsonPropertyName("sessions")]
    public int Sessions { get; set; }

    [JsonPropertyName("activeSessions")]
    public int ActiveSessions { get; set; }

    [JsonPropertyName("endedSessions")]
    public int EndedSessions { get; set; }

    [JsonPropertyName("approvals")]
    public int Approvals { get; set; }

    [JsonPropertyName("pendingApprovals")]
    public int PendingApprovals { get; set; }

    [JsonPropertyName("inputs")]
    public int Inputs { get; set; }

    [JsonPropertyName("pendingInputs")]
    public int PendingInputs { get; set; }
}

internal sealed class BridgeProductionBoundaryStatus
{
    [JsonPropertyName("feishuAdapter")]
    public BridgeProductionFeishuAdapterStatus FeishuAdapter { get; set; } = new();
}

internal sealed class BridgeProductionFeishuAdapterStatus
{
    [JsonPropertyName("liveEventStreamEnabled")]
    public bool LiveEventStreamEnabled { get; set; }

    [JsonPropertyName("outboundMessagingEnabled")]
    public bool OutboundMessagingEnabled { get; set; }
}

internal sealed class BridgeProductionPresenceStatus
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("sessions")]
    public List<BridgeProductionSessionPresence> Sessions { get; set; } = [];
}

internal sealed class BridgeProductionSessionPresence
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("managedTerminalOnline")]
    public bool ManagedTerminalOnline { get; set; }

    [JsonPropertyName("managedTerminalReady")]
    public bool ManagedTerminalReady { get; set; }
}
