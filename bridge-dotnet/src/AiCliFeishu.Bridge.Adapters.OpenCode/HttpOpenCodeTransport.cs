using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Adapters.OpenCode;

public sealed class HttpOpenCodeTransport(
    HttpClient httpClient,
    IOpenCodeEndpointDirectory endpoints,
    IOpenCodeRuntimeLifecycle lifecycle) : IOpenCodeTransport
{
    public bool IsReady(string sessionExternalId) =>
        endpoints.FindBySession(sessionExternalId) is { Ready: true };

    public async Task SendPromptAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = RequireEndpoint(sessionExternalId);
        using var response = await httpClient.PostAsJsonAsync(
            BuildUri(
                endpoint,
                $"/session/{Uri.EscapeDataString(sessionExternalId)}/prompt_async"),
            new
            {
                parts = new[] { new { type = "text", text = prompt } },
            },
            cancellationToken);
        await EnsureSuccessAsync(response, "发送提示", allowNoContent: true, cancellationToken);
    }

    public async Task ResolveApprovalAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string requestId,
        string decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = RequireEndpoint(sessionExternalId);
        var reply = decision switch
        {
            "allow_once" => "once",
            "allow_session" => "always",
            "deny" => "reject",
            _ => throw new InvalidDataException($"未知的审批决定 {decision}。"),
        };

        using var v2 = await httpClient.PostAsJsonAsync(
            new Uri(
                endpoint.BaseUri,
                $"/api/session/{Uri.EscapeDataString(sessionExternalId)}/permission/{Uri.EscapeDataString(requestId)}/reply"),
            new { reply },
            cancellationToken);
        if (v2.IsSuccessStatusCode)
        {
            return;
        }
        if (!IsUnsupported(v2.StatusCode))
        {
            await EnsureSuccessAsync(v2, "回复权限", false, cancellationToken);
        }

        using var modern = await httpClient.PostAsJsonAsync(
            BuildUri(endpoint, $"/permission/{Uri.EscapeDataString(requestId)}/reply"),
            new { reply },
            cancellationToken);
        if (modern.IsSuccessStatusCode)
        {
            return;
        }
        if (!IsUnsupported(modern.StatusCode))
        {
            await EnsureSuccessAsync(modern, "回复权限", false, cancellationToken);
        }

        using var legacy = await httpClient.PostAsJsonAsync(
            BuildUri(
                endpoint,
                $"/session/{Uri.EscapeDataString(sessionExternalId)}/permissions/{Uri.EscapeDataString(requestId)}"),
            new { response = reply },
            cancellationToken);
        await EnsureSuccessAsync(legacy, "回复权限", false, cancellationToken);
    }

    public async Task ResolveInputAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string requestId,
        IReadOnlyList<IReadOnlyList<string>> answers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = RequireEndpoint(sessionExternalId);
        using var response = await httpClient.PostAsJsonAsync(
            BuildUri(endpoint, $"/question/{Uri.EscapeDataString(requestId)}/reply"),
            new { answers = answers.Select(value => value.ToArray()).ToArray() },
            cancellationToken);
        await EnsureSuccessAsync(response, "回复问题", false, cancellationToken);
    }

    public async Task LaunchAsync(
        RuntimeCommandContext context,
        string requestedExternalId,
        string cwd,
        string? prompt,
        bool elevated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await lifecycle.LaunchAsync(context, requestedExternalId, cwd, elevated, cancellationToken);
        await lifecycle.WaitUntilReadyAsync(context, requestedExternalId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            await SendPromptAsync(context, requestedExternalId, prompt, cancellationToken);
        }
    }

    public async Task ResumeAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = endpoints.FindBySession(sessionExternalId);
        if (endpoint is not { Ready: true })
        {
            await lifecycle.ResumeAsync(
                context,
                sessionExternalId,
                endpoint?.Directory,
                cancellationToken);
            await lifecycle.WaitUntilReadyAsync(context, sessionExternalId, cancellationToken);
        }
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            await SendPromptAsync(context, sessionExternalId, prompt, cancellationToken);
        }
    }

    public async Task StopAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = endpoints.FindBySession(sessionExternalId);
        if (endpoint is { Ready: true })
        {
            using var response = await httpClient.PostAsync(
                BuildUri(
                    endpoint,
                    $"/session/{Uri.EscapeDataString(sessionExternalId)}/abort"),
                content: null,
                cancellationToken);
            await EnsureSuccessAsync(response, "中止会话", true, cancellationToken);
        }
        await lifecycle.StopAsync(context, sessionExternalId, reason, cancellationToken);
    }

    private OpenCodeEndpoint RequireEndpoint(string sessionExternalId)
    {
        return endpoints.FindBySession(sessionExternalId) is { Ready: true } endpoint
            ? endpoint
            : throw new InvalidOperationException("找不到对应的 OpenCode 实例。");
    }

    private static Uri BuildUri(OpenCodeEndpoint endpoint, string path)
    {
        var builder = new UriBuilder(new Uri(endpoint.BaseUri, path));
        if (!string.IsNullOrWhiteSpace(endpoint.Directory))
        {
            var query = builder.Query.TrimStart('?');
            builder.Query = string.IsNullOrEmpty(query)
                ? $"directory={Uri.EscapeDataString(endpoint.Directory)}"
                : $"{query}&directory={Uri.EscapeDataString(endpoint.Directory)}";
        }
        return builder.Uri;
    }

    private static bool IsUnsupported(HttpStatusCode status) =>
        status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed;

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string context,
        bool allowNoContent,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode ||
            allowNoContent && response.StatusCode == HttpStatusCode.NoContent)
        {
            return;
        }
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"{context}: HTTP {(int)response.StatusCode} {response.ReasonPhrase} {body}".Trim());
    }
}
