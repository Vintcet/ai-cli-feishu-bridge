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

    // A terminal host registers with the bridge as soon as it starts, long before the
    // runtime reports SessionStart, so registration gets its own much shorter budget.
    // It still has to cover an elevated launch waiting on a UAC prompt.
    internal const int DefaultRegistrationAttempts = 480;

    // One missed poll can race a registration refresh, so a disappearance is only
    // treated as an exit once it has been observed twice in a row.
    private const int OfflineConfirmations = 2;

    public static async Task<ManagedTerminalLaunchStatus> WaitAsync(
        string terminalId,
        Func<CancellationToken, Task<ManagedTerminalLaunchStatus?>> probe,
        Func<Exception?> earlyFailure,
        ManagedTerminalLaunchConfirmation confirmation =
            ManagedTerminalLaunchConfirmation.SessionBound,
        string? expectedSessionExternalId = null,
        int maximumAttempts = DefaultMaximumAttempts,
        int registrationAttempts = DefaultRegistrationAttempts,
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
        if (registrationAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(registrationAttempts));
        }
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        if (interval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }
        delay ??= Task.Delay;

        var sawOnline = false;
        var offlineStreak = 0;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (earlyFailure() is { } failure)
            {
                throw failure;
            }
            var status = await probe(cancellationToken);
            // A null status means the bridge could not answer, which says nothing about
            // the host; only an actual answer for this terminal is evidence either way.
            var answered = status is { Ok: true } &&
                string.Equals(status.TerminalId, terminalId, StringComparison.Ordinal);
            if (answered && status!.Registered && status.Online)
            {
                sawOnline = true;
                offlineStreak = 0;
                if (status.Ready &&
                    SessionMatches(
                        status.SessionExternalId,
                        confirmation,
                        expectedSessionExternalId))
                {
                    return status;
                }
            }
            else if (answered && sawOnline && ++offlineStreak >= OfflineConfirmations)
            {
                throw new InvalidOperationException(
                    "托管终端宿主在启动完成前退出，请查看该窗口中的错误信息" +
                    "（常见原因：CLI 未登录、resume 会话 ID 无效或启动器损坏）。");
            }
            if (!sawOnline && attempt + 1 >= registrationAttempts)
            {
                throw new TimeoutException(
                    "托管终端启动超时：终端宿主未向 Bridge 注册。" +
                    "请确认终端窗口已打开，且该 CLI 能在此目录正常启动。");
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
