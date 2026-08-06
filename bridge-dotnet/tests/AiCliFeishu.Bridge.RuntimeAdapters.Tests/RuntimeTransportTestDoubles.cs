using System.Net;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.RuntimeAdapters.Tests;

internal sealed record RecordedHttpRequest(
    HttpMethod Method,
    Uri Uri,
    string? Body,
    IReadOnlyList<string> Accept);

internal sealed class QueueHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> responses = new();

    public List<RecordedHttpRequest> Requests { get; } = [];

    public void Enqueue(HttpStatusCode status, string? body = null)
    {
        responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = body is null ? null : new StringContent(body),
        });
    }

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new(
            request.Method,
            request.RequestUri!,
            body,
            request.Headers.Accept.Select(value => value.MediaType ?? "").ToArray()));
        if (responses.Count == 0)
        {
            throw new InvalidOperationException("测试没有为 HTTP 请求配置响应。");
        }
        return responses.Dequeue()(request);
    }
}

internal sealed class FakeOpenCodeEndpointDirectory : IOpenCodeEndpointDirectory
{
    public Dictionary<string, OpenCodeEndpoint> Sessions { get; } =
        new(StringComparer.Ordinal);

    public OpenCodeEndpoint? FindBySession(string sessionExternalId) =>
        Sessions.GetValueOrDefault(sessionExternalId);

    public IReadOnlyList<OpenCodeEndpoint> ListReady() => Sessions.Values
        .Where(endpoint => endpoint.Ready)
        .Distinct()
        .ToArray();
}

internal sealed class FakeOpenCodeLifecycle(
    FakeOpenCodeEndpointDirectory endpoints,
    OpenCodeEndpoint readyEndpoint) : IOpenCodeRuntimeLifecycle
{
    public List<string> Calls { get; } = [];

    public Task LaunchAsync(
        RuntimeCommandContext context,
        string requestedExternalId,
        string cwd,
        bool elevated,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"launch:{context.CommandId}:{requestedExternalId}:{cwd}:{elevated}");
        return Task.CompletedTask;
    }

    public Task ResumeAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? cwd,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"resume:{context.CommandId}:{sessionExternalId}:{cwd}");
        return Task.CompletedTask;
    }

    public Task WaitUntilReadyAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"wait:{context.CommandId}:{sessionExternalId}");
        endpoints.Sessions[sessionExternalId] = readyEndpoint;
        return Task.CompletedTask;
    }

    public Task StopAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"stop:{context.CommandId}:{sessionExternalId}:{reason}");
        return Task.CompletedTask;
    }
}

internal sealed class RecordingRuntimeEventSink : IRuntimeEventSink
{
    private readonly TaskCompletionSource published =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<RuntimeEventEnvelope> Events { get; } = [];

    public Task FirstPublished => published.Task;

    public Task PublishAsync(
        RuntimeEventEnvelope runtimeEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add(runtimeEvent);
        published.TrySetResult();
        return Task.CompletedTask;
    }
}
