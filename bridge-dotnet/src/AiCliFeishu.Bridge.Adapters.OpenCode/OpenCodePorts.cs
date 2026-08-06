using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Adapters.OpenCode;

public sealed record OpenCodeEndpoint(Uri BaseUri, string? Directory, bool Ready = true);

public interface IOpenCodeEndpointDirectory
{
    OpenCodeEndpoint? FindBySession(string sessionExternalId);

    IReadOnlyList<OpenCodeEndpoint> ListReady();
}

public interface IOpenCodeEventSource
{
    IAsyncEnumerable<OpenCodeRawEvent> ReadAllAsync(
        OpenCodeEndpoint endpoint,
        CancellationToken cancellationToken = default);
}

public interface IOpenCodeTransport
{
    bool IsReady(string sessionExternalId);

    Task SendPromptAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string prompt,
        CancellationToken cancellationToken = default);

    Task ResolveApprovalAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string requestId,
        string decision,
        CancellationToken cancellationToken = default);

    Task ResolveInputAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string requestId,
        IReadOnlyList<IReadOnlyList<string>> answers,
        CancellationToken cancellationToken = default);

    Task LaunchAsync(
        RuntimeCommandContext context,
        string requestedExternalId,
        string cwd,
        string? prompt,
        bool elevated,
        CancellationToken cancellationToken = default);

    Task ResumeAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? prompt,
        CancellationToken cancellationToken = default);

    Task StopAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? reason,
        CancellationToken cancellationToken = default);
}

public interface IOpenCodeRuntimeLifecycle
{
    Task LaunchAsync(
        RuntimeCommandContext context,
        string requestedExternalId,
        string cwd,
        bool elevated,
        CancellationToken cancellationToken = default);

    Task ResumeAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? cwd,
        CancellationToken cancellationToken = default);

    Task WaitUntilReadyAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        CancellationToken cancellationToken = default);

    Task StopAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? reason,
        CancellationToken cancellationToken = default);
}
