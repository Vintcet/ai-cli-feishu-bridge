using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.RuntimeAdapters.Tests;

internal sealed record RecordedRuntimeCall(
    string Operation,
    RuntimeCommandContext Context,
    string SessionId,
    object? Payload = null);

internal sealed class RuntimeAdapterHarness
{
    private RuntimeAdapterHarness(IRuntimeAdapter adapter, CallRecorder recorder)
    {
        Adapter = adapter;
        Recorder = recorder;
    }

    public IRuntimeAdapter Adapter { get; }

    public CallRecorder Recorder { get; }

    public static RuntimeAdapterHarness Create(string runtime)
    {
        if (runtime == RuntimeNames.OpenCode)
        {
            var transport = new FakeOpenCodeTransport();
            return new(new OpenCodeRuntimeAdapter(transport), transport);
        }
        var managed = new FakeManagedRuntimePorts();
        IRuntimeAdapter adapter = runtime switch
        {
            RuntimeNames.Codex => new CodexRuntimeAdapter(managed, managed, managed, managed),
            RuntimeNames.ClaudeCode => new ClaudeCodeRuntimeAdapter(managed, managed, managed, managed),
            _ => throw new ArgumentOutOfRangeException(nameof(runtime)),
        };
        return new(adapter, managed);
    }
}

internal abstract class CallRecorder
{
    public List<RecordedRuntimeCall> Calls { get; } = [];

    public Exception? NextError { get; set; }

    protected Task Capture(
        string operation,
        RuntimeCommandContext context,
        string sessionId,
        object? payload = null)
    {
        Calls.Add(new(operation, context, sessionId, payload));
        if (NextError is { } error)
        {
            NextError = null;
            return Task.FromException(error);
        }
        return Task.CompletedTask;
    }
}

internal sealed class FakeManagedRuntimePorts : CallRecorder,
    IManagedTerminalDirectory,
    IManagedTerminalTransport,
    IManagedRuntimeLifecycle,
    IManagedHookResponseSink
{
    public bool Ready { get; set; } = true;
    public bool HookReady { get; set; }

    public bool IsReady(string runtime, string sessionExternalId) => HookReady;

    public ManagedTerminalTarget? FindBySession(string sessionExternalId) =>
        new("terminal_12345678", sessionExternalId, Ready);

    public Task SendAsync(
        RuntimeCommandContext context,
        ManagedTerminalTarget target,
        string prompt,
        ManagedTerminalSubmitMode submitMode,
        CancellationToken cancellationToken = default) =>
        Capture(RuntimeCommandTypes.PromptSend, context, target.SessionExternalId, new
        {
            prompt,
            mode = submitMode,
        });

    public Task LaunchAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string cwd,
        string? prompt,
        bool elevated,
        CancellationToken cancellationToken = default) =>
        Capture(RuntimeCommandTypes.SessionLaunch, context, sessionExternalId, new
        {
            runtime,
            cwd,
            prompt,
            elevated,
        });

    public Task ResumeAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string? cwd,
        string? prompt,
        CancellationToken cancellationToken = default) =>
        Capture(RuntimeCommandTypes.SessionResume, context, sessionExternalId, new
        {
            runtime,
            cwd,
            prompt,
        });

    public Task StopAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string? reason,
        CancellationToken cancellationToken = default) =>
        Capture(RuntimeCommandTypes.SessionStop, context, sessionExternalId, new
        {
            runtime,
            reason,
        });

    public Task ResolveApprovalAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string requestId,
        string decision,
        CancellationToken cancellationToken = default) =>
        Capture(RuntimeCommandTypes.ApprovalResolve, context, sessionExternalId, new
        {
            runtime,
            requestId,
            decision,
        });

    public Task ResolveInputAsync(
        RuntimeCommandContext context,
        string runtime,
        string sessionExternalId,
        string requestId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers,
        CancellationToken cancellationToken = default) =>
        Capture(RuntimeCommandTypes.InputResolve, context, sessionExternalId, new
        {
            runtime,
            requestId,
            answers,
        });
}

internal sealed class FakeOpenCodeTransport : CallRecorder, IOpenCodeTransport
{
    public bool Ready { get; set; } = true;

    public bool IsReady(string sessionExternalId) => Ready;

    public Task SendPromptAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string prompt,
        CancellationToken cancellationToken = default) =>
        Capture(RuntimeCommandTypes.PromptSend, context, sessionExternalId, prompt);

    public Task ResolveApprovalAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string requestId,
        string decision,
        CancellationToken cancellationToken = default) =>
        Capture(RuntimeCommandTypes.ApprovalResolve, context, sessionExternalId, new
        {
            requestId,
            decision,
        });

    public Task ResolveInputAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string requestId,
        IReadOnlyList<IReadOnlyList<string>> answers,
        CancellationToken cancellationToken = default) =>
        Capture(RuntimeCommandTypes.InputResolve, context, sessionExternalId, new
        {
            requestId,
            answers,
        });

    public Task LaunchAsync(
        RuntimeCommandContext context,
        string requestedExternalId,
        string cwd,
        string? prompt,
        bool elevated,
        CancellationToken cancellationToken = default) =>
        Capture(RuntimeCommandTypes.SessionLaunch, context, requestedExternalId, new
        {
            cwd,
            prompt,
            elevated,
        });

    public Task ResumeAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? prompt,
        CancellationToken cancellationToken = default) =>
        Capture(RuntimeCommandTypes.SessionResume, context, sessionExternalId, prompt);

    public Task StopAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? reason,
        CancellationToken cancellationToken = default) =>
        Capture(RuntimeCommandTypes.SessionStop, context, sessionExternalId, reason);
}
