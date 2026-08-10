using System.Text.Json.Serialization;

namespace AiCliFeishuControl;

internal sealed class ManagedTerminalLaunchStatus
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("terminalId")]
    public string TerminalId { get; set; } = "";

    [JsonPropertyName("registered")]
    public bool Registered { get; set; }

    [JsonPropertyName("online")]
    public bool Online { get; set; }

    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    [JsonPropertyName("sessionExternalId")]
    public string? SessionExternalId { get; set; }
}

internal static class ManagedTerminalLaunchWaiter
{
    public static async Task<ManagedTerminalLaunchStatus> WaitAsync(
        string terminalId,
        Func<CancellationToken, Task<ManagedTerminalLaunchStatus?>> probe,
        Func<Exception?> earlyFailure,
        int maximumAttempts = 120,
        TimeSpan? pollInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalId);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(earlyFailure);
        if (maximumAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        if (interval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }
        delay ??= Task.Delay;

        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (earlyFailure() is { } failure)
            {
                throw failure;
            }
            var status = await probe(cancellationToken);
            if (status is
                {
                    Ok: true,
                    Registered: true,
                    Online: true,
                    Ready: true,
                    SessionExternalId.Length: > 0,
                } &&
                string.Equals(status.TerminalId, terminalId, StringComparison.Ordinal))
            {
                return status;
            }
            if (attempt + 1 < maximumAttempts)
            {
                await delay(interval, cancellationToken);
            }
        }
        throw new TimeoutException(
            "托管终端启动超时：Bridge 未确认该窗口已在线、Ready 且完成 SessionStart。");
    }
}
