using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal static class BridgeRetryStopKinds
{
    public const string Stopped = "stopped";
    public const string AlreadyStopped = "already_stopped";
    public const string Stale = "stale";
}

internal static class BridgeRuntimeNotificationKinds
{
    public const string Stop = "stop";
    public const string Error = "error";
}

internal sealed record BridgeRetryStopResult(
    string Kind,
    bool RetryAlreadyStarted,
    FeishuCardView? Card = null,
    Func<CancellationToken, Task>? AfterAcknowledged = null);

internal interface IBridgeActiveRuntimeRetryCoordinator
{
    bool HasActiveRetry(string sessionId);

    ValueTask BeginManualTurnAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<BridgeRetryStopResult> StopAsync(
        string sessionId,
        string cycleId,
        string messageId,
        CancellationToken cancellationToken = default);
}

internal sealed partial class ActiveRuntimeRetryCoordinator :
    IBridgeRuntimeEventHandler,
    IBridgeActiveRuntimeRetryCoordinator,
    IBridgeHostSubsystem,
    IBridgeHostSubsystemHealth,
    IDisposable
{
    private const string RetryPrompt =
        "刚才的请求因临时服务错误失败。请重试上一项任务，并继续从中断处执行。";
    private const int DefaultMaxAttempts = BridgeSettingsLimits.RetryMaxAttemptsDefault;
    private const int DefaultIntervalSeconds = 5;
    private const int DefaultJitterSeconds = 3;
    private const string RetryStateExtension = "runtimeRetryState";
    private readonly object sync = new();
    private readonly BridgeHostOptions options;
    private readonly IBridgeActiveRuntimeStateSink stateSink;
    private readonly IBridgeProductionStoreOwner storeOwner;
    private readonly Func<IBridgeRuntimeCommandGateway> runtimeCommands;
    private readonly IFeishuGateway gateway;
    private readonly IFeishuCardRenderer renderer;
    private readonly ActiveRuntimeActivityCoordinator? activity;
    private readonly IBridgeActiveFileTransferCoordinator? fileTransfers;
    private readonly IBridgeActiveSessionGroupCoordinator? sessionGroups;
    private readonly IBridgeActiveApprovalNotifier? approvalNotifications;
    private readonly IBridgeActiveInputNotifier? inputNotifications;
    private readonly BridgeRemotePromptLedger? remotePrompts;
    private readonly CodexTranscriptMonitor? transcriptMonitor;
    private readonly TimeProvider clock;
    private readonly TimeSpan? retryDelayOverride;
    private readonly Func<int, int> selectJitter;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, RetryCycle> cycles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> attemptCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> generations = new(StringComparer.Ordinal);
    private readonly HashSet<string> notificationFlights = new(StringComparer.Ordinal);
    private readonly HashSet<Task> workers = [];
    private readonly SemaphoreSlim cardPatchGate = new(1, 1);
    private readonly SemaphoreSlim retryPersistenceGate = new(1, 1);
    private RetryRegistryState retries = RetryRegistryState.Empty;
    private bool started;
    private bool disposed;
    private int transcriptWatchRestoreAttempts;
    private int transcriptWatchRestoreSuccesses;
    private int transcriptWatchRestoreFailures;

    public ActiveRuntimeRetryCoordinator(
        BridgeHostOptions options,
        IBridgeActiveRuntimeStateSink stateSink,
        IBridgeProductionStoreOwner storeOwner,
        IBridgeRuntimeCommandGateway runtimeCommands,
        IFeishuGateway gateway,
        IFeishuCardRenderer renderer,
        TimeProvider? timeProvider = null,
        TimeSpan? retryDelayOverride = null,
        Func<int, int>? jitterSelector = null,
        ActiveRuntimeActivityCoordinator? activity = null,
        IBridgeActiveFileTransferCoordinator? fileTransfers = null,
        IBridgeActiveSessionGroupCoordinator? sessionGroups = null,
        IBridgeActiveApprovalNotifier? approvalNotifications = null,
        IBridgeActiveInputNotifier? inputNotifications = null,
        BridgeRemotePromptLedger? remotePrompts = null,
        CodexTranscriptMonitor? transcriptMonitor = null)
        : this(
            options,
            stateSink,
            storeOwner,
            () => runtimeCommands,
            gateway,
            renderer,
            timeProvider,
            retryDelayOverride,
            jitterSelector,
            activity,
            fileTransfers,
            sessionGroups,
            approvalNotifications,
            inputNotifications,
            remotePrompts,
            transcriptMonitor)
    {
        ArgumentNullException.ThrowIfNull(runtimeCommands);
    }

    internal ActiveRuntimeRetryCoordinator(
        BridgeHostOptions options,
        IBridgeActiveRuntimeStateSink stateSink,
        IBridgeProductionStoreOwner storeOwner,
        Func<IBridgeRuntimeCommandGateway> runtimeCommands,
        IFeishuGateway gateway,
        IFeishuCardRenderer renderer,
        TimeProvider? timeProvider = null,
        TimeSpan? retryDelayOverride = null,
        Func<int, int>? jitterSelector = null,
        ActiveRuntimeActivityCoordinator? activity = null,
        IBridgeActiveFileTransferCoordinator? fileTransfers = null,
        IBridgeActiveSessionGroupCoordinator? sessionGroups = null,
        IBridgeActiveApprovalNotifier? approvalNotifications = null,
        IBridgeActiveInputNotifier? inputNotifications = null,
        BridgeRemotePromptLedger? remotePrompts = null,
        CodexTranscriptMonitor? transcriptMonitor = null)
    {
        this.options = options;
        this.stateSink = stateSink;
        this.storeOwner = storeOwner;
        this.runtimeCommands = runtimeCommands ??
            throw new ArgumentNullException(nameof(runtimeCommands));
        this.gateway = gateway;
        this.renderer = renderer;
        this.activity = activity;
        this.fileTransfers = fileTransfers;
        this.sessionGroups = sessionGroups;
        this.approvalNotifications = approvalNotifications;
        this.inputNotifications = inputNotifications;
        this.remotePrompts = remotePrompts;
        this.transcriptMonitor = transcriptMonitor;
        transcriptMonitor?.Attach(HandleTranscriptErrorAsync);
        clock = timeProvider ?? TimeProvider.System;
        this.retryDelayOverride = retryDelayOverride;
        selectJitter = jitterSelector ?? (maximum => Random.Shared.Next(maximum + 1));
    }

    public string Name => "active-runtime-retry";

    public BridgeComponentHealth ComponentHealth
    {
        get
        {
            lock (sync)
            {
                var active = cycles.Values.Count(cycle =>
                    !cycle.Stopped && cycle.Phase is not RetryCyclePhases.Stopped);
                return new(
                    Name,
                    started ? "ready" : "starting",
                    $"active={active} tracked={cycles.Count} workers={workers.Count} " +
                    $"transcriptRestore={transcriptWatchRestoreSuccesses}/" +
                    $"{transcriptWatchRestoreAttempts} failed={transcriptWatchRestoreFailures}");
            }
        }
    }

    public bool HasActiveRetry(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }
        lock (sync)
        {
            return cycles.TryGetValue(sessionId, out var cycle) &&
                !cycle.Stopped &&
                cycle.Phase is not RetryCyclePhases.Stopped;
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
            lifetime.Cancel();
        }
        lifetime.Dispose();
        retryPersistenceGate.Dispose();
        cardPatchGate.Dispose();
    }

    private static class RetryCyclePhases
    {
        public const string Preparing = "preparing";
        public const string Scheduled = "scheduled";
        public const string Running = "running";
        public const string Stopped = "stopped";
    }

    private static class PersistedRetryPhases
    {
        public const string Scheduled = "scheduled";
        public const string Running = "running";
        public const string Dispatched = "dispatched";
        public const string Stopped = "stopped";
    }

    private sealed class RetryCycle(
        string cycleId,
        string taskId,
        string runtime,
        string sessionId,
        string turnId,
        string error,
        int attempt,
        int maxAttempts,
        TimeSpan delay,
        DateTimeOffset dueAt,
        string traceId,
        string eventId)
    {
        public string CycleId { get; } = cycleId;
        public string TaskId { get; } = taskId;
        public string Runtime { get; } = runtime;
        public string SessionId { get; } = sessionId;
        public string TurnId { get; } = turnId;
        public string Error { get; } = error;
        public int Attempt { get; } = attempt;
        public int MaxAttempts { get; } = maxAttempts;
        public TimeSpan Delay { get; } = delay;
        public DateTimeOffset DueAt { get; } = dueAt;
        public string TraceId { get; } = traceId;
        public string EventId { get; } = eventId;
        public string Phase { get; set; } = RetryCyclePhases.Preparing;
        public bool Stopped { get; set; }
        public bool WorkerScheduled { get; set; }
        public List<RetryMessageTarget> Targets { get; set; } = [];
    }

    private sealed record RetryFailure(
        string Runtime,
        string SessionId,
        string TurnId,
        string Error,
        string? ErrorCode,
        long Generation,
        string TraceId,
        string EventId);

    private sealed record RuntimeNotification(
        string Runtime,
        string SessionId,
        string TurnId,
        string Kind,
        string Message,
        string TraceId,
        string EventId);

    private sealed record RetryMessageTarget(
        string MessageId,
        string ChatId,
        int CardIndex);

    private sealed record RetrySettings(
        bool AutoRetry,
        int MaxAttempts,
        int IntervalSeconds,
        int JitterSeconds);

    private sealed record PersistedRetryState(
        string CycleId,
        string Runtime,
        string TurnId,
        string Error,
        int Attempt,
        int MaxAttempts,
        DateTimeOffset DueAt,
        string TraceId,
        string EventId,
        string Phase);
}
