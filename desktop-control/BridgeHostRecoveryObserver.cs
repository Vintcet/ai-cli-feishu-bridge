using System.Net.Sockets;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishuControl;

internal sealed record BridgeHostRecoveryInspection(
    BridgeHostCutoverCheckpointReadState CheckpointState,
    BridgeHostRecoveryPlan Plan,
    string? CheckpointFileVersion = null);

internal sealed record BridgeHostRecoveryExecutionInspection(
    BridgeHostRecoveryInspection Inspection,
    BridgeCutoverHostIdentity? ObservedIdentity);

internal sealed class BridgeHostRecoveryEndpointProbe : IDisposable
{
    private const string ControlTokenHeader = "X-AI-CLI-Feishu-Control-Token";

    private readonly HttpClient httpClient;
    private readonly string controlToken;

    public BridgeHostRecoveryEndpointProbe(
        Uri endpoint,
        string controlToken,
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null)
    {
        ValidateEndpoint(endpoint);
        if (!IsSafeHeaderValue(controlToken))
        {
            throw new ArgumentException(
                "恢复观察器的本机控制令牌无效。",
                nameof(controlToken));
        }
        if (timeout is { } configuredTimeout && configuredTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        this.controlToken = controlToken;
        httpClient = handler is null
            ? new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
            })
            : new HttpClient(handler, disposeHandler: true);
        httpClient.BaseAddress = endpoint;
        httpClient.Timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    public async ValueTask<BridgeHostRecoveryEndpointObservation> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "health");
            request.Headers.Add(ControlTokenHeader, controlToken);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return BridgeHostRecoveryEndpointObservation.Uncertain();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            return TryReadIdentity(document.RootElement, out var identity)
                ? BridgeHostRecoveryEndpointObservation.Authenticated(identity)
                : BridgeHostRecoveryEndpointObservation.Uncertain();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException error) when (IsConnectionRefused(error))
        {
            return BridgeHostRecoveryEndpointObservation.Offline();
        }
        catch (Exception error) when (
            error is HttpRequestException or OperationCanceledException or
                IOException or JsonException)
        {
            return BridgeHostRecoveryEndpointObservation.Uncertain();
        }
    }

    public void Dispose() => httpClient.Dispose();

    private static bool TryReadIdentity(
        JsonElement root,
        out BridgeCutoverHostIdentity identity)
    {
        identity = null!;
        if (root.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        string? hostKind = null;
        string? ownershipMode = null;
        string? instanceName = null;
        var processId = 0;
        var managementApiVersion = 0;
        var activeOwner = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "hostKind":
                    if (!seen.Add(property.Name) ||
                        property.Value.ValueKind is not JsonValueKind.String)
                    {
                        return false;
                    }
                    hostKind = property.Value.GetString();
                    break;
                case "managementApiVersion":
                    if (!seen.Add(property.Name) ||
                        !property.Value.TryGetInt32(out managementApiVersion))
                    {
                        return false;
                    }
                    break;
                case "instanceName":
                    if (!seen.Add(property.Name) ||
                        property.Value.ValueKind is not JsonValueKind.String)
                    {
                        return false;
                    }
                    instanceName = property.Value.GetString();
                    break;
                case "processId":
                    if (!seen.Add(property.Name) ||
                        !property.Value.TryGetInt32(out processId))
                    {
                        return false;
                    }
                    break;
                case "ownershipMode":
                    if (!seen.Add(property.Name) ||
                        property.Value.ValueKind is not JsonValueKind.String)
                    {
                        return false;
                    }
                    ownershipMode = property.Value.GetString();
                    break;
                case "activeOwner":
                    if (!seen.Add(property.Name) ||
                        property.Value.ValueKind is not (
                            JsonValueKind.True or JsonValueKind.False))
                    {
                        return false;
                    }
                    activeOwner = property.Value.GetBoolean();
                    break;
            }
        }

        if (seen.Count is not 6 ||
            processId <= 0 ||
            managementApiVersion <= 0 ||
            string.IsNullOrWhiteSpace(hostKind) ||
            string.IsNullOrWhiteSpace(ownershipMode) ||
            string.IsNullOrWhiteSpace(instanceName))
        {
            return false;
        }

        identity = new(
            processId,
            hostKind,
            managementApiVersion,
            ownershipMode,
            activeOwner,
            instanceName);
        return true;
    }

    private static bool IsConnectionRefused(HttpRequestException error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket &&
                socket.SocketErrorCode is SocketError.ConnectionRefused)
            {
                return true;
            }
        }
        return false;
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
            !endpoint.IsLoopback ||
            endpoint.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new ArgumentException(
                "恢复观察器只能连接本机回环 HTTP Origin。",
                nameof(endpoint));
        }
    }

    private static bool IsSafeHeaderValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character => character is >= '!' and <= '~');
}

internal sealed class BridgeHostRecoveryObserver : IDisposable
{
    private readonly Func<CancellationToken, ValueTask<BridgeHostCutoverCheckpointReadResult>>
        readCheckpoint;
    private readonly Func<CancellationToken, ValueTask<BridgeHostRecoveryEndpointObservation>>
        inspectEndpoint;
    private readonly Func<CancellationToken, ValueTask<ActiveOwnerLeaseSnapshot>> inspectLease;
    private readonly IDisposable? ownedResource;

    public BridgeHostRecoveryObserver(
        string dataDirectory,
        Uri endpoint,
        string controlToken,
        HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException(
                "恢复观察器的数据目录不能为空。",
                nameof(dataDirectory));
        }

        var fullDataDirectory = Path.GetFullPath(dataDirectory);
        var checkpointStore = new BridgeHostCutoverCheckpointStore(fullDataDirectory);
        var endpointProbe = new BridgeHostRecoveryEndpointProbe(
            endpoint,
            controlToken,
            handler);
        var leaseObserver = new ActiveOwnerLeaseObserver(fullDataDirectory);
        readCheckpoint = checkpointStore.ReadAsync;
        inspectEndpoint = endpointProbe.InspectAsync;
        inspectLease = leaseObserver.InspectAsync;
        ownedResource = endpointProbe;
    }

    internal BridgeHostRecoveryObserver(
        Func<CancellationToken, ValueTask<BridgeHostCutoverCheckpointReadResult>>
            readCheckpoint,
        Func<CancellationToken, ValueTask<BridgeHostRecoveryEndpointObservation>>
            inspectEndpoint,
        Func<CancellationToken, ValueTask<ActiveOwnerLeaseSnapshot>> inspectLease)
    {
        this.readCheckpoint = readCheckpoint ??
            throw new ArgumentNullException(nameof(readCheckpoint));
        this.inspectEndpoint = inspectEndpoint ??
            throw new ArgumentNullException(nameof(inspectEndpoint));
        this.inspectLease = inspectLease ??
            throw new ArgumentNullException(nameof(inspectLease));
    }

    public async ValueTask<BridgeHostRecoveryInspection> InspectAsync(
        CancellationToken cancellationToken = default) =>
        (await InspectForExecutionAsync(cancellationToken)).Inspection;

    internal async ValueTask<BridgeHostRecoveryExecutionInspection>
        InspectForExecutionAsync(
        CancellationToken cancellationToken = default)
    {
        var checkpointBefore = await readCheckpoint(cancellationToken);
        if (!TryGetCheckpoint(
                checkpointBefore,
                out var checkpoint,
                out var checkpointFailure))
        {
            return Execution(Manual(checkpointBefore.State, checkpointFailure));
        }

        var endpointBefore = await inspectEndpoint(cancellationToken);
        var leaseBefore = await inspectLease(cancellationToken);
        var endpointAfter = await inspectEndpoint(cancellationToken);
        var leaseAfter = await inspectLease(cancellationToken);
        var checkpointAfter = await readCheckpoint(cancellationToken);

        if (checkpointAfter.State is not BridgeHostCutoverCheckpointReadState.Present ||
            checkpointAfter.Checkpoint != checkpoint ||
            !string.Equals(
                checkpointAfter.FileVersion,
                checkpointBefore.FileVersion,
                StringComparison.Ordinal))
        {
            return Execution(Manual(
                NormalizeCheckpointState(checkpointAfter.State),
                BridgeHostRecoveryReason.CheckpointChanged));
        }
        if (!IsValidEndpoint(endpointBefore) || !IsValidEndpoint(endpointAfter))
        {
            return Execution(Manual(
                BridgeHostCutoverCheckpointReadState.Present,
                BridgeHostRecoveryReason.EndpointUncertain));
        }
        if (!IsValidLease(leaseBefore) || !IsValidLease(leaseAfter))
        {
            return Execution(Manual(
                BridgeHostCutoverCheckpointReadState.Present,
                BridgeHostRecoveryReason.ActiveOwnerLeaseInvalid));
        }
        if (endpointBefore != endpointAfter || leaseBefore != leaseAfter)
        {
            return Execution(Manual(
                BridgeHostCutoverCheckpointReadState.Present,
                BridgeHostRecoveryReason.ObservationChanged));
        }

        var planner = new BridgeHostRecoveryPlanner(
            checkpoint.ExpectedNode.InstanceName,
            checkpoint.ExpectedDotNetInstanceName);
        var plan = planner.Plan(
            checkpoint.ToSnapshot(),
            new BridgeHostRecoveryObservation(endpointAfter, leaseAfter));
        return Execution(
            new(
                BridgeHostCutoverCheckpointReadState.Present,
                plan,
                checkpointAfter.FileVersion),
            endpointAfter.Identity);
    }

    public void Dispose() => ownedResource?.Dispose();

    private static bool TryGetCheckpoint(
        BridgeHostCutoverCheckpointReadResult result,
        out BridgeHostCutoverCheckpoint checkpoint,
        out BridgeHostRecoveryReason failure)
    {
        checkpoint = null!;
        failure = result.State switch
        {
            BridgeHostCutoverCheckpointReadState.Missing =>
                BridgeHostRecoveryReason.CheckpointMissing,
            BridgeHostCutoverCheckpointReadState.Unavailable =>
                BridgeHostRecoveryReason.CheckpointUnavailable,
            _ => BridgeHostRecoveryReason.InvalidCheckpoint,
        };
        if (result.State is not BridgeHostCutoverCheckpointReadState.Present ||
            !BridgeHostCutoverCheckpointValidator.IsValid(result.Checkpoint))
        {
            return false;
        }

        checkpoint = result.Checkpoint!;
        return true;
    }

    private static bool IsValidEndpoint(BridgeHostRecoveryEndpointObservation? endpoint)
    {
        try
        {
            _ = endpoint?.Validate() ??
                throw new InvalidOperationException("恢复端点观察为空。");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsValidLease(ActiveOwnerLeaseSnapshot? lease)
    {
        try
        {
            _ = new BridgeHostRecoveryObservation(
                BridgeHostRecoveryEndpointObservation.Offline(),
                lease ?? throw new InvalidOperationException("恢复租约观察为空。"))
                .Validate();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static BridgeHostCutoverCheckpointReadState NormalizeCheckpointState(
        BridgeHostCutoverCheckpointReadState state) =>
        Enum.IsDefined(state)
            ? state
            : BridgeHostCutoverCheckpointReadState.Invalid;

    private static BridgeHostRecoveryInspection Manual(
        BridgeHostCutoverCheckpointReadState checkpointState,
        BridgeHostRecoveryReason reason) =>
        new(
            NormalizeCheckpointState(checkpointState),
            new BridgeHostRecoveryPlan(
                BridgeHostRecoveryDisposition.ManualIntervention,
                reason));

    private static BridgeHostRecoveryExecutionInspection Execution(
        BridgeHostRecoveryInspection inspection,
        BridgeCutoverHostIdentity? observedIdentity = null) =>
        new(inspection, observedIdentity);
}
