using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Adapters.ManagedTerminal;

public enum ManagedTerminalSubmitMode
{
    Steer,
    Queue,
}

public sealed record ManagedTerminalTarget(
    string TerminalId,
    string SessionExternalId,
    bool Ready,
    long Generation = 0);

public interface IManagedTerminalDirectory
{
    ManagedTerminalTarget? FindBySession(string sessionExternalId);
}

public interface IManagedTerminalTransport
{
    Task SendAsync(
        RuntimeCommandContext context,
        ManagedTerminalTarget target,
        string prompt,
        ManagedTerminalSubmitMode submitMode,
        CancellationToken cancellationToken = default);
}

public interface IManagedRuntimeLifecycle
{
    Task LaunchAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string cwd,
        string? prompt,
        bool elevated,
        CancellationToken cancellationToken = default);

    Task ResumeAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string? cwd,
        string? prompt,
        CancellationToken cancellationToken = default);

    Task StopAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string? reason,
        CancellationToken cancellationToken = default);
}

public interface IManagedHookResponseSink
{
    bool IsReady(string runtime, string sessionExternalId);

    Task ResolveApprovalAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string requestId,
        string decision,
        CancellationToken cancellationToken = default);

    Task ResolveInputAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string requestId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers,
        CancellationToken cancellationToken = default);

    Task DeferInputToLocalAsync(
        string runtime,
        string sessionExternalId,
        string requestId,
        CancellationToken cancellationToken = default);
}
