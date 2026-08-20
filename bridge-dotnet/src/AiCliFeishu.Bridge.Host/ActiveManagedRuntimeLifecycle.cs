using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed record BridgeManagedRuntimeLaunchRequest(
    string RequestId,
    string Kind,
    string SessionId,
    string Runtime,
    string Cwd,
    string? ProjectName,
    bool Elevated,
    DateTimeOffset CreatedAt);

internal sealed record BridgeManagedRuntimeLaunchCompletion(
    string? RequestId,
    bool? Success,
    string? Error);

internal sealed record BridgeManagedRuntimeLaunchCompletionResult(
    bool Ok,
    string? Kind = null,
    string? SessionId = null,
    bool AlreadyResolved = false,
    string? Error = null,
    string? FailureDetail = null);

internal sealed record BridgeManagedRuntimeLifecycleSnapshot(
    int Pending,
    int Claimed,
    int Launched,
    int QueuedPrompts);

internal interface IBridgeManagedRuntimeLaunchCoordinator
{
    BridgeManagedRuntimeLifecycleSnapshot Snapshot { get; }

    BridgeManagedRuntimeLaunchRequest? Claim();

    BridgeManagedRuntimeLaunchCompletionResult Complete(
        BridgeManagedRuntimeLaunchCompletion completion);

    Task DrainAsync(
        string sessionExternalId,
        CancellationToken cancellationToken = default);
}

internal sealed class ActiveManagedRuntimeLifecycle :
    IManagedRuntimeLifecycle,
    IBridgeManagedRuntimeLaunchCoordinator,
    IDisposable
{
    private const string NewKind = "new";
    private const string ResumeKind = "resume";

    // A claimed request keeps the queued Feishu prompt alive, so this has to outlive
    // the desktop launch wait (the panel polls a managed terminal for up to 300 s).
    // Observed Codex startups already reach 135 s, and with a shorter lifetime
    // PruneExpiredLocked would silently drop the prompt that asked for the launch.
    internal static readonly TimeSpan DefaultRequestLifetime = TimeSpan.FromMinutes(6);

    // The desktop launch wait this lifetime must cover:
    // ManagedTerminalLaunchWaiter.DefaultMaximumAttempts (1200) * 250 ms.
    internal static readonly TimeSpan DesktopLaunchWait = TimeSpan.FromSeconds(300);

    private readonly object sync = new();
    private readonly BridgeHostOptions options;
    private readonly IBridgeProductionStoreOwner storeOwner;
    private readonly IManagedTerminalDirectory terminals;
    private readonly IManagedTerminalTransport terminalTransport;
    private readonly TimeProvider clock;
    private readonly TimeSpan requestLifetime;
    private readonly Func<string> requestIdFactory;
    private readonly Dictionary<string, LaunchRequest> requests =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> requestIdsBySession =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, LaunchRequest> launchedBySession =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PendingPrompt>> promptsBySession =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DrainQueue> drainQueues =
        new(StringComparer.Ordinal);
    private long nextSequence;
    private bool disposed;

    public ActiveManagedRuntimeLifecycle(
        BridgeHostOptions options,
        IBridgeProductionStoreOwner storeOwner,
        IManagedTerminalDirectory terminals,
        IManagedTerminalTransport terminalTransport)
        : this(
            options,
            storeOwner,
            terminals,
            terminalTransport,
            TimeProvider.System,
            ConfiguredLifetime(options, "RUNTIME_AUTO_LAUNCH_TIMEOUT_MS", DefaultRequestLifetime))
    {
    }

    internal ActiveManagedRuntimeLifecycle(
        BridgeHostOptions options,
        IBridgeProductionStoreOwner storeOwner,
        IManagedTerminalDirectory terminals,
        IManagedTerminalTransport terminalTransport,
        TimeProvider clock,
        TimeSpan requestLifetime,
        Func<string>? requestIdFactory = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.storeOwner = storeOwner ?? throw new ArgumentNullException(nameof(storeOwner));
        this.terminals = terminals ?? throw new ArgumentNullException(nameof(terminals));
        this.terminalTransport = terminalTransport ??
            throw new ArgumentNullException(nameof(terminalTransport));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.requestLifetime = requestLifetime > TimeSpan.Zero
            ? requestLifetime
            : throw new ArgumentOutOfRangeException(nameof(requestLifetime));
        this.requestIdFactory = requestIdFactory ??
            (() => Guid.NewGuid().ToString("D"));
    }

    public BridgeManagedRuntimeLifecycleSnapshot Snapshot
    {
        get
        {
            lock (sync)
            {
                ThrowIfDisposedLocked();
                PruneExpiredLocked(clock.GetUtcNow());
                return new(
                    requests.Values.Count(request =>
                        request.Status is LaunchStatus.Pending),
                    requests.Values.Count(request =>
                        request.Status is LaunchStatus.Claimed),
                    requests.Values.Count(request =>
                        request.Status is LaunchStatus.Launched) +
                        launchedBySession.Count,
                    promptsBySession.Values.Sum(queue => queue.Count));
            }
        }
    }

    public Task LaunchAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string cwd,
        string? prompt,
        bool elevated,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        runtime = RequireRuntime(runtime);
        sessionExternalId = RequireSessionId(sessionExternalId);
        cwd = NormalizeCwd(cwd);
        prompt = ValidatePrompt(prompt);
        Publish(
            context,
            NewKind,
            runtime,
            sessionExternalId,
            cwd,
            ProjectName(cwd),
            prompt,
            elevated);
        return Task.CompletedTask;
    }

    public async Task ResumeAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string? cwd,
        string? prompt,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        runtime = RequireRuntime(runtime);
        sessionExternalId = RequireSessionId(sessionExternalId);
        prompt = ValidatePrompt(prompt);

        var store = await storeOwner.ReadAsync(cancellationToken);
        var session = FindSession(store.Sessions, sessionExternalId);
        if (string.Equals(session.Status, "ended", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("已经结束的托管终端会话不能恢复。");
        }
        var persistedRuntime = session.Runtime ?? RuntimeNames.Codex;
        if (!string.Equals(persistedRuntime, runtime, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("恢复请求与生产 Store 的运行时不一致。");
        }
        var persistedCwd = NormalizeCwd(session.Cwd);
        if (cwd is not null && !CwdEquals(persistedCwd, NormalizeCwd(cwd)))
        {
            throw new InvalidOperationException("恢复请求与生产 Store 的工作目录不一致。");
        }
        var elevated = OptionalBooleanExtension(session, "managedTerminalElevated") ?? false;
        Publish(
            context,
            ResumeKind,
            runtime,
            sessionExternalId,
            persistedCwd,
            session.ProjectName,
            prompt,
            elevated);
    }

    public Task StopAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        _ = RequireRuntime(runtime);
        sessionExternalId = RequireSessionId(sessionExternalId);
        if (reason?.Length > 500 || reason?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("托管终端停止原因无效。", nameof(reason));
        }
        var target = terminals.FindBySession(sessionExternalId);

        lock (sync)
        {
            ThrowIfDisposedLocked();
            PruneExpiredLocked(clock.GetUtcNow());
            var request = FindRequestBySessionLocked(sessionExternalId);
            if (target is not null ||
                request is { Status: not LaunchStatus.Pending } ||
                launchedBySession.ContainsKey(sessionExternalId))
            {
                throw new NotSupportedException(
                    "托管终端已被领取或已经启动；当前协议无法证明可安全终止对应窗口。");
            }
            if (request is not null)
            {
                ClearRequestLocked(request, clearPrompts: true);
            }
            else
            {
                promptsBySession.Remove(sessionExternalId);
            }
        }
        return Task.CompletedTask;
    }

    public BridgeManagedRuntimeLaunchRequest? Claim()
    {
        EnsureActive();
        lock (sync)
        {
            ThrowIfDisposedLocked();
            PruneExpiredLocked(clock.GetUtcNow());
            var request = requests.Values
                .Where(request => request.Status is LaunchStatus.Pending)
                .OrderBy(request => request.CreatedAt)
                .ThenBy(request => request.Sequence)
                .FirstOrDefault();
            if (request is null)
            {
                return null;
            }
            request.Status = LaunchStatus.Claimed;
            return SnapshotOf(request);
        }
    }

    public BridgeManagedRuntimeLaunchCompletionResult Complete(
        BridgeManagedRuntimeLaunchCompletion completion)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(completion);
        var requestId = completion.RequestId?.Trim();
        if (string.IsNullOrEmpty(requestId) || completion.Success is null)
        {
            return new(false, Error: "自动恢复结果参数不完整。");
        }

        lock (sync)
        {
            ThrowIfDisposedLocked();
            PruneExpiredLocked(clock.GetUtcNow());
            if (!requests.TryGetValue(requestId, out var request))
            {
                return new(true, AlreadyResolved: true);
            }
            if (request.Status is LaunchStatus.Pending)
            {
                return new(false, Error: "自动恢复请求尚未被桌面助手领取。");
            }
            if (request.Status is LaunchStatus.Launched)
            {
                return new(true, SessionId: request.SessionId, AlreadyResolved: true);
            }
            if (completion.Success is false)
            {
                var detail = completion.Error?.Trim() is { Length: > 0 } supplied
                    ? supplied[..Math.Min(supplied.Length, 500)]
                    : "桌面助手未能启动对应窗口。";
                ClearRequestLocked(request, clearPrompts: true);
                return new(
                    true,
                    SessionId: request.SessionId,
                    FailureDetail: detail);
            }
            if (request.Kind is NewKind)
            {
                ClearRequestLocked(request, clearPrompts: false);
                launchedBySession[request.SessionId] = request;
                return new(true, Kind: NewKind, SessionId: request.SessionId);
            }
            request.Status = LaunchStatus.Launched;
            return new(true, SessionId: request.SessionId);
        }
    }

    public async Task DrainAsync(
        string sessionExternalId,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        sessionExternalId = RequireSessionId(sessionExternalId);
        var queue = AcquireDrainQueue(sessionExternalId);
        var entered = false;
        try
        {
            await queue.Gate.WaitAsync(cancellationToken);
            entered = true;
            var target = terminals.FindBySession(sessionExternalId);
            if (target is not { Ready: true } ||
                !string.Equals(
                    target.SessionExternalId,
                    sessionExternalId,
                    StringComparison.Ordinal))
            {
                return;
            }

            List<PendingPrompt> prompts;
            lock (sync)
            {
                ThrowIfDisposedLocked();
                PruneExpiredLocked(clock.GetUtcNow());
                var request = FindRequestBySessionLocked(sessionExternalId);
                if (request is not null)
                {
                    ClearRequestLocked(request, clearPrompts: false);
                }
                launchedBySession.Remove(sessionExternalId);
                if (!promptsBySession.Remove(sessionExternalId, out prompts!))
                {
                    return;
                }
            }

            for (var index = 0; index < prompts.Count; index++)
            {
                try
                {
                    await terminalTransport.SendAsync(
                        prompts[index].Context,
                        target,
                        prompts[index].Prompt,
                        index == 0
                            ? ManagedTerminalSubmitMode.Steer
                            : ManagedTerminalSubmitMode.Queue,
                        cancellationToken);
                }
                catch
                {
                    RestorePrompts(sessionExternalId, prompts[index..]);
                    throw;
                }
            }
        }
        finally
        {
            if (entered)
            {
                queue.Gate.Release();
            }
            ReleaseDrainQueue(sessionExternalId, queue);
        }
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
            requests.Clear();
            requestIdsBySession.Clear();
            launchedBySession.Clear();
            promptsBySession.Clear();
        }
    }

    private DrainQueue AcquireDrainQueue(string sessionExternalId)
    {
        lock (sync)
        {
            ThrowIfDisposedLocked();
            if (!drainQueues.TryGetValue(sessionExternalId, out var queue))
            {
                queue = new DrainQueue();
                drainQueues.Add(sessionExternalId, queue);
            }
            queue.References++;
            return queue;
        }
    }

    private void ReleaseDrainQueue(string sessionExternalId, DrainQueue queue)
    {
        var dispose = false;
        lock (sync)
        {
            queue.References--;
            if (queue.References == 0 &&
                drainQueues.TryGetValue(sessionExternalId, out var current) &&
                ReferenceEquals(current, queue))
            {
                drainQueues.Remove(sessionExternalId);
                dispose = true;
            }
        }
        if (dispose)
        {
            queue.Gate.Dispose();
        }
    }

    private void Publish(
        RuntimeCommandContext context,
        string kind,
        string runtime,
        string sessionExternalId,
        string cwd,
        string? projectName,
        string? prompt,
        bool elevated)
    {
        lock (sync)
        {
            ThrowIfDisposedLocked();
            var now = clock.GetUtcNow();
            PruneExpiredLocked(now);
            var existing = FindRequestBySessionLocked(sessionExternalId) ??
                launchedBySession.GetValueOrDefault(sessionExternalId);
            if (existing is not null)
            {
                EnsureSameRequest(existing, kind, runtime, cwd, elevated);
                QueuePromptLocked(
                    sessionExternalId,
                    context,
                    prompt,
                    now + requestLifetime);
                return;
            }

            var requestId = requestIdFactory();
            if (string.IsNullOrWhiteSpace(requestId) ||
                requests.ContainsKey(requestId))
            {
                throw new InvalidOperationException("托管终端启动请求 ID 无效或重复。");
            }
            var request = new LaunchRequest(
                requestId,
                kind,
                sessionExternalId,
                runtime,
                cwd,
                projectName,
                elevated,
                now,
                now + requestLifetime,
                ++nextSequence);
            requests.Add(request.RequestId, request);
            requestIdsBySession.Add(sessionExternalId, request.RequestId);
            QueuePromptLocked(
                sessionExternalId,
                context,
                prompt,
                request.ExpiresAt);
        }
    }

    private void RestorePrompts(
        string sessionExternalId,
        IEnumerable<PendingPrompt> prompts)
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            var now = clock.GetUtcNow();
            var remaining = prompts.Where(prompt => prompt.ExpiresAt > now).ToList();
            if (remaining.Count == 0)
            {
                return;
            }
            if (promptsBySession.TryGetValue(sessionExternalId, out var queued))
            {
                remaining.AddRange(queued);
            }
            promptsBySession[sessionExternalId] = remaining;
        }
    }

    private void QueuePromptLocked(
        string sessionExternalId,
        RuntimeCommandContext context,
        string? prompt,
        DateTimeOffset expiresAt)
    {
        if (prompt is null)
        {
            return;
        }
        if (!promptsBySession.TryGetValue(sessionExternalId, out var queue))
        {
            queue = [];
            promptsBySession.Add(sessionExternalId, queue);
        }
        queue.Add(new(context, prompt, expiresAt));
    }

    private void PruneExpiredLocked(DateTimeOffset now)
    {
        foreach (var request in requests.Values
                     .Where(request => request.ExpiresAt <= now)
                     .ToArray())
        {
            ClearRequestLocked(request, clearPrompts: true);
        }
        foreach (var sessionId in launchedBySession
                     .Where(item => item.Value.ExpiresAt <= now)
                     .Select(item => item.Key)
                     .ToArray())
        {
            launchedBySession.Remove(sessionId);
            promptsBySession.Remove(sessionId);
        }
        foreach (var sessionId in promptsBySession.Keys.ToArray())
        {
            promptsBySession[sessionId].RemoveAll(prompt => prompt.ExpiresAt <= now);
            if (promptsBySession[sessionId].Count == 0)
            {
                promptsBySession.Remove(sessionId);
            }
        }
    }

    private LaunchRequest? FindRequestBySessionLocked(string sessionExternalId) =>
        requestIdsBySession.TryGetValue(sessionExternalId, out var requestId) &&
        requests.TryGetValue(requestId, out var request)
            ? request
            : null;

    private void ClearRequestLocked(LaunchRequest request, bool clearPrompts)
    {
        requests.Remove(request.RequestId);
        if (requestIdsBySession.GetValueOrDefault(request.SessionId) == request.RequestId)
        {
            requestIdsBySession.Remove(request.SessionId);
        }
        if (clearPrompts)
        {
            promptsBySession.Remove(request.SessionId);
            launchedBySession.Remove(request.SessionId);
        }
    }

    private static BridgeManagedRuntimeLaunchRequest SnapshotOf(LaunchRequest request) => new(
        request.RequestId,
        request.Kind,
        request.SessionId,
        request.Runtime,
        request.Cwd,
        request.ProjectName,
        request.Elevated,
        request.CreatedAt);

    private static SessionStoreRecord FindSession(
        SessionStoreDocument sessions,
        string sessionExternalId)
    {
        var matches = sessions.Sessions.Values
            .Where(session => string.Equals(
                session.SessionId,
                sessionExternalId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new KeyNotFoundException("生产 Store 中找不到要恢复的会话。"),
            _ => throw new InvalidDataException("生产 Store 包含重复的会话身份。"),
        };
    }

    private static bool? OptionalBooleanExtension(SessionStoreRecord session, string name)
    {
        if (session.ExtensionData is null)
        {
            return null;
        }
        var values = session.ExtensionData
            .Where(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Value)
            .Take(2)
            .ToArray();
        if (values.Length == 0 ||
            values[0].ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (values.Length != 1 ||
            values[0].ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException(
                $"生产 Store 的托管终端会话包含无效扩展字段 {name}。");
        }
        return values[0].GetBoolean();
    }

    private static void EnsureSameRequest(
        LaunchRequest existing,
        string kind,
        string runtime,
        string cwd,
        bool elevated)
    {
        if (!string.Equals(existing.Kind, kind, StringComparison.Ordinal) ||
            !string.Equals(existing.Runtime, runtime, StringComparison.Ordinal) ||
            !CwdEquals(existing.Cwd, cwd) ||
            existing.Elevated != elevated)
        {
            throw new InvalidOperationException(
                "同一会话已经存在身份不同的托管终端启动请求。");
        }
    }

    private void EnsureActive()
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "托管终端生产生命周期只能用于 Active Host。");
        }
    }

    private static TimeSpan ConfiguredLifetime(BridgeHostOptions options, string name, TimeSpan fallback)
    {
        var raw = BridgeLocalConfiguration.Read(options, name);
        return long.TryParse(raw, out var milliseconds) && milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : fallback;
    }

    private void ThrowIfDisposedLocked()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static string RequireRuntime(string runtime) => runtime switch
    {
        RuntimeNames.Codex => RuntimeNames.Codex,
        RuntimeNames.ClaudeCode => RuntimeNames.ClaudeCode,
        RuntimeNames.OpenCode => RuntimeNames.OpenCode,
        _ => throw new ArgumentException("桌面运行时无效。", nameof(runtime)),
    };

    private static string RequireSessionId(string sessionExternalId) =>
        !string.IsNullOrWhiteSpace(sessionExternalId) &&
        sessionExternalId.Length <= 256 &&
        !sessionExternalId.Any(char.IsControl)
            ? sessionExternalId
            : throw new ArgumentException(
                "托管终端会话 ID 无效。",
                nameof(sessionExternalId));

    private static string NormalizeCwd(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd.Trim()))
        {
            throw new ArgumentException("托管终端工作目录无效。", nameof(cwd));
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd.Trim()));
    }

    private static string? ValidatePrompt(string? prompt)
    {
        if (prompt is null)
        {
            return null;
        }
        var normalized = prompt.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        if (normalized.Length == 0)
        {
            return null;
        }
        if (normalized.Length > 8_000)
        {
            throw new ArgumentException("托管终端启动提示超过 8000 字。", nameof(prompt));
        }
        return prompt;
    }

    private static bool CwdEquals(string left, string right) => string.Equals(
        left,
        right,
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private static string? ProjectName(string cwd) =>
        Path.GetFileName(cwd) is { Length: > 0 } name ? name : null;

    private enum LaunchStatus
    {
        Pending,
        Claimed,
        Launched,
    }

    private sealed class LaunchRequest(
        string requestId,
        string kind,
        string sessionId,
        string runtime,
        string cwd,
        string? projectName,
        bool elevated,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        long sequence)
    {
        public string RequestId { get; } = requestId;
        public string Kind { get; } = kind;
        public string SessionId { get; } = sessionId;
        public string Runtime { get; } = runtime;
        public string Cwd { get; } = cwd;
        public string? ProjectName { get; } = projectName;
        public bool Elevated { get; } = elevated;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public long Sequence { get; } = sequence;
        public LaunchStatus Status { get; set; }
    }

    private sealed record PendingPrompt(
        RuntimeCommandContext Context,
        string Prompt,
        DateTimeOffset ExpiresAt);

    private sealed class DrainQueue
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int References { get; set; }
    }
}
