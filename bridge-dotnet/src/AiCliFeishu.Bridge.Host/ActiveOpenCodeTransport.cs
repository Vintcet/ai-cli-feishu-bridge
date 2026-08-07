using System.Net;
using System.Net.Http.Json;
using AiCliFeishu.Bridge.Adapters.OpenCode;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host;

internal sealed class ActiveOpenCodeTransport : IOpenCodeTransport, IDisposable
{
    private static readonly TimeSpan DefaultPromptTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(10);
    private readonly BridgeHostOptions options;
    private readonly IBridgeOpenCodeEndpointRegistrationDirectory directory;
    private readonly IOpenCodeRuntimeLifecycle lifecycle;
    private readonly HttpClient httpClient;
    private readonly TimeSpan promptTimeout;
    private readonly TimeSpan commandTimeout;
    private readonly bool ownsHttpClient;
    private int disposed;

    public ActiveOpenCodeTransport(
        BridgeHostOptions options,
        IBridgeOpenCodeEndpointRegistrationDirectory directory,
        IOpenCodeRuntimeLifecycle lifecycle)
        : this(
            options,
            directory,
            lifecycle,
            CreateHttpClient(),
            DefaultPromptTimeout,
            DefaultCommandTimeout,
            ownsHttpClient: true)
    {
    }

    internal ActiveOpenCodeTransport(
        BridgeHostOptions options,
        IBridgeOpenCodeEndpointRegistrationDirectory directory,
        IOpenCodeRuntimeLifecycle lifecycle,
        HttpClient httpClient,
        TimeSpan? promptTimeout = null,
        TimeSpan? commandTimeout = null)
        : this(
            options,
            directory,
            lifecycle,
            httpClient,
            promptTimeout ?? DefaultPromptTimeout,
            commandTimeout ?? DefaultCommandTimeout,
            ownsHttpClient: false)
    {
    }

    private ActiveOpenCodeTransport(
        BridgeHostOptions options,
        IBridgeOpenCodeEndpointRegistrationDirectory directory,
        IOpenCodeRuntimeLifecycle lifecycle,
        HttpClient httpClient,
        TimeSpan promptTimeout,
        TimeSpan commandTimeout,
        bool ownsHttpClient)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.promptTimeout = promptTimeout > TimeSpan.Zero
            ? promptTimeout
            : throw new ArgumentOutOfRangeException(nameof(promptTimeout));
        this.commandTimeout = commandTimeout > TimeSpan.Zero
            ? commandTimeout
            : throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        this.ownsHttpClient = ownsHttpClient;
        this.httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public bool IsReady(string sessionExternalId)
    {
        EnsureAvailable(CancellationToken.None);
        return FindCurrentTarget(sessionExternalId) is { Ready: true };
    }

    public async Task SendPromptAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        Prepare(context, cancellationToken);
        ArgumentNullException.ThrowIfNull(prompt);
        var target = RequireReadyTarget(sessionExternalId);
        using var response = await SendJsonAsync(
            target,
            sessionExternalId,
            BuildScopedUri(
                target,
                $"/session/{Escape(sessionExternalId)}/prompt_async"),
            new { parts = new[] { new { type = "text", text = prompt } } },
            "发送提示",
            promptTimeout,
            cancellationToken);
        EnsureSuccess(response, "发送提示");
    }

    public async Task ResolveApprovalAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string requestId,
        string decision,
        CancellationToken cancellationToken = default)
    {
        Prepare(context, cancellationToken);
        ArgumentNullException.ThrowIfNull(requestId);
        var target = RequireReadyTarget(sessionExternalId);
        var reply = decision switch
        {
            "allow_once" => "once",
            "allow_session" => "always",
            "deny" => "reject",
            _ => throw new InvalidDataException($"未知的审批决定 {decision}。"),
        };

        using (var response = await SendJsonAsync(
                   target,
                   sessionExternalId,
                   new Uri(
                       target.Endpoint.BaseUri,
                       $"/api/session/{Escape(sessionExternalId)}/permission/{Escape(requestId)}/reply"),
                   new { reply },
                   "回复权限",
                   commandTimeout,
                   cancellationToken))
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }
            if (!IsUnsupported(response.StatusCode))
            {
                EnsureSuccess(response, "回复权限");
            }
        }

        using (var response = await SendJsonAsync(
                   target,
                   sessionExternalId,
                   BuildScopedUri(
                       target,
                       $"/permission/{Escape(requestId)}/reply"),
                   new { reply },
                   "回复权限",
                   commandTimeout,
                   cancellationToken))
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }
            if (!IsUnsupported(response.StatusCode))
            {
                EnsureSuccess(response, "回复权限");
            }
        }

        using var legacy = await SendJsonAsync(
            target,
            sessionExternalId,
            BuildScopedUri(
                target,
                $"/session/{Escape(sessionExternalId)}/permissions/{Escape(requestId)}"),
            new { response = reply },
            "回复权限",
            commandTimeout,
            cancellationToken);
        EnsureSuccess(legacy, "回复权限");
    }

    public async Task ResolveInputAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string requestId,
        IReadOnlyList<IReadOnlyList<string>> answers,
        CancellationToken cancellationToken = default)
    {
        Prepare(context, cancellationToken);
        ArgumentNullException.ThrowIfNull(requestId);
        ArgumentNullException.ThrowIfNull(answers);
        var target = RequireReadyTarget(sessionExternalId);
        using var response = await SendJsonAsync(
            target,
            sessionExternalId,
            BuildScopedUri(target, $"/question/{Escape(requestId)}/reply"),
            new { answers = answers.Select(value => value.ToArray()).ToArray() },
            "回复问题",
            commandTimeout,
            cancellationToken);
        EnsureSuccess(response, "回复问题");
    }

    public async Task LaunchAsync(
        RuntimeCommandContext context,
        string requestedExternalId,
        string cwd,
        string? prompt,
        bool elevated,
        CancellationToken cancellationToken = default)
    {
        Prepare(context, cancellationToken);
        await lifecycle.LaunchAsync(
            context,
            requestedExternalId,
            cwd,
            elevated,
            cancellationToken);
        await lifecycle.WaitUntilReadyAsync(
            context,
            requestedExternalId,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            await SendPromptAsync(
                context,
                requestedExternalId,
                prompt,
                cancellationToken);
        }
    }

    public async Task ResumeAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? prompt,
        CancellationToken cancellationToken = default)
    {
        Prepare(context, cancellationToken);
        var target = FindCurrentTarget(sessionExternalId);
        if (target is not { Ready: true })
        {
            await lifecycle.ResumeAsync(
                context,
                sessionExternalId,
                target?.Cwd,
                cancellationToken);
            await lifecycle.WaitUntilReadyAsync(
                context,
                sessionExternalId,
                cancellationToken);
        }
        else
        {
            EnsureCurrent(target, sessionExternalId);
        }
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            await SendPromptAsync(
                context,
                sessionExternalId,
                prompt,
                cancellationToken);
        }
    }

    public async Task StopAsync(
        RuntimeCommandContext context,
        string sessionExternalId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        Prepare(context, cancellationToken);
        if (FindCurrentTarget(sessionExternalId) is { Ready: true } target)
        {
            using var response = await SendAsync(
                target,
                sessionExternalId,
                BuildScopedUri(
                    target,
                    $"/session/{Escape(sessionExternalId)}/abort"),
                content: null,
                "中止会话",
                commandTimeout,
                cancellationToken);
            EnsureSuccess(response, "中止会话");
        }
        await lifecycle.StopAsync(
            context,
            sessionExternalId,
            reason,
            cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0 && ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private Task<HttpResponseMessage> SendJsonAsync<T>(
        BridgeOpenCodeEndpointIdentity target,
        string sessionExternalId,
        Uri uri,
        T value,
        string operation,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        SendAsync(
            target,
            sessionExternalId,
            uri,
            JsonContent.Create(value),
            operation,
            timeout,
            cancellationToken);

    private async Task<HttpResponseMessage> SendAsync(
        BridgeOpenCodeEndpointIdentity target,
        string sessionExternalId,
        Uri uri,
        HttpContent? content,
        string operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = content,
        };
        EnsureCurrent(target, sessionExternalId);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        HttpResponseMessage? response = null;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCancellation.Token);
            EnsureCurrent(target, sessionExternalId);
            return response;
        }
        catch (OperationCanceledException error) when (
            !cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw new TimeoutException($"{operation}超时。", error);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }

    private BridgeOpenCodeEndpointIdentity RequireReadyTarget(
        string sessionExternalId) =>
        FindCurrentTarget(sessionExternalId) is { Ready: true } target
            ? target
            : throw new InvalidOperationException("找不到对应的 OpenCode 实例。");

    private BridgeOpenCodeEndpointIdentity? FindCurrentTarget(
        string sessionExternalId)
    {
        var target = directory.FindRegistrationBySession(sessionExternalId);
        if (target is null)
        {
            return null;
        }
        ValidateTarget(target);
        if (!target.Ready)
        {
            return target;
        }
        return directory.IsCurrent(target, sessionExternalId) ? target : null;
    }

    private void EnsureCurrent(
        BridgeOpenCodeEndpointIdentity target,
        string sessionExternalId)
    {
        EnsureAvailable(CancellationToken.None);
        if (!directory.IsCurrent(target, sessionExternalId))
        {
            throw new InvalidOperationException(
                "OpenCode 端点代际或会话归属已变化，已拒绝命令以避免串线。");
        }
    }

    private void Prepare(
        RuntimeCommandContext context,
        CancellationToken cancellationToken)
    {
        EnsureAvailable(cancellationToken);
        ArgumentNullException.ThrowIfNull(context);
    }

    private void EnsureAvailable(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) == 1, this);
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "OpenCode 生产传输只能用于 Active Host。");
        }
    }

    private static void ValidateTarget(BridgeOpenCodeEndpointIdentity target)
    {
        if (target.Port is <= 0 or > 65_535 ||
            target.Generation <= 0 ||
            string.IsNullOrWhiteSpace(target.Cwd) ||
            target.Cwd.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(target.Cwd))
        {
            throw new InvalidOperationException("OpenCode 传输目标身份无效。");
        }
        ActiveOpenCodeEventSource.ValidateEndpoint(target.Endpoint);
    }

    private static Uri BuildScopedUri(
        BridgeOpenCodeEndpointIdentity target,
        string path)
    {
        var builder = new UriBuilder(new Uri(target.Endpoint.BaseUri, path))
        {
            Query = $"directory={Uri.EscapeDataString(target.Cwd)}",
        };
        return builder.Uri;
    }

    private static void EnsureSuccess(
        HttpResponseMessage response,
        string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{operation}: HTTP {(int)response.StatusCode}。",
                inner: null,
                response.StatusCode);
        }
    }

    private static bool IsUnsupported(HttpStatusCode status) =>
        status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed;

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
