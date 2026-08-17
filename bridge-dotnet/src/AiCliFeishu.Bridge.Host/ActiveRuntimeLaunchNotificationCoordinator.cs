using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.Host;

internal sealed class ActiveRuntimeLaunchNotificationCoordinator : IDisposable
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);
    private readonly object sync = new();
    private readonly Dictionary<string, NotificationEntry> entries =
        new(StringComparer.Ordinal);
    private readonly IFeishuGateway gateway;
    private readonly TimeProvider clock;
    private readonly TimeSpan lifetime;
    private bool disposed;

    public ActiveRuntimeLaunchNotificationCoordinator(
        BridgeHostOptions options,
        IFeishuGateway gateway)
        : this(
            gateway,
            TimeProvider.System,
            BridgeLocalConfiguration.ParsePositiveMilliseconds(
                BridgeLocalConfiguration.Read(
                    options,
                    "RUNTIME_AUTO_LAUNCH_TIMEOUT_MS"),
                DefaultLifetime))
    {
    }

    internal ActiveRuntimeLaunchNotificationCoordinator(
        IFeishuGateway gateway,
        TimeProvider clock,
        TimeSpan lifetime)
    {
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.lifetime = lifetime > TimeSpan.Zero
            ? lifetime
            : throw new ArgumentOutOfRangeException(nameof(lifetime));
    }

    public void Track(
        string sessionExternalId,
        string runtime,
        string sourceMessageId,
        string chatId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionExternalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);

        NotificationEntry? previous = null;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            entries.Remove(sessionExternalId, out previous);
            var entry = new NotificationEntry(
                sessionExternalId,
                runtime,
                sourceMessageId,
                chatId);
            entry.Timer = clock.CreateTimer(
                static state =>
                {
                    var callback = (TimeoutCallback)state!;
                    callback.Owner.Timeout(callback.SessionExternalId);
                },
                new TimeoutCallback(this, sessionExternalId),
                lifetime,
                System.Threading.Timeout.InfiniteTimeSpan);
            entries.Add(sessionExternalId, entry);
        }
        previous?.Dispose();
    }

    public void Cancel(string sessionExternalId)
    {
        NotificationEntry? entry;
        lock (sync)
        {
            entries.Remove(sessionExternalId, out entry);
        }
        entry?.Dispose();
    }

    public async Task CompleteAsync(
        string sessionExternalId,
        bool success,
        string? error,
        CancellationToken cancellationToken)
    {
        var entry = Take(sessionExternalId);
        if (entry is null)
        {
            return;
        }
        entry.Dispose();
        if (!success)
        {
            var detail = string.IsNullOrWhiteSpace(error)
                ? "桌面助手未能启动对应窗口。"
                : error.Trim();
            await NotifyBestEffortAsync(entry, $"{RuntimeDisplayName(entry.Runtime)} 未启动：{detail}", cancellationToken);
        }
    }

    private void Timeout(string sessionExternalId)
    {
        var entry = Take(sessionExternalId);
        if (entry is null)
        {
            return;
        }
        entry.Dispose();
        _ = NotifyBestEffortAsync(
            entry,
            $"{RuntimeDisplayName(entry.Runtime)} 未启动：等待桌面助手打开窗口超时。" +
            "请确认面板正在运行，然后重试。",
            CancellationToken.None);
    }

    private NotificationEntry? Take(string sessionExternalId)
    {
        lock (sync)
        {
            entries.Remove(sessionExternalId, out var entry);
            return entry;
        }
    }

    private async Task NotifyBestEffortAsync(
        NotificationEntry entry,
        string text,
        CancellationToken cancellationToken)
    {
        try
        {
            await gateway.ReplyTextAsync(
                entry.SourceMessageId,
                text,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            try
            {
                await gateway.SendTextAsync(entry.ChatId, text, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        NotificationEntry[] current;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            current = entries.Values.ToArray();
            entries.Clear();
        }
        foreach (var entry in current)
        {
            entry.Dispose();
        }
    }

    private static string RuntimeDisplayName(string runtime) => runtime switch
    {
        "claudecode" => "Claude Code",
        "opencode" => "OpenCode",
        _ => "Codex",
    };

    private sealed record TimeoutCallback(
        ActiveRuntimeLaunchNotificationCoordinator Owner,
        string SessionExternalId);

    private sealed class NotificationEntry(
        string sessionExternalId,
        string runtime,
        string sourceMessageId,
        string chatId) : IDisposable
    {
        public string SessionExternalId { get; } = sessionExternalId;
        public string Runtime { get; } = runtime;
        public string SourceMessageId { get; } = sourceMessageId;
        public string ChatId { get; } = chatId;
        public ITimer? Timer { get; set; }

        public void Dispose() => Timer?.Dispose();
    }
}
