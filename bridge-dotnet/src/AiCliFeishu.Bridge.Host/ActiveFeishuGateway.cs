using AiCliFeishu.Bridge.Adapters.Feishu;

namespace AiCliFeishu.Bridge.Host;

internal sealed class ActiveFeishuGateway : IFeishuGateway, IFeishuPriorityGateway, IDisposable
{
    private readonly BridgeHostOptions options;
    private readonly IBridgeFeishuCredentialSource credentials;
    private readonly Func<BridgeFeishuCredentials, IFeishuGateway> createGateway;
    private readonly Lazy<IFeishuGateway> gateway;
    private readonly HttpClient? ownedHttpClient;
    private readonly FeishuMessageDispatcher dispatcher = new();
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
        SendTextAsync(chatId, text, FeishuMessagePriority.Normal, cancellationToken);

    public Task<string> SendTextAsync(
        string chatId,
        string text,
        FeishuMessagePriority priority,
        CancellationToken cancellationToken = default) =>
        EnqueueMessage(
            priority,
            token => GetGateway(token).SendTextAsync(chatId, text, token),
            cancellationToken);

    public Task<string> ReplyTextAsync(
        string messageId,
        string text,
        CancellationToken cancellationToken = default) =>
        ReplyTextAsync(messageId, text, FeishuMessagePriority.Normal, cancellationToken);

    public Task<string> ReplyTextAsync(
        string messageId,
        string text,
        FeishuMessagePriority priority,
        CancellationToken cancellationToken = default) =>
        EnqueueMessage(
            priority,
            token => GetGateway(token).ReplyTextAsync(messageId, text, token),
            cancellationToken);

    public Task<string> SendCardAsync(
        string chatId,
        FeishuCardView card,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        SendCardAsync(
            chatId,
            card,
            idempotencyKey,
            FeishuMessagePriority.Normal,
            cancellationToken);

    public Task<string> SendCardAsync(
        string chatId,
        FeishuCardView card,
        string? idempotencyKey,
        FeishuMessagePriority priority,
        CancellationToken cancellationToken = default) =>
        EnqueueMessage(
            priority,
            token => GetGateway(token).SendCardAsync(
                chatId,
                card,
                idempotencyKey,
                token),
            cancellationToken);

    public Task PatchCardAsync(
        string messageId,
        FeishuCardView card,
        CancellationToken cancellationToken = default) =>
        PatchCardAsync(
            messageId,
            card,
            FeishuMessagePriority.Normal,
            cancellationToken);

    public async Task PatchCardAsync(
        string messageId,
        FeishuCardView card,
        FeishuMessagePriority priority,
        CancellationToken cancellationToken = default) =>
        _ = await EnqueueMessage(
            priority,
            async token =>
            {
                await GetGateway(token).PatchCardAsync(messageId, card, token);
                return true;
            },
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
        SendLocalFileAsync(
            chatId,
            filePath,
            FeishuMessagePriority.Normal,
            cancellationToken);

    public Task<string> SendLocalFileAsync(
        string chatId,
        string filePath,
        FeishuMessagePriority priority,
        CancellationToken cancellationToken = default) =>
        EnqueueMessage(
            priority,
            token => GetGateway(token).SendLocalFileAsync(chatId, filePath, token),
            cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            dispatcher.Dispose();
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

    private Task<T> EnqueueMessage<T>(
        FeishuMessagePriority priority,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) == 1, this);
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "飞书生产发送 Gateway 只能用于 Active Host。");
        }
        return dispatcher.Enqueue(priority, operation, cancellationToken);
    }

    private sealed class FeishuMessageDispatcher : IDisposable
    {
        private readonly object sync = new();
        private readonly PriorityQueue<IWorkItem, (int Rank, long Sequence)> pending = new();
        private readonly SemaphoreSlim available = new(0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task worker;
        private long sequence;
        private bool disposed;

        public FeishuMessageDispatcher() => worker = Task.Run(ProcessAsync);

        public Task<T> Enqueue<T>(
            FeishuMessagePriority priority,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkItem<T> item;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                item = new(operation, cancellationToken);
                pending.Enqueue(item, (Rank(priority), sequence++));
                available.Release();
            }
            return item.Completion.Task.WaitAsync(cancellationToken);
        }

        public void Dispose()
        {
            IWorkItem[] abandoned;
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                abandoned = pending.UnorderedItems
                    .Select(item => item.Element)
                    .ToArray();
                pending.Clear();
            }
            foreach (var item in abandoned)
            {
                item.Cancel();
            }
            lifetime.Cancel();
        }

        private async Task ProcessAsync()
        {
            try
            {
                while (true)
                {
                    await available.WaitAsync(lifetime.Token);
                    IWorkItem item;
                    lock (sync)
                    {
                        if (pending.Count == 0)
                        {
                            continue;
                        }
                        item = pending.Dequeue();
                    }
                    await item.RunAsync();
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                available.Dispose();
                lifetime.Dispose();
            }
        }

        private static int Rank(FeishuMessagePriority priority) => priority switch
        {
            FeishuMessagePriority.High => 0,
            FeishuMessagePriority.Normal => 1,
            FeishuMessagePriority.Low => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(priority)),
        };

        private interface IWorkItem
        {
            Task RunAsync();

            void Cancel();
        }

        private sealed class WorkItem<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken) : IWorkItem
        {
            public TaskCompletionSource<T> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task RunAsync()
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Completion.TrySetCanceled(cancellationToken);
                    return;
                }
                try
                {
                    Completion.TrySetResult(await operation(cancellationToken));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception error)
                {
                    Completion.TrySetException(error);
                }
            }

            public void Cancel() => Completion.TrySetCanceled(cancellationToken);
        }
    }
}
