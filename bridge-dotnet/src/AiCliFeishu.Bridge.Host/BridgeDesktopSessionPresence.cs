using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

public sealed record BridgeDesktopSessionPresence(
    string SessionId,
    bool ManagedTerminalOnline,
    bool ManagedTerminalReady);

public sealed record BridgeDesktopSessionPresenceSnapshot(
    bool Ok,
    IReadOnlyList<BridgeDesktopSessionPresence> Sessions);

internal sealed record BridgeDesktopSessionHeartbeat(
    string SessionId,
    int ClientProcessId,
    string? ClientProcessStartedAt);

internal sealed class BridgeDesktopSessionHeartbeatDirectory(
    Func<int, string?, bool>? processProbe = null)
{
    private readonly ConcurrentDictionary<string, BridgeDesktopSessionHeartbeat> heartbeats =
        new(StringComparer.Ordinal);
    private readonly Func<int, string?, bool> processProbe =
        processProbe ?? BridgeAssistantProcessProbe.IsOnline;

    public (BridgeDesktopSessionHeartbeat Heartbeat, bool Changed) Record(
        JsonElement payload)
    {
        if (payload.ValueKind is not JsonValueKind.Object ||
            !payload.TryGetProperty("session_id", out var sessionElement) ||
            sessionElement.ValueKind is not JsonValueKind.String ||
            sessionElement.GetString() is not { } sessionId ||
            string.IsNullOrWhiteSpace(sessionId) ||
            sessionId.Length > 256 ||
            sessionId.Any(char.IsControl))
        {
            throw new InvalidDataException("本机会话在线登记缺少有效的 session_id。");
        }
        if (!payload.TryGetProperty("client_process_id", out var processElement) ||
            processElement.ValueKind is not JsonValueKind.Number ||
            !processElement.TryGetInt32(out var processId) ||
            processId <= 0)
        {
            throw new InvalidDataException("本机会话在线登记缺少有效的客户端进程 ID。");
        }

        string? startedAt = null;
        if (payload.TryGetProperty("client_process_started_at", out var startedElement))
        {
            if (startedElement.ValueKind is not JsonValueKind.String ||
                startedElement.GetString() is not { } value ||
                !DateTimeOffset.TryParse(value, out _))
            {
                throw new InvalidDataException("本机会话在线登记的客户端启动时间无效。");
            }
            startedAt = value;
        }
        if (!processProbe(processId, startedAt))
        {
            throw new InvalidDataException("本机会话在线登记指向的助手进程不存在或身份不匹配。");
        }

        var heartbeat = new BridgeDesktopSessionHeartbeat(
            sessionId,
            processId,
            startedAt);
        var changed = true;
        heartbeats.AddOrUpdate(
            sessionId,
            _ => heartbeat,
            (_, current) =>
            {
                changed = current != heartbeat;
                return heartbeat;
            });
        return (heartbeat, changed);
    }

    public bool IsOnline(string sessionId)
    {
        if (!heartbeats.TryGetValue(sessionId, out var heartbeat))
        {
            return false;
        }
        if (processProbe(
                heartbeat.ClientProcessId,
                heartbeat.ClientProcessStartedAt))
        {
            return true;
        }
        _ = ((ICollection<KeyValuePair<string, BridgeDesktopSessionHeartbeat>>)heartbeats)
            .Remove(new(sessionId, heartbeat));
        return false;
    }
}

public static class BridgeDesktopSessionPresenceProjection
{
    private static readonly TimeSpan FallbackLifetime = TimeSpan.FromMinutes(5);

    public static BridgeDesktopSessionPresenceSnapshot Project(
        BridgeStoreSnapshot store,
        IManagedTerminalDirectory terminals,
        IOpenCodeEndpointDirectory openCode,
        DateTimeOffset now,
        Func<int, string?, bool>? processProbe = null,
        Func<string, bool>? heartbeatProbe = null,
        TimeSpan? sessionActiveLifetime = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(terminals);
        ArgumentNullException.ThrowIfNull(openCode);
        processProbe ??= BridgeAssistantProcessProbe.IsOnline;
        heartbeatProbe ??= _ => false;
        var fallbackLifetime = Min(
            sessionActiveLifetime is { } configured && configured > TimeSpan.Zero
                ? configured
                : TimeSpan.FromDays(1),
            FallbackLifetime);

        var sessions = new List<BridgeDesktopSessionPresence>();
        foreach (var session in store.Sessions.Sessions.Values
                     .Where(session =>
                         !string.IsNullOrWhiteSpace(session.SessionId) &&
                         !string.Equals(session.Status, "ended", StringComparison.Ordinal))
                     .OrderBy(session => session.SessionId, StringComparer.Ordinal))
        {
            if (string.Equals(session.Runtime, "opencode", StringComparison.Ordinal))
            {
                if (openCode.FindBySession(session.SessionId) is not null)
                {
                    sessions.Add(new(session.SessionId, false, false));
                }
                continue;
            }

            if (ExtensionString(session, "managedTerminalId").Length > 0)
            {
                var target = terminals.FindBySession(session.SessionId);
                if (target is not null)
                {
                    sessions.Add(new(session.SessionId, true, target.Ready));
                }
                continue;
            }

            if (TryExtensionInt32(session, "clientProcessId", out var processId))
            {
                if (processProbe(
                        processId,
                        ExtensionString(session, "clientProcessStartedAt")) ||
                    heartbeatProbe(session.SessionId))
                {
                    sessions.Add(new(session.SessionId, false, false));
                }
                continue;
            }

            if (heartbeatProbe(session.SessionId))
            {
                sessions.Add(new(session.SessionId, false, false));
                continue;
            }

            if (DateTimeOffset.TryParse(session.LastSeenAt, out var lastSeenAt) &&
                lastSeenAt <= now + TimeSpan.FromSeconds(1) &&
                now - lastSeenAt <= fallbackLifetime)
            {
                sessions.Add(new(session.SessionId, false, false));
            }
        }
        return new(true, sessions);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) =>
        left <= right ? left : right;

    private static bool TryExtensionInt32(
        SessionStoreRecord session,
        string name,
        out int value)
    {
        value = 0;
        return TryExtension(session, name, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out value) &&
            value > 0;
    }

    private static string ExtensionString(SessionStoreRecord session, string name)
    {
        if (!TryExtension(session, name, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }
        return element.GetString() ?? string.Empty;
    }

    private static bool TryExtension(
        SessionStoreRecord session,
        string name,
        out JsonElement value)
    {
        value = default;
        if (session.ExtensionData is null)
        {
            return false;
        }
        if (session.ExtensionData.TryGetValue(name, out value))
        {
            return true;
        }
        foreach (var item in session.ExtensionData)
        {
            if (string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = item.Value;
                return true;
            }
        }
        return false;
    }
}

internal static class BridgeAssistantProcessProbe
{
    public static bool IsOnline(int processId, string? expectedStartedAt)
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
}
