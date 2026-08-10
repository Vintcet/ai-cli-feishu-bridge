using System.Diagnostics;
using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiCliFeishuControl;

internal sealed partial class BridgeClient
{
    private async Task<BridgeProbe?> ProbeBridgeAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("health", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<BridgeProbe>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (Exception error) when (
            error is HttpRequestException or
            TaskCanceledException or
            JsonException or
            IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
            return null;
        }
    }

    private async Task<BridgeStatus?> GetDotNetProductionStatusAsync(
        BridgeHostTarget target,
        string controlToken,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        using var healthRequest = new HttpRequestMessage(HttpMethod.Get, "health");
        using var controlRequest = new HttpRequestMessage(
            HttpMethod.Get,
            forceRefresh ? "control/status?refresh=1" : "control/status");
        using var presenceRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "control/desktop-presence");
        healthRequest.Headers.Add(ControlTokenHeader, controlToken);
        controlRequest.Headers.Add(ControlTokenHeader, controlToken);
        presenceRequest.Headers.Add(ControlTokenHeader, controlToken);

        var healthTask = httpClient.SendAsync(healthRequest, cancellationToken);
        var controlTask = httpClient.SendAsync(controlRequest, cancellationToken);
        var presenceTask = httpClient.SendAsync(presenceRequest, cancellationToken);
        await Task.WhenAll(healthTask, controlTask, presenceTask);
        using var healthResponse = await healthTask;
        using var controlResponse = await controlTask;
        using var presenceResponse = await presenceTask;
        if (!healthResponse.IsSuccessStatusCode)
        {
            AppLog.WarnThrottled(
                $"/health 返回 HTTP {(int)healthResponse.StatusCode} " +
                healthResponse.ReasonPhrase,
                TimeSpan.FromSeconds(10));
            return null;
        }
        if (!controlResponse.IsSuccessStatusCode)
        {
            AppLog.WarnThrottled(
                $"/control/status 返回 HTTP {(int)controlResponse.StatusCode} " +
                controlResponse.ReasonPhrase,
                TimeSpan.FromSeconds(10));
            return null;
        }
        if (!presenceResponse.IsSuccessStatusCode)
        {
            AppLog.WarnThrottled(
                $"/control/desktop-presence 返回 HTTP " +
                $"{(int)presenceResponse.StatusCode} {presenceResponse.ReasonPhrase}，" +
                "改用本机进程判活。",
                TimeSpan.FromSeconds(10));
        }

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };
        await using var healthStream = await healthResponse.Content
            .ReadAsStreamAsync(cancellationToken);
        await using var controlStream = await controlResponse.Content
            .ReadAsStreamAsync(cancellationToken);
        var health = await JsonSerializer.DeserializeAsync<BridgeProductionHealthStatus>(
            healthStream,
            jsonOptions,
            cancellationToken) ?? throw new JsonException("生产健康状态为空。");
        var control = await JsonSerializer.DeserializeAsync<BridgeProductionControlStatus>(
            controlStream,
            jsonOptions,
            cancellationToken) ?? throw new JsonException("生产控制状态为空。");
        var presence = await ReadProductionPresenceAsync(
            presenceResponse,
            jsonOptions,
            cancellationToken);
        var status = await productionStatusProjector.ProjectAsync(
            health,
            control,
            presence,
            cancellationToken);
        if (!target.Matches(status))
        {
            throw new InvalidOperationException(
                $"端口 {target.Port} 返回了身份不匹配的 Bridge Host：" +
                $"expected={target.HostKind}/{target.OwnershipMode}，" +
                $"actual={status.HostKind}/{status.OwnershipMode}。");
        }
        return status;
    }

    private static async Task<BridgeProductionPresenceStatus> ReadProductionPresenceAsync(
        HttpResponseMessage response,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return new BridgeProductionPresenceStatus();
        }
        try
        {
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<BridgeProductionPresenceStatus>(
                stream,
                jsonOptions,
                cancellationToken) ?? new BridgeProductionPresenceStatus();
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            AppLog.WarnThrottled(
                "生产桌面在线会话状态无效，改用本机进程判活。",
                TimeSpan.FromSeconds(10));
            return new BridgeProductionPresenceStatus();
        }
    }

    private async Task<BridgeHostExitObservation> ObserveBridgeExitAsync(
        BridgeHostTarget target,
        int expectedProcessId,
        CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(target, cancellationToken);
        if (status is not null)
        {
            return BridgeHostExitObservation.Authenticated(status.ProcessId);
        }
        var publicProbe = await ProbeBridgeAsync(cancellationToken);
        if (publicProbe?.Ok == true)
        {
            return BridgeHostExitObservation.Unauthenticated;
        }
        return IsProcessAlive(expectedProcessId)
            ? BridgeHostExitObservation.ExpectedProcessAlive
            : BridgeHostExitObservation.Offline;
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
    }

    private void StartBridgeProcess(BridgeHostTarget target)
    {
        using var process = StartBridgeProcessCore(target);
        AppLog.Info($"已启动 {target.HostKind} 桥接进程 pid={process.Id}。");
    }

    private Process StartBridgeProcessCore(BridgeHostTarget target)
    {
        var startInfo = target.CreateStartInfo(BridgeRoot, AppContext.BaseDirectory);
        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException($"{target.HostKind} 桥接进程未能启动。");
        }
        catch (Win32Exception error)
        {
            throw new InvalidOperationException(
                $"无法启动 {target.HostKind} 桥接。请确认 C# Bridge Host 已部署，并安装对应的 .NET Runtime。",
                error);
        }
    }

    private async Task RunPowerShellScriptAsync(string scriptName, TimeSpan timeout)
    {
        var scriptPath = Path.Combine(BridgeRoot, "scripts", scriptName);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("找不到桥接控制脚本。", scriptPath);
        }

        var executable = File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe")
            ? @"C:\Program Files\PowerShell\7\pwsh.exe"
            : "powershell.exe";
        var sourceHookHost = Path.Combine(
            AppContext.BaseDirectory,
            "AiCliFeishuTerminalHost.exe");
        if (!File.Exists(sourceHookHost))
        {
            sourceHookHost = Path.Combine(
                BridgeRoot,
                "AiCliFeishuTerminalHost.exe");
        }
        await RunProcessAsync(
            executable,
            [
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-WindowStyle",
                "Hidden",
                "-File",
                scriptPath,
                "-Port",
                Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-SourceHookHost",
                sourceHookHost,
            ],
            timeout,
            throwOnFailure: true);
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        bool throwOnFailure)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("操作超时，请稍后重试。");
        }

        var result = new ProcessResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
        if (throwOnFailure && result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            detail = Regex.Replace(
                detail,
                "\u001B\\[[0-?]*[ -/]*[@-~]",
                string.Empty,
                RegexOptions.CultureInvariant);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail) ? "操作失败。" : detail.Trim());
        }
        return result;
    }

}
