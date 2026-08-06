namespace AiCliFeishuControl;

internal enum BridgeHostExitObservationKind
{
    Offline,
    ExpectedProcessAlive,
    AuthenticatedHost,
    UnauthenticatedEndpoint,
}

internal sealed record BridgeHostExitObservation(
    BridgeHostExitObservationKind Kind,
    int ProcessId = 0)
{
    public static BridgeHostExitObservation Offline { get; } =
        new(BridgeHostExitObservationKind.Offline);

    public static BridgeHostExitObservation ExpectedProcessAlive { get; } =
        new(BridgeHostExitObservationKind.ExpectedProcessAlive);

    public static BridgeHostExitObservation Authenticated(int processId) =>
        new(BridgeHostExitObservationKind.AuthenticatedHost, processId);

    public static BridgeHostExitObservation Unauthenticated { get; } =
        new(BridgeHostExitObservationKind.UnauthenticatedEndpoint);
}

internal static class BridgeHostExitWaiter
{
    public const int DefaultMaxAttempts = 41;
    public static TimeSpan DefaultPollInterval { get; } = TimeSpan.FromMilliseconds(250);

    public static async Task WaitAsync(
        int expectedProcessId,
        Func<CancellationToken, Task<BridgeHostExitObservation>> observe,
        CancellationToken cancellationToken = default,
        int maxAttempts = DefaultMaxAttempts,
        TimeSpan? pollInterval = null)
    {
        if (expectedProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedProcessId));
        }
        ArgumentNullException.ThrowIfNull(observe);
        if (maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }
        var interval = pollInterval ?? DefaultPollInterval;
        if (interval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = await observe(cancellationToken);
            switch (observation.Kind)
            {
                case BridgeHostExitObservationKind.Offline:
                    return;
                case BridgeHostExitObservationKind.ExpectedProcessAlive:
                case BridgeHostExitObservationKind.AuthenticatedHost
                    when observation.ProcessId == expectedProcessId:
                    break;
                case BridgeHostExitObservationKind.AuthenticatedHost:
                    throw new InvalidOperationException(
                        $"目标 Bridge Host pid={expectedProcessId} 已被 pid={observation.ProcessId} 替换；" +
                        "为避免停止或切换错误进程，已中止操作。");
                case BridgeHostExitObservationKind.UnauthenticatedEndpoint:
                    throw new InvalidOperationException(
                        "Bridge Host 停止期间端口仍被无法认证的服务占用；" +
                        "为避免错误切换，已中止操作。");
                default:
                    throw new InvalidOperationException("未知的 Bridge Host 停止探测结果。");
            }

            if (attempt < maxAttempts && interval > TimeSpan.Zero)
            {
                await Task.Delay(interval, cancellationToken);
            }
        }

        throw new TimeoutException(
            $"Bridge Host pid={expectedProcessId} 已接受停止请求，但未在等待时间内退出；" +
            "可能仍在刷新 Store 或清理后台连接。");
    }
}
