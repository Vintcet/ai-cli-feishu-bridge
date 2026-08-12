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

internal enum ManagedTerminalLaunchConfirmation
{
    TerminalReady,
    SessionBound,
}

internal static class ManagedTerminalLaunchWaiter
{
    internal const int DefaultMaximumAttempts = 1_200;

    public static async Task<ManagedTerminalLaunchStatus> WaitAsync(
        string terminalId,
        Func<CancellationToken, Task<ManagedTerminalLaunchStatus?>> probe,
        Func<Exception?> earlyFailure,
        ManagedTerminalLaunchConfirmation confirmation =
            ManagedTerminalLaunchConfirmation.SessionBound,
        string? expectedSessionExternalId = null,
        int maximumAttempts = DefaultMaximumAttempts,
        TimeSpan? pollInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalId);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(earlyFailure);
        if (confirmation is ManagedTerminalLaunchConfirmation.TerminalReady &&
            expectedSessionExternalId is not null)
        {
            throw new ArgumentException(
                "只等待终端 Ready 时不能指定会话 ID。",
                nameof(expectedSessionExternalId));
        }
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
                } &&
                string.Equals(status.TerminalId, terminalId, StringComparison.Ordinal) &&
                SessionMatches(
                    status.SessionExternalId,
                    confirmation,
                    expectedSessionExternalId))
            {
                return status;
            }
            if (attempt + 1 < maximumAttempts)
            {
                await delay(interval, cancellationToken);
            }
        }
        throw new TimeoutException(confirmation switch
        {
            ManagedTerminalLaunchConfirmation.TerminalReady =>
                "托管终端启动超时：Bridge 未确认该窗口已在线且 Ready。",
            _ when expectedSessionExternalId is not null =>
                $"托管终端启动超时：Bridge 未确认该窗口已绑定目标会话 {expectedSessionExternalId}。",
            _ => "托管终端启动超时：Bridge 未确认该窗口已完成 SessionStart。",
        });
    }

    private static bool SessionMatches(
        string? actualSessionExternalId,
        ManagedTerminalLaunchConfirmation confirmation,
        string? expectedSessionExternalId) =>
        confirmation is ManagedTerminalLaunchConfirmation.TerminalReady ||
        !string.IsNullOrWhiteSpace(actualSessionExternalId) &&
        (expectedSessionExternalId is null ||
         string.Equals(
             actualSessionExternalId,
             expectedSessionExternalId,
             StringComparison.Ordinal));
}
