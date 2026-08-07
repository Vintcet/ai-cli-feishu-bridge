using System.Runtime.CompilerServices;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.OpenCode;

namespace AiCliFeishu.Bridge.Host;

internal interface IBridgeOpenCodeEventStreamOwner : IOpenCodeEventSource
{
    ValueTask<bool> ProbeHealthAsync(
        OpenCodeEndpoint endpoint,
        CancellationToken cancellationToken = default);
}

internal sealed class ActiveOpenCodeEventSource :
    IBridgeOpenCodeEventStreamOwner,
    IDisposable
{
    private const int DefaultMaximumHealthBodyBytes = 64 * 1024;
    private static readonly TimeSpan DefaultHealthTimeout = TimeSpan.FromSeconds(3);
    private readonly BridgeHostOptions options;
    private readonly HttpClient httpClient;
    private readonly HttpOpenCodeEventSource eventSource;
    private readonly TimeSpan healthTimeout;
    private readonly int maximumHealthBodyBytes;
    private readonly bool ownsHttpClient;
    private int disposed;

    public ActiveOpenCodeEventSource(BridgeHostOptions options)
        : this(
            options,
            CreateHttpClient(),
            DefaultHealthTimeout,
            DefaultMaximumHealthBodyBytes,
            ownsHttpClient: true)
    {
    }

    internal ActiveOpenCodeEventSource(
        BridgeHostOptions options,
        HttpClient httpClient,
        TimeSpan? healthTimeout = null,
        int maximumHealthBodyBytes = DefaultMaximumHealthBodyBytes)
        : this(
            options,
            httpClient,
            healthTimeout ?? DefaultHealthTimeout,
            maximumHealthBodyBytes,
            ownsHttpClient: false)
    {
    }

    private ActiveOpenCodeEventSource(
        BridgeHostOptions options,
        HttpClient httpClient,
        TimeSpan healthTimeout,
        int maximumHealthBodyBytes,
        bool ownsHttpClient)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.httpClient = httpClient ??
            throw new ArgumentNullException(nameof(httpClient));
        this.healthTimeout = healthTimeout > TimeSpan.Zero
            ? healthTimeout
            : throw new ArgumentOutOfRangeException(nameof(healthTimeout));
        this.maximumHealthBodyBytes = maximumHealthBodyBytes > 0
            ? maximumHealthBodyBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumHealthBodyBytes));
        this.ownsHttpClient = ownsHttpClient;
        this.httpClient.Timeout = Timeout.InfiniteTimeSpan;
        eventSource = new HttpOpenCodeEventSource(this.httpClient);
    }

    public IAsyncEnumerable<OpenCodeRawEvent> ReadAllAsync(
        OpenCodeEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable(endpoint, cancellationToken);
        return ReadValidatedAsync(endpoint, cancellationToken);
    }

    public async ValueTask<bool> ProbeHealthAsync(
        OpenCodeEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable(endpoint, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(healthTimeout);
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(endpoint.BaseUri, "/global/health"));
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength > maximumHealthBodyBytes)
            {
                return false;
            }
            var body = await ReadLimitedBodyAsync(
                response.Content,
                maximumHealthBodyBytes,
                timeout.Token);
            if (body is null || body.Length == 0)
            {
                return body is not null;
            }
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind is not JsonValueKind.Object)
                {
                    return true;
                }
                return !IsFalse(document.RootElement, "healthy") &&
                    !IsFalse(document.RootElement, "ok");
            }
            catch (JsonException)
            {
                return true;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error) when (
            error is HttpRequestException or IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0 && ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private async IAsyncEnumerable<OpenCodeRawEvent> ReadValidatedAsync(
        OpenCodeEndpoint endpoint,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var rawEvent in eventSource.ReadAllAsync(
                           endpoint,
                           cancellationToken))
        {
            yield return rawEvent;
        }
    }

    private void EnsureAvailable(
        OpenCodeEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) == 1, this);
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "OpenCode 生产事件流只能用于 Active Host。");
        }
        ValidateEndpoint(endpoint);
    }

    internal static void ValidateEndpoint(OpenCodeEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var uri = endpoint.BaseUri;
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
            !string.Equals(uri.IdnHost, "127.0.0.1", StringComparison.Ordinal) ||
            uri.Port is <= 0 or > 65_535 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "OpenCode 事件端点必须是固定 IPv4 回环 HTTP Origin。");
        }
    }

    private static async ValueTask<byte[]?> ReadLimitedBodyAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(8 * 1024, maximumBytes)];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }
            if (buffer.Length + read > maximumBytes)
            {
                return null;
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool IsFalse(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.False;

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = DefaultHealthTimeout,
            UseCookies = false,
            UseProxy = false,
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
