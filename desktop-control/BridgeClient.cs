using System.Diagnostics;
using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiCliFeishuControl;

internal sealed partial class BridgeClient : IDisposable
{
    private const string ControlTokenHeader = "X-AI-CLI-Feishu-Control-Token";
    private const string ExpectedHostKindHeader = "X-AI-CLI-Feishu-Expected-Host-Kind";
    private const string ManagementApiVersionHeader = "X-AI-CLI-Feishu-Management-Api-Version";
    private const string ExpectedProcessIdHeader = "X-AI-CLI-Feishu-Expected-Process-Id";
    private readonly HttpClient httpClient;
    private readonly BridgeHostTarget target;
    private readonly ProductionBridgeStatusProjector productionStatusProjector;
    private readonly SemaphoreSlim hookInstallationGate = new(1, 1);

    public BridgeClient()
    {
        BridgeRoot = FindBridgeRoot();
        target = SelectHostTarget(BridgeRoot);
        productionStatusProjector = new ProductionBridgeStatusProjector(
            Path.Combine(BridgeRoot, "data"),
            Directory.GetParent(BridgeRoot)?.FullName ?? BridgeRoot,
            ReadBindCommand(BridgeRoot));
        httpClient = new HttpClient
        {
            BaseAddress = new Uri(
                $"http://127.0.0.1:{target.Port}/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    public string BridgeRoot { get; }

    public int Port => target.Port;

    public bool IsProductionTarget => target.IsProduction;

    public bool IsDotNetProductionTarget => true;

    public string HostDisplayName => target.DisplayName;

    internal BridgeHostTarget CurrentTarget => target;

    public async ValueTask RefreshTargetAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ValueTask.CompletedTask;
    }

    public async Task<BridgeStatus?> GetStatusAsync(
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        var target = CurrentTarget;
        return await GetStatusAsync(target, cancellationToken, forceRefresh);
    }

    private async Task<BridgeStatus?> GetStatusAsync(
        BridgeHostTarget target,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        if (!TryReadControlToken(BridgeRoot, out var controlToken))
        {
            return null;
        }

        try
        {
            return await GetDotNetProductionStatusAsync(
                target,
                controlToken,
                cancellationToken,
                forceRefresh);
        }
        catch (Exception error) when (
            error is HttpRequestException or TaskCanceledException or JsonException)
        {
            AppLog.WarnThrottled(
                $"Bridge 状态请求失败 {error.GetType().Name}: {error.Message}",
                TimeSpan.FromSeconds(10));
            return null;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var target = CurrentTarget;
        AppLog.Info(
            $"启动桥接（host={target.HostKind}，ownership={target.OwnershipMode}，" +
            $"root={BridgeRoot}，port={target.Port}）...");
        var running = await GetStatusAsync(target, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (running is not null)
        {
            if (running.Ok)
            {
                await EnsureHooksInstalledAsync(cancellationToken: cancellationToken);
                AppLog.Info($"桥接已经在线（pid={running.ProcessId}，version={running.Version}）。");
                return;
            }
            throw new InvalidOperationException(
                $"{target.HostKind} Bridge Host 已在端口 {target.Port} 运行，" +
                $"但状态为 {running.Status}，已拒绝重复启动。");
        }

        var publicProbe = await ProbeBridgeAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (publicProbe?.Ok == true)
        {
            throw new InvalidOperationException(
                $"端口 {target.Port} 上已有桥接进程，但本机控制令牌不匹配；为避免启动重复进程，已拒绝继续。");
        }

        await EnsureHooksInstalledAsync(cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        StartBridgeProcess(target);
        AppLog.Info($"{target.HostKind} 桥接进程已直接启动。");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var target = CurrentTarget;
        AppLog.Info("停止桥接...");
        var status = await GetStatusAsync(target, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (status is null)
        {
            var publicProbe = await ProbeBridgeAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (publicProbe?.Ok == true)
            {
                throw new InvalidOperationException(
                    $"端口 {target.Port} 上的桥接进程未通过本机控制令牌验证，已拒绝停止。");
            }
            AppLog.Info("桥接已经停止。");
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "control/shutdown")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add(ControlTokenHeader, ReadControlToken(BridgeRoot));
        request.Headers.Add(ExpectedHostKindHeader, target.HostKind);
        request.Headers.Add(
            ManagementApiVersionHeader,
            target.ManagementApiVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(
            ExpectedProcessIdHeader,
            status.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"桥接拒绝停止：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        AppLog.Info($"桥接已接受平滑停止请求（pid={status.ProcessId}）。");
        await BridgeHostExitWaiter.WaitAsync(
            status.ProcessId,
            cancellationToken => ObserveBridgeExitAsync(
                target,
                status.ProcessId,
                cancellationToken),
            cancellationToken);
        AppLog.Info($"桥接已完成平滑停止（pid={status.ProcessId}）。");
    }

    public int RunBridgeService()
    {
        var target = CurrentTarget;
        var running = GetStatusAsync(target, default).GetAwaiter().GetResult();
        if (running is not null)
        {
            if (running.Ok)
            {
                AppLog.Info($"桥接已经在线（pid={running.ProcessId}），后台宿主无需重复启动。");
                return 0;
            }
            throw new InvalidOperationException(
                $"{target.HostKind} Bridge Host 已在端口 {target.Port} 运行，" +
                $"但状态为 {running.Status}，后台宿主不会重复启动。");
        }
        var publicProbe = ProbeBridgeAsync().GetAwaiter().GetResult();
        if (publicProbe?.Ok == true)
        {
            throw new InvalidOperationException(
                $"端口 {target.Port} 上已有桥接进程，但本机控制令牌不匹配。");
        }

        using var process = StartBridgeProcessCore(target);
        AppLog.Info($"后台宿主正在监控桥接 pid={process.Id}。");
        process.WaitForExit();
        AppLog.Info($"桥接 pid={process.Id} 已退出，代码 {process.ExitCode}。");
        return process.ExitCode;
    }

    public void OpenBridgeFolder()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { BridgeRoot },
            UseShellExecute = true,
        });
    }

    private static string FindBridgeRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 10; depth++, current = current.Parent)
        {
            if (IsBridgeRoot(current.FullName))
            {
                return current.FullName;
            }
        }

        var configuredPath = Environment.GetEnvironmentVariable(
            "AI_CLI_FEISHU_BRIDGE_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredPath) &&
            Directory.Exists(configuredPath) &&
            IsBridgeRoot(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }
        throw new DirectoryNotFoundException("找不到 AI CLI 飞书助手目录。");
    }

    private static bool IsBridgeRoot(string path) =>
        Directory.Exists(Path.Combine(path, "scripts")) &&
        (File.Exists(Path.Combine(path, "AiCliFeishuBridgeHost.exe")) ||
         File.Exists(Path.Combine(
             path,
             "bridge-dotnet",
             "src",
             "AiCliFeishu.Bridge.Host",
             "AiCliFeishu.Bridge.Host.csproj")));

    private static int ReadBridgePort(string bridgeRoot)
    {
        try
        {
            var envPath = Path.Combine(bridgeRoot, ".env");
            foreach (var line in File.ReadLines(envPath))
            {
                if (!line.TrimStart().StartsWith("BRIDGE_HTTP_PORT=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var value = line[(line.IndexOf('=') + 1)..].Trim();
                if (int.TryParse(value, out var port) && port is > 0 and <= 65535)
                {
                    return port;
                }
            }
        }
        catch
        {
            // Fall back to the bridge default without exposing .env contents.
        }
        return 8765;
    }

    private static string ReadBindCommand(string bridgeRoot)
    {
        try
        {
            var envPath = Path.Combine(bridgeRoot, ".env");
            foreach (var line in File.ReadLines(envPath))
            {
                if (!line.TrimStart().StartsWith(
                        "FEISHU_BIND_COMMAND=",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var value = line[(line.IndexOf('=') + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        catch
        {
            // Keep the product default when .env is unavailable.
        }
        return "绑定";
    }

    private static BridgeHostTarget SelectHostTarget(string bridgeRoot)
        => BridgeHostTarget.FromConfiguration(
            Environment.GetEnvironmentVariable("AI_CLI_FEISHU_BRIDGE_HOST"),
            ReadBridgePort(bridgeRoot));

    private static bool TryReadControlToken(string bridgeRoot, out string token)
        => BridgeControlTokenReader.TryRead(bridgeRoot, out token);

    private static string ReadControlToken(string bridgeRoot)
        => BridgeControlTokenReader.Read(bridgeRoot);

    public void Dispose()
    {
        httpClient.Dispose();
        hookInstallationGate.Dispose();
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed record ManagedTerminalLaunchReceipt(
        string TerminalId,
        Process Launcher,
        bool AllowsCleanEarlyExit);

    private sealed class BridgeProbe
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; } = "";

        [JsonPropertyName("processId")]
        public int ProcessId { get; set; }
    }

}
