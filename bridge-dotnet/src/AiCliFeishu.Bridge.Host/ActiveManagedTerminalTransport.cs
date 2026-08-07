using System.Text;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host;

internal sealed class ActiveManagedTerminalTransport : IManagedTerminalTransport
{
    private const int MaximumAttempts = 4;
    private readonly object sync = new();
    private readonly Dictionary<string, TerminalQueue> queues =
        new(StringComparer.Ordinal);
    private readonly BridgeHostOptions options;
    private readonly IBridgeManagedTerminalRegistrationDirectory directory;
    private readonly IManagedTerminalTransport pipeTransport;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public ActiveManagedTerminalTransport(
        BridgeHostOptions options,
        IBridgeManagedTerminalRegistrationDirectory directory)
        : this(
            options,
            directory,
            CreatePipeTransport(directory),
            static (duration, cancellationToken) =>
                Task.Delay(duration, cancellationToken))
    {
    }

    internal ActiveManagedTerminalTransport(
        BridgeHostOptions options,
        IBridgeManagedTerminalRegistrationDirectory directory,
        IManagedTerminalTransport pipeTransport,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
        this.pipeTransport = pipeTransport ??
            throw new ArgumentNullException(nameof(pipeTransport));
        this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public async Task SendAsync(
        RuntimeCommandContext context,
        ManagedTerminalTarget target,
        string prompt,
        ManagedTerminalSubmitMode submitMode,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(prompt);
        ValidateTarget(target);
        if (submitMode is not ManagedTerminalSubmitMode.Steer and
            not ManagedTerminalSubmitMode.Queue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(submitMode),
                "托管终端提交模式无效。");
        }
        var normalizedPrompt = NormalizePrompt(prompt);
        if (normalizedPrompt.Length is 0 or > 8_000)
        {
            throw new ArgumentException("回复内容为空或超过 8000 字。", nameof(prompt));
        }
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCurrent(target);

        var queue = AcquireQueue(target.TerminalId);
        var entered = false;
        try
        {
            await queue.Gate.WaitAsync(cancellationToken);
            entered = true;
            await SendWithRetryAsync(
                context,
                target,
                normalizedPrompt,
                submitMode,
                cancellationToken);
        }
        finally
        {
            if (entered)
            {
                queue.Gate.Release();
            }
            ReleaseQueue(target.TerminalId, queue);
        }
    }

    private async Task SendWithRetryAsync(
        RuntimeCommandContext context,
        ManagedTerminalTarget target,
        string prompt,
        ManagedTerminalSubmitMode submitMode,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCurrent(target);
            try
            {
                await pipeTransport.SendAsync(
                    context,
                    target,
                    prompt,
                    submitMode,
                    cancellationToken);
                return;
            }
            catch (ManagedTerminalUnavailableException) when (
                attempt < MaximumAttempts)
            {
                await delay(
                    TimeSpan.FromMilliseconds(attempt * 150),
                    cancellationToken);
            }
        }
    }

    private TerminalQueue AcquireQueue(string terminalId)
    {
        lock (sync)
        {
            if (!queues.TryGetValue(terminalId, out var queue))
            {
                queue = new TerminalQueue();
                queues.Add(terminalId, queue);
            }
            queue.References++;
            return queue;
        }
    }

    private void ReleaseQueue(string terminalId, TerminalQueue queue)
    {
        var dispose = false;
        lock (sync)
        {
            queue.References--;
            if (queue.References == 0 &&
                queues.TryGetValue(terminalId, out var current) &&
                ReferenceEquals(current, queue))
            {
                queues.Remove(terminalId);
                dispose = true;
            }
        }
        if (dispose)
        {
            queue.Gate.Dispose();
        }
    }

    private void EnsureCurrent(ManagedTerminalTarget target)
    {
        if (!directory.IsCurrent(target))
        {
            throw new InvalidOperationException(
                "托管终端与目标会话不匹配，已拒绝输入以避免串线。");
        }
    }

    private void EnsureActive()
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "托管终端生产传输只能用于 Active Host。");
        }
    }

    private static void ValidateTarget(ManagedTerminalTarget target)
    {
        if (!target.Ready)
        {
            throw new InvalidOperationException("同步窗口仍在启动或已经离线。");
        }
        if (string.IsNullOrEmpty(target.TerminalId) ||
            target.TerminalId.Length is < 8 or > 64 ||
            target.TerminalId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
        {
            throw new ArgumentException("托管终端 ID 无效。", nameof(target));
        }
        if (string.IsNullOrWhiteSpace(target.SessionExternalId) ||
            target.SessionExternalId.Length > 256 ||
            target.SessionExternalId.Any(char.IsControl) ||
            target.Generation <= 0)
        {
            throw new ArgumentException("托管终端会话身份无效。", nameof(target));
        }
    }

    private static string NormalizePrompt(string prompt)
    {
        var normalized = new StringBuilder(prompt.Length);
        var lineBreak = false;
        foreach (var character in prompt)
        {
            if (character is '\r' or '\n')
            {
                if (!lineBreak)
                {
                    normalized.Append(' ');
                    lineBreak = true;
                }
                continue;
            }
            normalized.Append(character);
            lineBreak = false;
        }
        return normalized.ToString().Trim();
    }

    private static IManagedTerminalTransport CreatePipeTransport(
        IBridgeManagedTerminalRegistrationDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return new NamedPipeManagedTerminalTransport(isCurrent: directory.IsCurrent);
    }

    private sealed class TerminalQueue
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int References { get; set; }
    }
}
