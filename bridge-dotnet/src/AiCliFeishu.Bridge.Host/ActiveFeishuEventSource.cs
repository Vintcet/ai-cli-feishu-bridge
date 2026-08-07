using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.Host;

internal sealed class ActiveFeishuEventSource : IFeishuEventSource, IDisposable
{
    private readonly BridgeHostOptions options;
    private readonly IBridgeFeishuCredentialSource credentials;
    private readonly Func<BridgeFeishuCredentials, IFeishuEventSource> createSource;
    private readonly Lazy<IFeishuEventSource> source;
    private readonly HttpClient? ownedHttpClient;
    private int disposed;

    public ActiveFeishuEventSource(
        BridgeHostOptions options,
        IBridgeFeishuCredentialSource credentials)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.credentials = credentials ??
            throw new ArgumentNullException(nameof(credentials));
        ownedHttpClient = new HttpClient();
        var connections = new ClientFeishuWebSocketConnectionFactory();
        createSource = value =>
        {
            var endpoint = new HttpFeishuWebSocketEndpointProvider(
                ownedHttpClient,
                CreateOptions(value));
            return new FeishuWebSocketEventSource(endpoint, connections);
        };
        source = NewLazySource();
    }

    internal ActiveFeishuEventSource(
        BridgeHostOptions options,
        IBridgeFeishuCredentialSource credentials,
        Func<BridgeFeishuCredentials, IFeishuEventSource> createSource)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.credentials = credentials ??
            throw new ArgumentNullException(nameof(credentials));
        this.createSource = createSource ??
            throw new ArgumentNullException(nameof(createSource));
        source = NewLazySource();
    }

    public IAsyncEnumerable<FeishuInboundEnvelope> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) == 1, this);
        return source.Value.ReadAllAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            ownedHttpClient?.Dispose();
        }
    }

    internal static FeishuWebSocketOptions CreateOptions(
        BridgeFeishuCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return new(
            credentials.AppId,
            credentials.AppSecret,
            FeishuGatewayOptions.DefaultBaseUri);
    }

    private Lazy<IFeishuEventSource> NewLazySource() => new(
        CreateSource,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private IFeishuEventSource CreateSource()
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "飞书生产事件流只能用于 Active Host。");
        }
        return createSource(credentials.Credentials) ??
            throw new InvalidOperationException("飞书生产事件流工厂不能返回 null。");
    }
}
