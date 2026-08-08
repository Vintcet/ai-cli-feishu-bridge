using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiCliFeishuControl;

internal interface IBridgeStoreHandoffInspector
{
    ValueTask<BridgeStoreHandoffEvidence> InspectAsync(
        CancellationToken cancellationToken);
}

internal sealed record BridgeHostCutoverProcessOptions(
    Uri Endpoint,
    string ControlToken,
    IBridgeStoreHandoffInspector StoreHandoffInspector,
    Func<ProcessStartInfo> CreateNodeStartInfo,
    Func<string, ProcessStartInfo> CreateDotNetStartInfo,
    Func<ProcessStartInfo, Process?>? StartProcess = null,
    int MaxProbeAttempts = BridgeHostExitWaiter.DefaultMaxAttempts,
    TimeSpan? PollInterval = null)
{
    public BridgeHostCutoverProcessOptions Validate()
    {
        if (!Endpoint.IsAbsoluteUri ||
            !string.Equals(Endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
            !Endpoint.IsLoopback ||
            Endpoint.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(Endpoint.Query) ||
            !string.IsNullOrEmpty(Endpoint.Fragment) ||
            !string.IsNullOrEmpty(Endpoint.UserInfo))
        {
            throw new ArgumentException(
                "切换协调器只能连接本机回环 HTTP 端点。",
                nameof(Endpoint));
        }
        if (string.IsNullOrWhiteSpace(ControlToken))
        {
            throw new ArgumentException("本机控制令牌不能为空。", nameof(ControlToken));
        }
        ArgumentNullException.ThrowIfNull(StoreHandoffInspector);
        ArgumentNullException.ThrowIfNull(CreateNodeStartInfo);
        ArgumentNullException.ThrowIfNull(CreateDotNetStartInfo);
        if (MaxProbeAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxProbeAttempts));
        }
        if (PollInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        }
        return this;
    }
}

internal sealed class BridgeHostCutoverProcessOperations :
    IBridgeHostPersistentCutoverOperations,
    IDisposable
{
    private const string ControlTokenHeader = "X-AI-CLI-Feishu-Control-Token";
    private const string ExpectedHostKindHeader =
        "X-AI-CLI-Feishu-Expected-Host-Kind";
    private const string ManagementApiVersionHeader =
        "X-AI-CLI-Feishu-Management-Api-Version";
    private const string ExpectedProcessIdHeader =
        "X-AI-CLI-Feishu-Expected-Process-Id";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly BridgeHostCutoverProcessOptions options;
    private readonly HttpClient httpClient;
    private readonly Func<ProcessStartInfo, Process?> startProcess;

    public BridgeHostCutoverProcessOperations(
        BridgeHostCutoverProcessOptions options,
        HttpMessageHandler? handler = null)
    {
        this.options = (options ?? throw new ArgumentNullException(nameof(options)))
            .Validate();
        startProcess = options.StartProcess ?? Process.Start;
        httpClient = handler is null
            ? new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
            })
            : new HttpClient(handler, disposeHandler: true);
        httpClient.BaseAddress = options.Endpoint;
        httpClient.Timeout = TimeSpan.FromSeconds(5);
    }

    public async ValueTask RequestNodeStopAsync(
        BridgeCutoverHostIdentity expectedNode,
        CancellationToken cancellationToken)
    {
        await VerifyExpectedIdentityAsync(
            expectedNode,
            BridgeCutoverFailureReason.NodeIdentityMismatch,
            cancellationToken);
        await RequestStopAsync(expectedNode, cancellationToken);
    }

    public ValueTask VerifyNodeOfflineAsync(
        int expectedProcessId,
        CancellationToken cancellationToken) =>
        VerifyOfflineAsync(
            expectedProcessId,
            "node",
            BridgeCutoverFailureReason.NodeStillOnline,
            cancellationToken);

    public ValueTask<BridgeStoreHandoffEvidence> InspectStoreHandoffAsync(
        CancellationToken cancellationToken) =>
        options.StoreHandoffInspector.InspectAsync(cancellationToken);

    public ValueTask<int> StartDotNetActiveAsync(
        string instanceName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            Start(options.CreateDotNetStartInfo(instanceName)));
    }

    public ValueTask<int> StartDotNetActiveAuthorizedAsync(
        string instanceName,
        string operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOperationId(operationId);
        return ValueTask.FromResult(
            Start(CreateAuthorizedDotNetStartInfo(instanceName, operationId)));
    }

    public ValueTask<int> StartDotNetActiveAndBindAuthorizedAsync(
        string instanceName,
        string operationId,
        BridgeHostProcessStartedCallback processStarted,
        CancellationToken cancellationToken)
    {
        ValidateOperationId(operationId);
        return StartAndBindAsync(
            CreateAuthorizedDotNetStartInfo(instanceName, operationId),
            processStarted,
            cancellationToken);
    }

    public ValueTask<int> StartDotNetActiveAndBindAsync(
        string instanceName,
        BridgeHostProcessStartedCallback processStarted,
        CancellationToken cancellationToken) =>
        StartAndBindAsync(
            options.CreateDotNetStartInfo(instanceName),
            processStarted,
            cancellationToken);

    public ValueTask<BridgeCutoverHostIdentity> VerifyDotNetActiveAsync(
        int expectedProcessId,
        string expectedInstanceName,
        CancellationToken cancellationToken) =>
        VerifyActiveAsync(
            expectedProcessId,
            "dotnet",
            expectedInstanceName,
            BridgeCutoverFailureReason.DotNetIdentityMismatch,
            cancellationToken);

    public async ValueTask RequestDotNetStopAsync(
        int expectedProcessId,
        CancellationToken cancellationToken)
    {
        var expected = new BridgeCutoverHostIdentity(
            expectedProcessId,
            "dotnet",
            BridgeHostCutoverTransaction.CurrentManagementApiVersion,
            "active",
            ActiveOwner: true,
            InstanceName: "ignored");
        var actual = await ReadIdentityRequiredAsync(
            "dotnet",
            BridgeCutoverFailureReason.OwnershipUncertain,
            cancellationToken);
        if (actual.ProcessId != expected.ProcessId ||
            !actual.IsDotNetActive(
                BridgeHostCutoverTransaction.CurrentManagementApiVersion))
        {
            throw OperationFailure(
                BridgeCutoverFailureReason.OwnershipUncertain,
                "C# Active Host 身份已变化，拒绝停止未知进程。");
        }
        await RequestStopAsync(actual, cancellationToken);
    }

    public async ValueTask RequestExpectedDotNetStopAsync(
        BridgeCutoverHostIdentity expectedDotNet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedDotNet);
        if (!expectedDotNet.IsDotNetActive(
                BridgeHostCutoverTransaction.CurrentManagementApiVersion))
        {
            throw new ArgumentException(
                "恢复停止目标必须是完整的 .NET Active Owner 身份。",
                nameof(expectedDotNet));
        }

        await VerifyExpectedIdentityAsync(
            expectedDotNet,
            BridgeCutoverFailureReason.OwnershipUncertain,
            cancellationToken);
        await RequestStopAsync(expectedDotNet, cancellationToken);
    }

    public ValueTask VerifyDotNetOfflineAsync(
        int expectedProcessId,
        CancellationToken cancellationToken) =>
        VerifyOfflineAsync(
            expectedProcessId,
            "dotnet",
            BridgeCutoverFailureReason.DotNetStillOnline,
            cancellationToken);

    public ValueTask<int> StartNodeActiveAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Start(options.CreateNodeStartInfo()));
    }

    public ValueTask<int> StartNodeActiveAndBindAsync(
        BridgeHostProcessStartedCallback processStarted,
        CancellationToken cancellationToken) =>
        StartAndBindAsync(
            options.CreateNodeStartInfo(),
            processStarted,
            cancellationToken);

    public ValueTask<BridgeCutoverHostIdentity> VerifyNodeActiveAsync(
        int expectedProcessId,
        CancellationToken cancellationToken) =>
        VerifyActiveAsync(
            expectedProcessId,
            "node",
            expectedInstanceName: null,
            BridgeCutoverFailureReason.NodeRollbackIdentityMismatch,
            cancellationToken);

    public void Dispose() => httpClient.Dispose();

    private async ValueTask VerifyExpectedIdentityAsync(
        BridgeCutoverHostIdentity expected,
        BridgeCutoverFailureReason failureReason,
        CancellationToken cancellationToken)
    {
        var actual = await ReadIdentityRequiredAsync(
            expected.HostKind,
            failureReason,
            cancellationToken);
        if (!actual.Matches(expected))
        {
            throw OperationFailure(
                failureReason,
                "目标 Bridge Host 身份已变化，拒绝继续切换。");
        }
    }

    private async ValueTask RequestStopAsync(
        BridgeCutoverHostIdentity expected,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "control/shutdown")
            {
                Content = JsonContent.Create(new { }),
            };
            request.Headers.Add(ControlTokenHeader, options.ControlToken);
            request.Headers.Add(ExpectedHostKindHeader, expected.HostKind);
            request.Headers.Add(
                ManagementApiVersionHeader,
                expected.ManagementApiVersion.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add(
                ExpectedProcessIdHeader,
                expected.ProcessId.ToString(CultureInfo.InvariantCulture));
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw OperationFailure(
                    BridgeCutoverFailureReason.OwnershipUncertain,
                    $"Bridge Host 拒绝停止，HTTP {(int)response.StatusCode}。");
            }
        }
        catch (BridgeHostCutoverOperationException)
        {
            throw;
        }
        catch (Exception error) when (
            error is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new BridgeHostCutoverOperationException(
                BridgeCutoverFailureReason.OwnershipUncertain,
                "Bridge Host 停止请求的结果无法确认。",
                error);
        }
    }

    private async ValueTask VerifyOfflineAsync(
        int expectedProcessId,
        string hostKind,
        BridgeCutoverFailureReason stillOnlineReason,
        CancellationToken cancellationToken)
    {
        try
        {
            await BridgeHostExitWaiter.WaitAsync(
                expectedProcessId,
                token => ObserveExitAsync(expectedProcessId, hostKind, token),
                cancellationToken,
                options.MaxProbeAttempts,
                options.PollInterval);
        }
        catch (TimeoutException error)
        {
            throw new BridgeHostCutoverOperationException(
                stillOnlineReason,
                "目标 Bridge Host 未在截止时间前离线。",
                error);
        }
        catch (InvalidOperationException error)
        {
            throw new BridgeHostCutoverOperationException(
                BridgeCutoverFailureReason.OwnershipUncertain,
                "Bridge Host 离线确认期间发现身份替换或未知端点。",
                error);
        }
    }

    private async Task<BridgeHostExitObservation> ObserveExitAsync(
        int expectedProcessId,
        string hostKind,
        CancellationToken cancellationToken)
    {
        var identity = await TryReadIdentityAsync(hostKind, cancellationToken);
        if (identity is not null)
        {
            var expectedKindIsActive = string.Equals(hostKind, "node", StringComparison.Ordinal)
                ? identity.IsNodeActive(
                    BridgeHostCutoverTransaction.CurrentManagementApiVersion)
                : identity.IsDotNetActive(
                    BridgeHostCutoverTransaction.CurrentManagementApiVersion);
            if (!expectedKindIsActive)
            {
                return BridgeHostExitObservation.Unauthenticated;
            }
            return BridgeHostExitObservation.Authenticated(identity.ProcessId);
        }
        if (await PublicEndpointIsAliveAsync(cancellationToken))
        {
            return BridgeHostExitObservation.Unauthenticated;
        }
        return IsProcessAlive(expectedProcessId)
            ? BridgeHostExitObservation.ExpectedProcessAlive
            : BridgeHostExitObservation.Offline;
    }

    private async ValueTask<BridgeCutoverHostIdentity> VerifyActiveAsync(
        int expectedProcessId,
        string hostKind,
        string? expectedInstanceName,
        BridgeCutoverFailureReason failureReason,
        CancellationToken cancellationToken)
    {
        var interval = options.PollInterval ?? BridgeHostExitWaiter.DefaultPollInterval;
        for (var attempt = 1; attempt <= options.MaxProbeAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = await TryReadIdentityAsync(hostKind, cancellationToken);
            if (identity is not null)
            {
                var kindMatches = hostKind == "node"
                    ? identity.IsNodeActive(
                        BridgeHostCutoverTransaction.CurrentManagementApiVersion)
                    : identity.IsDotNetActive(
                        BridgeHostCutoverTransaction.CurrentManagementApiVersion);
                if (identity.ProcessId != expectedProcessId ||
                    !kindMatches ||
                    (expectedInstanceName is not null &&
                        !string.Equals(
                            identity.InstanceName,
                            expectedInstanceName,
                            StringComparison.Ordinal)))
                {
                    throw OperationFailure(
                        failureReason,
                        "新启动的 Bridge Host 身份与切换目标不匹配。");
                }
                return identity;
            }

            if (attempt < options.MaxProbeAttempts && interval > TimeSpan.Zero)
            {
                await Task.Delay(interval, cancellationToken);
            }
        }

        throw OperationFailure(
            failureReason,
            "新启动的 Bridge Host 未在截止时间前提供认证身份。");
    }

    private async ValueTask<BridgeCutoverHostIdentity> ReadIdentityRequiredAsync(
        string hostKind,
        BridgeCutoverFailureReason failureReason,
        CancellationToken cancellationToken) =>
        await TryReadIdentityAsync(hostKind, cancellationToken) ??
        throw OperationFailure(
            failureReason,
            "无法读取目标 Bridge Host 的认证身份。");

    private async ValueTask<BridgeCutoverHostIdentity?> TryReadIdentityAsync(
        string hostKind,
        CancellationToken cancellationToken)
    {
        var path = string.Equals(hostKind, "node", StringComparison.Ordinal)
            ? "health"
            : "control/status";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add(ControlTokenHeader, options.ControlToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var status = await JsonSerializer.DeserializeAsync<CutoverStatus>(
                stream,
                JsonOptions,
                cancellationToken);
            if (status?.Ok != true || status.ProcessId <= 0)
            {
                return null;
            }
            return new(
                status.ProcessId,
                status.HostKind,
                status.ManagementApiVersion,
                status.OwnershipMode,
                status.ActiveOwner,
                status.InstanceName);
        }
        catch (Exception error) when (
            error is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async ValueTask<bool> PublicEndpointIsAliveAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync("health", cancellationToken);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return true;
        }
    }

    private int Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        try
        {
            using var process = startProcess(startInfo) ??
                throw new InvalidOperationException("Bridge Host 进程未能启动。");
            return process.Id;
        }
        catch (Exception error) when (
            error is Win32Exception or InvalidOperationException)
        {
            throw new BridgeHostCutoverOperationException(
                BridgeCutoverFailureReason.OwnershipUncertain,
                "Bridge Host 启动调用的结果无法确认。",
                error);
        }
    }

    private async ValueTask<int> StartAndBindAsync(
        ProcessStartInfo startInfo,
        BridgeHostProcessStartedCallback processStarted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processStarted);
        cancellationToken.ThrowIfCancellationRequested();
        var processId = Start(startInfo);
        await processStarted(processId, cancellationToken);
        return processId;
    }

    private ProcessStartInfo CreateAuthorizedDotNetStartInfo(
        string instanceName,
        string operationId)
    {
        var startInfo = options.CreateDotNetStartInfo(instanceName);
        startInfo.ArgumentList.Add("--cutover-operation");
        startInfo.ArgumentList.Add(operationId);
        return startInfo;
    }

    private static void ValidateOperationId(string operationId)
    {
        if (!BridgeHostCutoverCheckpointValidator.IsValidOperationId(operationId))
        {
            throw new ArgumentException(
                "Active 启动 operationId 无效。",
                nameof(operationId));
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }

    private static BridgeHostCutoverOperationException OperationFailure(
        BridgeCutoverFailureReason reason,
        string message) => new(reason, message);

    private sealed class CutoverStatus
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("hostKind")]
        public string HostKind { get; set; } = "";

        [JsonPropertyName("managementApiVersion")]
        public int ManagementApiVersion { get; set; }

        [JsonPropertyName("instanceName")]
        public string InstanceName { get; set; } = "";

        [JsonPropertyName("ownershipMode")]
        public string OwnershipMode { get; set; } = "";

        [JsonPropertyName("activeOwner")]
        public bool ActiveOwner { get; set; }

        [JsonPropertyName("processId")]
        public int ProcessId { get; set; }
    }
}
