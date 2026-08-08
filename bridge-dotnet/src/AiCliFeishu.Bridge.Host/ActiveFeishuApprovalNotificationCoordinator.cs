using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal interface IBridgeActiveApprovalNotifier
{
    Task NotifyPendingAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task SynchronizeAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task SynchronizeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}

internal sealed class ActiveFeishuApprovalNotificationCoordinator(
    IBridgeActiveApprovalStateOwner stateOwner,
    IBridgeProductionStoreOwner storeOwner,
    IFeishuGateway gateway,
    IFeishuCardRenderer renderer,
    FeishuInteractionCoordinator interactions,
    IBridgeActiveSessionGroupCoordinator sessionGroups)
    : IBridgeActiveApprovalNotifier,
      IBridgeHostSubsystem,
      IBridgeHostSubsystemHealth,
      IBridgeBackgroundSubsystem,
      IDisposable
{
    private static readonly TimeSpan SynchronizationInterval = TimeSpan.FromSeconds(30);
    private readonly object sync = new();
    private readonly SemaphoreSlim synchronizationGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Task? synchronizationLoop;
    private bool started;
    private bool disposed;
    private int synchronizationRuns;
    private int synchronizationFailures;

    public string Name => "active-feishu-approval-notifications";

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            lock (sync)
            {
                return new(
                    Name,
                    started ? "ready" : "starting",
                    $"runs={synchronizationRuns} failed={synchronizationFailures}");
            }
        }
    }

    public Task? Completion
    {
        get
        {
            lock (sync)
            {
                return synchronizationLoop;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started)
            {
                return;
            }
            started = true;
        }

        try
        {
            await SynchronizeAllBestEffortAsync(cancellationToken);
            lock (sync)
            {
                synchronizationLoop = RunSynchronizationLoopAsync(lifetime.Token);
            }
        }
        catch
        {
            lock (sync)
            {
                started = false;
            }
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? loop;
        lock (sync)
        {
            if (!started)
            {
                return;
            }
            started = false;
            lifetime.Cancel();
            loop = synchronizationLoop;
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
        lock (sync)
        {
            synchronizationLoop = null;
        }
    }

    public async Task NotifyPendingAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var current = stateOwner.Snapshot;
        if (!TryPending(current, requestId, sessionId, out var approval, out var session) ||
            approval.MessageIds.Count > 0)
        {
            return;
        }

        var store = await storeOwner.ReadAsync(cancellationToken);
        if (!store.Sessions.Sessions.TryGetValue(sessionId, out var storedSession) ||
            !string.Equals(session.Runtime, Runtime(storedSession), StringComparison.Ordinal) ||
            !string.Equals(session.Cwd, storedSession.Cwd, StringComparison.Ordinal))
        {
            return;
        }

        var chats = await sessionGroups.NotificationChatsAsync(
            sessionId,
            cancellationToken);
        if (chats.Count == 0)
        {
            return;
        }

        var sessionView = SessionView(session, storedSession);
        var approvalView = ApprovalView(approval, store);
        var card = renderer.PendingApproval(sessionView, approvalView);
        foreach (var chatId in chats
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.Ordinal))
        {
            current = stateOwner.Snapshot;
            if (!TryPending(current, requestId, sessionId, out approval, out _) ||
                approval.MessageIds.Count > 0)
            {
                return;
            }

            var messageId = await gateway.SendCardAsync(
                chatId,
                card,
                NotificationKey(requestId, chatId),
                cancellationToken);
            if (string.IsNullOrWhiteSpace(messageId))
            {
                throw new InvalidOperationException("飞书审批卡片未返回消息 ID。");
            }

            var delivered = await stateOwner.RecordApprovalDeliveryAsync(
                requestId,
                sessionId,
                messageId,
                chatId,
                DateTimeOffset.UtcNow,
                cancellationToken);
            if (delivered is not null && IsTerminal(delivered.Approval))
            {
                await interactions.SynchronizeApprovalAsync(
                    delivered.Approval,
                    sessionView,
                    approvalView,
                    cancellationToken);
            }
        }
    }

    public async Task SynchronizeAsync(
        string requestId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var current = stateOwner.Snapshot;
        if (!TryTerminal(current, requestId, sessionId, out var approval, out var session))
        {
            return;
        }
        var store = await storeOwner.ReadAsync(cancellationToken);
        if (!TryStoredSession(session, store, out var storedSession))
        {
            return;
        }
        await interactions.SynchronizeApprovalAsync(
            approval,
            SessionView(session, storedSession),
            ApprovalView(approval, store),
            cancellationToken);
    }

    public async Task SynchronizeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var current = stateOwner.Snapshot;
        if (!current.Initialized ||
            !current.Sessions.Sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }
        var approvals = current.Approvals.Requests.Values
            .Where(approval =>
                string.Equals(approval.SessionId, sessionId, StringComparison.Ordinal) &&
                IsTerminal(approval))
            .ToArray();
        if (approvals.Length == 0)
        {
            return;
        }
        var store = await storeOwner.ReadAsync(cancellationToken);
        if (!TryStoredSession(session, store, out var storedSession))
        {
            return;
        }

        Exception? firstFailure = null;
        var sessionView = SessionView(session, storedSession);
        foreach (var approval in approvals)
        {
            try
            {
                await interactions.SynchronizeApprovalAsync(
                    approval,
                    sessionView,
                    ApprovalView(approval, store),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                Interlocked.Increment(ref synchronizationFailures);
                firstFailure ??= error;
            }
        }
        if (firstFailure is not null)
        {
            throw firstFailure;
        }
    }

    private async Task RunSynchronizationLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SynchronizationInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await SynchronizeAllBestEffortAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SynchronizeAllBestEffortAsync(CancellationToken cancellationToken)
    {
        await synchronizationGate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Increment(ref synchronizationRuns);
            var current = stateOwner.Snapshot;
            if (!current.Initialized)
            {
                return;
            }

            NodeStoreSnapshot store;
            try
            {
                store = await storeOwner.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                Interlocked.Increment(ref synchronizationFailures);
                return;
            }

            foreach (var approval in current.Approvals.Requests.Values.Where(IsTerminal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!current.Sessions.Sessions.TryGetValue(
                        approval.SessionId,
                        out var session) ||
                    !TryStoredSession(session, store, out var storedSession))
                {
                    continue;
                }
                try
                {
                    await interactions.SynchronizeApprovalAsync(
                        approval,
                        SessionView(session, storedSession),
                        ApprovalView(approval, store),
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    Interlocked.Increment(ref synchronizationFailures);
                }
            }
        }
        finally
        {
            synchronizationGate.Release();
        }
    }

    private static bool TryPending(
        BridgeBusinessStateSnapshot current,
        string requestId,
        string sessionId,
        out ApprovalState approval,
        out SessionState session)
    {
        if (current.Initialized &&
            current.Approvals.Requests.TryGetValue(requestId, out approval!) &&
            approval.Status == ApprovalStatuses.Pending &&
            string.Equals(approval.SessionId, sessionId, StringComparison.Ordinal) &&
            current.Sessions.Sessions.TryGetValue(sessionId, out session!))
        {
            return true;
        }
        approval = null!;
        session = null!;
        return false;
    }

    private static bool TryTerminal(
        BridgeBusinessStateSnapshot current,
        string requestId,
        string sessionId,
        out ApprovalState approval,
        out SessionState session)
    {
        if (current.Initialized &&
            current.Approvals.Requests.TryGetValue(requestId, out approval!) &&
            string.Equals(approval.SessionId, sessionId, StringComparison.Ordinal) &&
            IsTerminal(approval) &&
            current.Sessions.Sessions.TryGetValue(sessionId, out session!))
        {
            return true;
        }
        approval = null!;
        session = null!;
        return false;
    }

    private static bool IsTerminal(ApprovalState approval) =>
        approval.Status is ApprovalStatuses.Resolved or ApprovalStatuses.Orphaned &&
        approval.Resolution is not null &&
        approval.MessageIds.Count > 0;

    private static bool TryStoredSession(
        SessionState session,
        NodeStoreSnapshot store,
        out SessionStoreRecord storedSession)
    {
        if (store.Sessions.Sessions.TryGetValue(session.SessionId, out storedSession!) &&
            string.Equals(session.Runtime, Runtime(storedSession), StringComparison.Ordinal) &&
            string.Equals(session.Cwd, storedSession.Cwd, StringComparison.Ordinal))
        {
            return true;
        }
        storedSession = null!;
        return false;
    }

    private static FeishuSessionView SessionView(
        SessionState session,
        SessionStoreRecord stored) => new(
            session.SessionId,
            session.Runtime,
            ExtensionString(stored.ExtensionData, "alias") ??
                stored.ProjectName ??
                stored.ShortId ??
                ShortId(stored.SessionId),
            session.Cwd,
            ExtensionBoolean(stored.ExtensionData, "managedByAssistant"));

    private static FeishuApprovalView ApprovalView(
        ApprovalState approval,
        NodeStoreSnapshot store)
    {
        var stored = store.Approvals.Requests.GetValueOrDefault(approval.RequestId);
        return new(
            approval.RequestId,
            approval.ToolName,
            approval.ToolPreview,
            ExtensionString(stored?.ExtensionData, "riskLevel") ?? "normal",
            ExtensionString(stored?.ExtensionData, "riskReason"));
    }

    private static string NotificationKey(string requestId, string chatId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{requestId}\0approval\0{chatId}"));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    private static string Runtime(SessionStoreRecord session) =>
        string.IsNullOrWhiteSpace(session.Runtime)
            ? RuntimeNames.Codex
            : session.Runtime;

    private static string? ExtensionString(
        Dictionary<string, JsonElement>? extensions,
        string name) =>
        extensions is not null &&
        extensions.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static bool ExtensionBoolean(
        Dictionary<string, JsonElement>? extensions,
        string name) =>
        extensions is not null &&
        extensions.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static string ShortId(string sessionId)
    {
        var compact = new string(sessionId.Where(char.IsLetterOrDigit).ToArray());
        var source = compact.Length == 0 ? sessionId : compact;
        return source[^Math.Min(8, source.Length)..].ToLowerInvariant();
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
        }
        lifetime.Cancel();
        lifetime.Dispose();
        synchronizationGate.Dispose();
    }
}
