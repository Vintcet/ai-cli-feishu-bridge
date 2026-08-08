using System.Diagnostics;
using System.Text.Json;

namespace AiCliFeishuControl;

internal sealed class BridgeHostProductionCutover : IDisposable
{
    private const int MaximumControlTokenFileBytes = 4 * 1024;

    private readonly BridgeHostPersistentCutoverCoordinator cutoverCoordinator;
    private readonly BridgeHostCutoverProcessOperations processOperations;
    private readonly BridgeHostRecoveryObserver recoveryObserver;
    private readonly BridgeHostRecoveryExecutor recoveryExecutor;
    private bool disposed;

    public BridgeHostProductionCutover(
        string bridgeRoot,
        string applicationDirectory,
        int productionPort)
        : this(
            bridgeRoot,
            applicationDirectory,
            productionPort,
            startProcess: null,
            processHandler: null,
            recoveryHandler: null)
    {
    }

    internal BridgeHostProductionCutover(
        string bridgeRoot,
        string applicationDirectory,
        int productionPort,
        Func<ProcessStartInfo, Process?>? startProcess,
        HttpMessageHandler? processHandler,
        HttpMessageHandler? recoveryHandler)
    {
        if (string.IsNullOrWhiteSpace(bridgeRoot))
        {
            throw new ArgumentException("生产 Bridge 根目录不能为空。", nameof(bridgeRoot));
        }
        if (string.IsNullOrWhiteSpace(applicationDirectory))
        {
            throw new ArgumentException(
                "桌面程序目录不能为空。",
                nameof(applicationDirectory));
        }
        if (productionPort is <= 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(productionPort),
                "生产 Bridge 端口必须在 1 到 65535 之间。");
        }

        BridgeRoot = Path.GetFullPath(bridgeRoot);
        ApplicationDirectory = Path.GetFullPath(applicationDirectory);
        DataDirectory = Path.Combine(BridgeRoot, "data");
        Port = productionPort;
        Endpoint = new Uri(
            $"http://127.0.0.1:{productionPort}/",
            UriKind.Absolute);
        NodeTarget = BridgeHostTarget.NodeProduction(productionPort);
        DotNetTarget = BridgeHostTarget.DotNetProduction(productionPort);

        var controlToken = ReadControlToken(DataDirectory);
        StoreHandoffInspector = new ProductionBridgeStoreHandoffInspector(DataDirectory);
        var options = new BridgeHostCutoverProcessOptions(
            Endpoint,
            controlToken,
            StoreHandoffInspector,
            CreateNodeStartInfo,
            CreateDotNetStartInfo,
            startProcess);

        BridgeHostCutoverProcessOperations? operations = null;
        BridgeHostRecoveryObserver? observer = null;
        try
        {
            operations = new BridgeHostCutoverProcessOperations(options, processHandler);
            observer = new BridgeHostRecoveryObserver(
                DataDirectory,
                Endpoint,
                controlToken,
                recoveryHandler);
            processOperations = operations;
            recoveryObserver = observer;
            cutoverCoordinator = new BridgeHostPersistentCutoverCoordinator(
                DataDirectory,
                processOperations);
            recoveryExecutor = new BridgeHostRecoveryExecutor(
                DataDirectory,
                recoveryObserver,
                processOperations);
        }
        catch
        {
            observer?.Dispose();
            operations?.Dispose();
            throw;
        }
    }

    public string BridgeRoot { get; }

    public string ApplicationDirectory { get; }

    public string DataDirectory { get; }

    public int Port { get; }

    public Uri Endpoint { get; }

    public BridgeHostTarget NodeTarget { get; }

    public BridgeHostTarget DotNetTarget { get; }

    internal IBridgeStoreHandoffInspector StoreHandoffInspector { get; }

    public ValueTask<BridgeHostPersistentCutoverResult> CutoverAsync(
        BridgeCutoverHostIdentity expectedNode,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(expectedNode);
        if (!expectedNode.IsNodeActive(NodeTarget.ManagementApiVersion) ||
            !string.Equals(
                expectedNode.InstanceName,
                NodeTarget.InstanceName,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "生产切换必须绑定已认证的预期 Node 生产实例。",
                nameof(expectedNode));
        }
        return cutoverCoordinator.RunAsync(
            expectedNode,
            DotNetTarget.InstanceName,
            cancellationToken);
    }

    public void ValidateStartPrerequisites()
    {
        ThrowIfDisposed();
        _ = CreateNodeStartInfo();
        _ = CreateDotNetStartInfo(DotNetTarget.InstanceName);
    }

    public ValueTask<BridgeHostRecoveryInspection> InspectRecoveryAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return recoveryObserver.InspectAsync(cancellationToken);
    }

    public ValueTask<BridgeHostRecoveryExecutionResult> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return recoveryExecutor.RunAsync(cancellationToken);
    }

    internal ProcessStartInfo CreateNodeStartInfo() =>
        NodeTarget.CreateStartInfo(BridgeRoot, ApplicationDirectory);

    internal ProcessStartInfo CreateDotNetStartInfo(string instanceName)
    {
        if (!string.Equals(
                instanceName,
                DotNetTarget.InstanceName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "C# 生产 Host 启动实例与组合根绑定不一致。已拒绝启动。");
        }
        return DotNetTarget.CreateStartInfo(BridgeRoot, ApplicationDirectory);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        recoveryObserver.Dispose();
        processOperations.Dispose();
    }

    private static string ReadControlToken(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "control-token.json");
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException();
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumControlTokenFileBytes)
            {
                throw new InvalidDataException();
            }

            using var document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                throw new InvalidDataException();
            }

            string? token = null;
            var tokenPropertyCount = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("token"))
                {
                    continue;
                }
                tokenPropertyCount++;
                token = property.Value.ValueKind is JsonValueKind.String
                    ? property.Value.GetString()
                    : null;
            }
            if (tokenPropertyCount != 1 ||
                token is null ||
                token.Length != 64 ||
                token.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException();
            }
            return token;
        }
        catch (Exception error) when (
            error is IOException or
                UnauthorizedAccessException or
                JsonException or
                InvalidDataException)
        {
            throw new InvalidOperationException(
                "生产切换所需的本机控制令牌缺失或格式无效。");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
