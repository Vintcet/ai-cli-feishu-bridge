using System.Text.Json.Serialization;

namespace AiCliFeishuControl;

internal sealed class OpenCodeLaunchStatus
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("registered")]
    public bool Registered { get; set; }

    [JsonPropertyName("ready")]
    public bool Ready { get; set; }

    [JsonPropertyName("generation")]
    public long Generation { get; set; }
}

internal static class OpenCodeLaunchWaiter
{
    internal const int DefaultMaximumAttempts = 600;

    public static async Task<OpenCodeLaunchStatus> WaitAsync(
        int port,
        long generation,
        Func<CancellationToken, Task<OpenCodeLaunchStatus?>> probe,
        Func<Exception?> earlyFailure,
        int maximumAttempts = DefaultMaximumAttempts,
        TimeSpan? pollInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken cancellationToken = default)
    {
        if (port is <= 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }
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
                    Ready: true,
                    Generation: > 0,
                } && status.Port == port && status.Generation == generation)
            {
                return status;
            }
            if (attempt + 1 < maximumAttempts)
            {
                await delay(interval, cancellationToken);
            }
        }
        throw new TimeoutException(
            $"OpenCode 启动超时：Bridge 未确认端口 {port} 健康且事件流 Ready。");
    }
}
