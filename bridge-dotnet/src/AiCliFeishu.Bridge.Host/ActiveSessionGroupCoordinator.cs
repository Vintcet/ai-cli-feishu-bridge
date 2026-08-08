using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

/// <summary>
/// Owns assistant-created Feishu session groups in the reserved Active graph.
/// Group ordinals and every binding/error transition are committed through the
/// persistent business-state writer before they are exposed to notification
/// callers. A caller cancellation never abandons an already-created remote
/// group between the Feishu side effect and its durable binding/compensation.
/// </summary>
internal sealed class ActiveSessionGroupCoordinator :
    IBridgeActiveSessionGroupCoordinator,
    IBridgeHostSubsystem,
    IBridgeHostSubsystemHealth,
    IDisposable
{
    private const int MaximumErrorLength = 500;
    private static readonly TimeSpan DefaultInactiveAge = TimeSpan.FromDays(7);
    private readonly object sync = new();
    private readonly BridgeHostOptions options;
    private readonly IBridgeProductionStoreOwner storeOwner;
    private readonly IBridgeActiveSessionGroupStateOwner stateOwner;
    private readonly IFeishuGateway gateway;
    private readonly TimeProvider clock;
    private readonly TimeSpan inactiveAge;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Task<SessionStoreRecord?>> creates =
        new(StringComparer.Ordinal);
    private readonly HashSet<Task> workers = [];
    private bool started;
    private bool disposed;
    private int created;
    private int renamed;
    private int failures;

    public ActiveSessionGroupCoordinator(
        BridgeHostOptions options,
        IBridgeProductionStoreOwner storeOwner,
        IBridgeActiveSessionGroupStateOwner stateOwner,
        IFeishuGateway gateway,
        TimeProvider? timeProvider = null,
        TimeSpan? inactiveAge = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.storeOwner = storeOwner ?? throw new ArgumentNullException(nameof(storeOwner));
        this.stateOwner = stateOwner ?? throw new ArgumentNullException(nameof(stateOwner));
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        clock = timeProvider ?? TimeProvider.System;
        this.inactiveAge = inactiveAge ?? DefaultInactiveAge;
        if (this.inactiveAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inactiveAge),
                "会话群不活跃期限必须大于零。");
        }
    }

    public string Name => "active-session-groups";

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            lock (sync)
            {
                return new(
                    Name,
                    started ? "ready" : "starting",
                    $"pending={creates.Count} workers={workers.Count} " +
                    $"created={created} renamed={renamed} failed={failures}");
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureActive();
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
            await InitializeAsync(cancellationToken);
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
        _ = cancellationToken;
        Task[] pending;
        lock (sync)
        {
            if (!started)
            {
                return;
            }
            started = false;
            lifetime.Cancel();
            pending = workers.ToArray();
        }
        try
        {
            await Task.WhenAll(pending);
        }
        catch
        {
            // Create/rename failures have already been persisted or compensated.
            // Shutdown only joins the bounded operations before Store/credentials
            // owners are stopped in reverse subsystem order.
        }
        lock (sync)
        {
            creates.Clear();
            workers.Clear();
        }
    }

    public async ValueTask<SessionStoreRecord?> EnsureAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureStarted();

        var store = await storeOwner.ReadAsync(cancellationToken);
        if (!store.Sessions.Sessions.TryGetValue(sessionId, out var session) ||
            !ExtensionBoolean(session, "managedByAssistant"))
        {
            return session;
        }
        if (ExtensionString(session, "feishuChatId") is not null)
        {
            var numbered = await stateOwner.EnsureSessionGroupOrdinalAsync(
                sessionId,
                cancellationToken);
            return numbered.Session ?? session;
        }
        if (ExtensionString(session, "feishuChatError") is not null ||
            string.IsNullOrWhiteSpace(store.Bindings.OwnerOpenId))
        {
            return session;
        }

        Task<SessionStoreRecord?> operation;
        lock (sync)
        {
            EnsureStartedLocked();
            if (!creates.TryGetValue(sessionId, out operation!))
            {
                operation = CreateAsync(sessionId, lifetime.Token);
                creates.Add(sessionId, operation);
                workers.Add(operation);
                _ = ObserveCreateAsync(sessionId, operation);
            }
        }
        return await operation.WaitAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<string>> NotificationChatsAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _ = await EnsureAsync(sessionId, cancellationToken);
        var store = await storeOwner.ReadAsync(cancellationToken);
        if (store.Sessions.Sessions.TryGetValue(sessionId, out var session) &&
            ExtensionBoolean(session, "managedByAssistant") &&
            ExtensionString(session, "feishuChatId") is { } sessionChat)
        {
            return [sessionChat];
        }
        return store.Bindings.OwnerOpenId is { } ownerOpenId &&
            store.Bindings.Users.TryGetValue(ownerOpenId, out var binding) &&
            !string.IsNullOrWhiteSpace(binding.ChatId)
                ? [binding.ChatId]
                : [];
    }

    public void ScheduleEnsure(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        EnsureStarted();
        var worker = EnsureScheduledAsync(sessionId);
        lock (sync)
        {
            EnsureStartedLocked();
            workers.Add(worker);
        }
        _ = ObserveWorkerAsync(worker);
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
            lifetime.Cancel();
            lifetime.Dispose();
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var store = await storeOwner.ReadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(store.Bindings.OwnerOpenId))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var sessions = store.Sessions.Sessions.Values
            .Where(session =>
                !string.Equals(
                    session.Status,
                    SessionStatuses.Ended,
                    StringComparison.Ordinal) &&
                ExtensionBoolean(session, "managedByAssistant") &&
                (ExtensionString(session, "feishuChatId") is not null ||
                 now - SessionGroupActivityTime(session) < inactiveAge))
            .OrderBy(SessionOpenedAt)
            .ThenBy(session => session.SessionId, StringComparer.Ordinal)
            .Select(session => session.SessionId)
            .ToArray();

        foreach (var sessionId in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var numbered = await stateOwner.EnsureSessionGroupOrdinalAsync(
                sessionId,
                cancellationToken);
            if (!numbered.Succeeded)
            {
                continue;
            }
            if (ExtensionString(numbered.Session!, "feishuChatId") is not null)
            {
                await RenameExistingBestEffortAsync(
                    numbered.Session!,
                    cancellationToken);
                continue;
            }
            _ = await EnsureAsync(sessionId, cancellationToken);
        }
    }

    private async Task<SessionStoreRecord?> CreateAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var prepared = await stateOwner.EnsureSessionGroupOrdinalAsync(
            sessionId,
            cancellationToken);
        if (!prepared.Succeeded ||
            ExtensionPositiveInteger(
                prepared.Session!,
                "feishuChatOrdinal") is not { } ordinal)
        {
            return prepared.Session;
        }

        var store = await storeOwner.ReadAsync(cancellationToken);
        if (!store.Sessions.Sessions.TryGetValue(sessionId, out var session) ||
            !ExtensionBoolean(session, "managedByAssistant") ||
            ExtensionPositiveInteger(session, "feishuChatOrdinal") != ordinal)
        {
            return session;
        }
        var ownerOpenId = store.Bindings.OwnerOpenId;
        if (ExtensionString(session, "feishuChatId") is not null ||
            ExtensionString(session, "feishuChatError") is not null ||
            string.IsNullOrWhiteSpace(ownerOpenId))
        {
            return session;
        }

        var name = GroupName(session, ordinal);
        FeishuSessionGroup group;
        try
        {
            group = await gateway.CreateSessionGroupAsync(
                ownerOpenId,
                name,
                $"{RuntimeDisplayName(session.Runtime)} 会话 {ShortId(session)} · {session.Cwd}",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            var detail = TruncateError(error);
            var failed = await stateOwner.RecordSessionGroupErrorAsync(
                sessionId,
                ordinal,
                ownerOpenId,
                detail,
                clock.GetUtcNow(),
                CancellationToken.None);
            lock (sync)
            {
                failures++;
            }
            return failed.Session ?? session;
        }

        BridgeSessionGroupNameUpdateResult bound;
        try
        {
            bound = await stateOwner.BindSessionGroupAsync(
                sessionId,
                ordinal,
                ownerOpenId,
                group.ChatId,
                group.Name,
                clock.GetUtcNow(),
                CancellationToken.None);
        }
        catch
        {
            await DeleteCreatedGroupBestEffortAsync(group.ChatId);
            throw;
        }
        if (!bound.Succeeded)
        {
            await DeleteCreatedGroupBestEffortAsync(group.ChatId);
            lock (sync)
            {
                failures++;
            }
            return bound.Session ?? session;
        }

        lock (sync)
        {
            created++;
        }
        await RenameExistingBestEffortAsync(
            bound.Session!,
            cancellationToken);
        try
        {
            _ = await gateway.SendTextAsync(
                group.ChatId,
                $"已连接到 {SessionLabel(bound.Session!)}。" +
                $"以后这个群里的消息都会发送到对应 {RuntimeDisplayName(bound.Session!.Runtime)} 窗口。",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The durable binding is already complete. A shutdown cancellation
            // only skips the optional welcome text.
        }
        catch
        {
            // Welcome delivery is best effort and must not roll back a valid group.
        }
        return bound.Session;
    }

    private async Task RenameExistingBestEffortAsync(
        SessionStoreRecord session,
        CancellationToken cancellationToken)
    {
        var chatId = ExtensionString(session, "feishuChatId");
        var ordinal = ExtensionPositiveInteger(session, "feishuChatOrdinal");
        if (chatId is null || ordinal is null)
        {
            return;
        }
        var name = GroupName(session, ordinal.Value);
        if (string.Equals(
                ExtensionString(session, "feishuChatName"),
                name,
                StringComparison.Ordinal))
        {
            return;
        }
        try
        {
            await gateway.UpdateSessionGroupNameAsync(
                chatId,
                name,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            lock (sync)
            {
                failures++;
            }
            return;
        }

        var updated = await stateOwner.UpdateSessionGroupNameAsync(
            session.SessionId,
            chatId,
            name,
            cancellationToken);
        lock (sync)
        {
            if (updated.Succeeded)
            {
                renamed++;
            }
            else
            {
                failures++;
            }
        }
    }

    private async Task DeleteCreatedGroupBestEffortAsync(string chatId)
    {
        try
        {
            await gateway.DeleteSessionGroupAsync(chatId, CancellationToken.None);
        }
        catch
        {
            // The Store remains unbound. A later cleanup/audit slice can surface
            // an API-side orphan, but this coordinator must never bind stale data.
        }
    }

    private async Task EnsureScheduledAsync(string sessionId)
    {
        try
        {
            _ = await EnsureAsync(sessionId, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            lock (sync)
            {
                failures++;
            }
        }
    }

    private async Task ObserveCreateAsync(
        string sessionId,
        Task<SessionStoreRecord?> operation)
    {
        try
        {
            _ = await operation;
        }
        catch
        {
            // The awaiting caller or scheduled wrapper observes the failure.
        }
        finally
        {
            lock (sync)
            {
                if (creates.GetValueOrDefault(sessionId) == operation)
                {
                    creates.Remove(sessionId);
                }
                workers.Remove(operation);
            }
        }
    }

    private async Task ObserveWorkerAsync(Task worker)
    {
        try
        {
            await worker;
        }
        catch
        {
        }
        finally
        {
            lock (sync)
            {
                workers.Remove(worker);
            }
        }
    }

    private void EnsureActive()
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "会话群协调器只能用于 Active Host。");
        }
    }

    private void EnsureStarted()
    {
        lock (sync)
        {
            EnsureStartedLocked();
        }
    }

    private void EnsureStartedLocked()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started)
        {
            throw new InvalidOperationException("会话群协调器尚未初始化。");
        }
    }

    private static string GroupName(SessionStoreRecord session, int ordinal) =>
        SessionGroupNameRules.Build(
            session.Runtime,
            ExtensionString(session, "alias"),
            session.ProjectName,
            session.ShortId,
            ordinal);

    private static string SessionLabel(SessionStoreRecord session)
    {
        var project = string.IsNullOrWhiteSpace(session.ProjectName)
            ? session.Cwd
            : session.ProjectName;
        var shortId = ShortId(session);
        return ExtensionString(session, "alias") is { } alias
            ? $"@{alias} · {project} #{shortId}"
            : $"{project} #{shortId}";
    }

    private static string ShortId(SessionStoreRecord session) =>
        string.IsNullOrWhiteSpace(session.ShortId)
            ? session.SessionId[^Math.Min(8, session.SessionId.Length)..]
            : session.ShortId;

    private static string RuntimeDisplayName(string? runtime) => runtime switch
    {
        RuntimeNames.ClaudeCode => "Claude Code",
        RuntimeNames.OpenCode => "OpenCode",
        _ => "Codex",
    };

    private static DateTimeOffset SessionOpenedAt(SessionStoreRecord session) =>
        DateTimeOffset.TryParse(session.OpenedAt, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static DateTimeOffset SessionGroupActivityTime(
        SessionStoreRecord session)
    {
        var lastSeenAt = DateTimeOffset.TryParse(session.LastSeenAt, out var seen)
            ? seen
            : DateTimeOffset.MinValue;
        var createdAt = DateTimeOffset.TryParse(
            ExtensionString(session, "feishuChatCreatedAt"),
            out var created)
                ? created
                : DateTimeOffset.MinValue;
        return lastSeenAt >= createdAt ? lastSeenAt : createdAt;
    }

    private static string TruncateError(Exception error)
    {
        var detail = string.IsNullOrWhiteSpace(error.Message)
            ? error.GetType().Name
            : error.Message;
        if (detail.Length <= MaximumErrorLength)
        {
            return detail;
        }
        var length = 0;
        foreach (var rune in detail.EnumerateRunes())
        {
            if (length + rune.Utf16SequenceLength > MaximumErrorLength)
            {
                break;
            }
            length += rune.Utf16SequenceLength;
        }
        return detail[..length];
    }

    private static bool ExtensionBoolean(
        ExtensibleStoreObject value,
        string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.Any(item =>
            string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase) &&
            item.Value.ValueKind is JsonValueKind.True);

    private static string? ExtensionString(
        ExtensibleStoreObject value,
        string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.FirstOrDefault(item => string.Equals(
            item.Key,
            name,
            StringComparison.OrdinalIgnoreCase))
            is { Value.ValueKind: JsonValueKind.String } item &&
        !string.IsNullOrWhiteSpace(item.Value.GetString())
            ? item.Value.GetString()!.Trim()
            : null;

    private static int? ExtensionPositiveInteger(
        ExtensibleStoreObject value,
        string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.FirstOrDefault(item => string.Equals(
            item.Key,
            name,
            StringComparison.OrdinalIgnoreCase))
            is { Value.ValueKind: JsonValueKind.Number } item &&
        item.Value.TryGetInt32(out var number) &&
        number > 0
            ? number
            : null;
}
