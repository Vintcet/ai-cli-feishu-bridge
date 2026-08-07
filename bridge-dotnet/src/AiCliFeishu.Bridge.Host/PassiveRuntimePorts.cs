using System.Runtime.CompilerServices;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host;

/// <summary>
/// Passive Host 的 Runtime 端口实现。
/// 它们只让 Adapter 完成装配和能力探测，不连接真实终端、OpenCode 或 CLI。
/// </summary>
public sealed class PassiveManagedTerminalDirectory : IManagedTerminalDirectory
{
    public ManagedTerminalTarget? FindBySession(string sessionExternalId) => null;
}

public sealed class PassiveManagedTerminalTransport : IManagedTerminalTransport
{
    public Task SendAsync(
        RuntimeCommandContext context,
        ManagedTerminalTarget target,
        string prompt,
        ManagedTerminalSubmitMode submitMode,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    private static InvalidOperationException Unavailable() =>
        new("Passive Host 不连接 Managed Terminal 命名管道。");
}

public sealed class PassiveManagedRuntimeLifecycle : IManagedRuntimeLifecycle
{
    public Task LaunchAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string cwd,
        string? prompt,
        bool elevated,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    public Task ResumeAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string? cwd,
        string? prompt,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    public Task StopAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string? reason,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    private static InvalidOperationException Unavailable() =>
        new("Passive Host 不启动或停止 Managed Terminal。");
}

public sealed class PassiveManagedHookResponseSink : IManagedHookResponseSink
{
    public bool IsReady(string runtime, string sessionExternalId) => false;

    public Task ResolveApprovalAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string requestId,
        string decision,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    public Task ResolveInputAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string requestId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    private static InvalidOperationException Unavailable() =>
        new("Passive Host 不回写 Managed Terminal Hook。");
}

public sealed class PassiveOpenCodeEndpointDirectory : IOpenCodeEndpointDirectory
{
    public OpenCodeEndpoint? FindBySession(string sessionExternalId) => null;

    public IReadOnlyList<OpenCodeEndpoint> ListReady() => [];
}

public sealed class PassiveOpenCodeEventSource : IOpenCodeEventSource
{
    public async IAsyncEnumerable<OpenCodeRawEvent> ReadAllAsync(
        OpenCodeEndpoint endpoint,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }
}

public sealed class PassiveOpenCodeTransport : IOpenCodeTransport
{
    public bool IsReady(string sessionExternalId) => false;

    public Task SendPromptAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string prompt,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    public Task ResolveApprovalAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string requestId,
        string decision,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    public Task ResolveInputAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string requestId,
        IReadOnlyList<IReadOnlyList<string>> answers,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    public Task LaunchAsync(
        RuntimeCommandContext context,
        string requestedExternalId,
        string cwd,
        string? prompt,
        bool elevated,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    public Task ResumeAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? prompt,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    public Task StopAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? reason,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    private static InvalidOperationException Unavailable() =>
        new("Passive Host 不调用 OpenCode HTTP API。");
}

public sealed class PassiveOpenCodeRuntimeLifecycle : IOpenCodeRuntimeLifecycle
{
    public Task LaunchAsync(
        RuntimeCommandContext context,
        string requestedExternalId,
        string cwd,
        bool elevated,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    public Task ResumeAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? cwd,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    public Task WaitUntilReadyAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    public Task StopAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? reason,
        CancellationToken cancellationToken = default) =>
        throw Unavailable();

    private static InvalidOperationException Unavailable() =>
        new("Passive Host 不启动或停止 OpenCode。");
}
