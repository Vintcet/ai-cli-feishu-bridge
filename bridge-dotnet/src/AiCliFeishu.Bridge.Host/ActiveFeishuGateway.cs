using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.Host;

internal sealed class ActiveFeishuGateway : IFeishuGateway, IDisposable
{
    private readonly BridgeHostOptions options;
    private readonly IBridgeFeishuCredentialSource credentials;
    private readonly Func<BridgeFeishuCredentials, IFeishuGateway> createGateway;
    private readonly Lazy<IFeishuGateway> gateway;
    private readonly HttpClient? ownedHttpClient;
    private int disposed;

    public ActiveFeishuGateway(
        BridgeHostOptions options,
        IBridgeFeishuCredentialSource credentials)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.credentials = credentials ??
            throw new ArgumentNullException(nameof(credentials));
        ownedHttpClient = new HttpClient();
        createGateway = value => new HttpFeishuGateway(
            ownedHttpClient,
            CreateOptions(value));
        gateway = NewLazyGateway();
    }

    internal ActiveFeishuGateway(
        BridgeHostOptions options,
        IBridgeFeishuCredentialSource credentials,
        Func<BridgeFeishuCredentials, IFeishuGateway> createGateway)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.credentials = credentials ??
            throw new ArgumentNullException(nameof(credentials));
        this.createGateway = createGateway ??
            throw new ArgumentNullException(nameof(createGateway));
        gateway = NewLazyGateway();
    }

    public Task<string> SendTextAsync(
        string chatId,
        string text,
        CancellationToken cancellationToken = default) =>
        GetGateway(cancellationToken).SendTextAsync(
            chatId,
            text,
            cancellationToken);

    public Task<string> ReplyTextAsync(
        string messageId,
        string text,
        CancellationToken cancellationToken = default) =>
        GetGateway(cancellationToken).ReplyTextAsync(
            messageId,
            text,
            cancellationToken);

    public Task<string> SendCardAsync(
        string chatId,
        FeishuCardView card,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        GetGateway(cancellationToken).SendCardAsync(
            chatId,
            card,
            idempotencyKey,
            cancellationToken);

    public Task PatchCardAsync(
        string messageId,
        FeishuCardView card,
        CancellationToken cancellationToken = default) =>
        GetGateway(cancellationToken).PatchCardAsync(
            messageId,
            card,
            cancellationToken);

    public Task<FeishuSessionGroup> CreateSessionGroupAsync(
        string ownerOpenId,
        string name,
        string description,
        CancellationToken cancellationToken = default) =>
        GetGateway(cancellationToken).CreateSessionGroupAsync(
            ownerOpenId,
            name,
            description,
            cancellationToken);

    public Task UpdateSessionGroupNameAsync(
        string chatId,
        string name,
        CancellationToken cancellationToken = default) =>
        GetGateway(cancellationToken).UpdateSessionGroupNameAsync(
            chatId,
            name,
            cancellationToken);

    public Task DeleteSessionGroupAsync(
        string chatId,
        CancellationToken cancellationToken = default) =>
        GetGateway(cancellationToken).DeleteSessionGroupAsync(
            chatId,
            cancellationToken);

    public Task<long> DownloadMessageResourceAsync(
        string messageId,
        string fileKey,
        string resourceType,
        string destinationPath,
        long maxBytes,
        CancellationToken cancellationToken = default) =>
        GetGateway(cancellationToken).DownloadMessageResourceAsync(
            messageId,
            fileKey,
            resourceType,
            destinationPath,
            maxBytes,
            cancellationToken);

    public Task<string> SendLocalFileAsync(
        string chatId,
        string filePath,
        CancellationToken cancellationToken = default) =>
        GetGateway(cancellationToken).SendLocalFileAsync(
            chatId,
            filePath,
            cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            ownedHttpClient?.Dispose();
        }
    }

    internal static FeishuGatewayOptions CreateOptions(
        BridgeFeishuCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return new(
            credentials.AppId,
            credentials.AppSecret,
            FeishuGatewayOptions.DefaultBaseUri);
    }

    private Lazy<IFeishuGateway> NewLazyGateway() => new(
        CreateGateway,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private IFeishuGateway GetGateway(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) == 1, this);
        return gateway.Value;
    }

    private IFeishuGateway CreateGateway()
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "飞书生产发送 Gateway 只能用于 Active Host。");
        }
        return createGateway(credentials.Credentials) ??
            throw new InvalidOperationException("飞书生产发送 Gateway 工厂不能返回 null。");
    }
}
