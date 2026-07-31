using System.Diagnostics;
using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexFeishuControl;

internal sealed class BridgeClient : IDisposable
{
    private readonly HttpClient httpClient;

    public BridgeClient()
    {
        BridgeRoot = FindBridgeRoot();
        Port = ReadBridgePort(BridgeRoot);
        httpClient = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{Port}/"),
            Timeout = TimeSpan.FromSeconds(2),
        };
    }

    public string BridgeRoot { get; }

    public int Port { get; }

    public async Task<BridgeStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("health", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<BridgeStatus>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (Exception error) when (
            error is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public async Task StartAsync()
    {
        await RunPowerShellScriptAsync("install-hooks.ps1", TimeSpan.FromSeconds(10));
        await RunPowerShellScriptAsync("start-bridge.ps1", TimeSpan.FromSeconds(10));
    }

    public Task StopAsync() => RunPowerShellScriptAsync("stop-bridge.ps1", TimeSpan.FromSeconds(10));

    public async Task SetSessionAliasAsync(
        string sessionId,
        string? alias,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "sessions/alias",
            new { sessionId, alias },
            cancellationToken);
        AliasUpdateResult? result = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            result = await JsonSerializer.DeserializeAsync<AliasUpdateResult>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (JsonException)
        {
        }

        if (!response.IsSuccessStatusCode || result?.Ok != true)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result?.Error) ? "设置会话别名失败。" : result.Error);
        }
    }

    public async Task<ApprovalResolveResult> ResolveApprovalAsync(
        string requestId,
        string resolution,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId) ||
            resolution is not ("allow" or "deny"))
        {
            throw new InvalidOperationException("审批请求参数不正确。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "approvals/resolve")
        {
            Content = JsonContent.Create(new { requestId, resolution }),
        };
        request.Headers.Add(
            "X-Codex-Feishu-Control-Token",
            ReadControlToken(BridgeRoot));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        ApprovalResolveResult? result = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            result = await JsonSerializer.DeserializeAsync<ApprovalResolveResult>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (JsonException)
        {
        }

        if (!response.IsSuccessStatusCode || result?.Ok != true)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result?.Error) ? "处理本机审批失败。" : result.Error);
        }
        return result;
    }

    public async Task<BridgeSettings> UpdateSettingsAsync(
        BridgeSettings settings,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "settings")
        {
            Content = JsonContent.Create(new
            {
                notifyActivity = settings.NotifyActivity,
                autoRetryErrors = settings.AutoRetryErrors,
                autoApprove = settings.AutoApprove,
            }),
        };
        request.Headers.Add(
            "X-Codex-Feishu-Control-Token",
            ReadControlToken(BridgeRoot));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        SettingsUpdateResult? result = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            result = await JsonSerializer.DeserializeAsync<SettingsUpdateResult>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (JsonException)
        {
        }
        if (!response.IsSuccessStatusCode || result?.Ok != true)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result?.Error) ? "保存设置失败。" : result.Error);
        }
        return result.Settings;
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

    public string StartManagedTerminal(string cwd, bool elevated, string? codexArguments = null)
    {
        var fullPath = Path.GetFullPath(cwd);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("选择的项目目录不存在。");
        }

        var controlExecutable = Application.ExecutablePath;
        if (!File.Exists(controlExecutable))
        {
            throw new FileNotFoundException("找不到 Codex 飞书助手程序。", controlExecutable);
        }
        var terminalHost = FindTerminalHost(controlExecutable);
        if (!File.Exists(terminalHost))
        {
            throw new FileNotFoundException(
                "找不到 Windows Terminal 同步宿主，请重新安装或更新 Codex 飞书助手。",
                terminalHost);
        }

        var terminalId = Guid.NewGuid().ToString("N");
        var normalizedArguments = codexArguments?.Trim() ?? "";
        if (normalizedArguments.Length > 4_000 ||
            normalizedArguments.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new InvalidOperationException("Codex 启动参数无效或过长。");
        }
        var windowsTerminal = FindWindowsTerminal();
        var startInfo = windowsTerminal is not null
            ? BuildWindowsTerminalStartInfo(
                windowsTerminal,
                terminalHost,
                terminalId,
                fullPath,
                elevated,
                normalizedArguments)
            : BuildClassicTerminalStartInfo(
                terminalHost,
                terminalId,
                fullPath,
                elevated,
                normalizedArguments);

        try
        {
            Process.Start(startInfo);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("已取消管理员权限确认。", error);
        }
        return terminalId;
    }

    private ProcessStartInfo BuildWindowsTerminalStartInfo(
        string windowsTerminal,
        string terminalHost,
        string terminalId,
        string cwd,
        bool elevated,
        string codexArguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = windowsTerminal,
            WorkingDirectory = cwd,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        };
        if (elevated)
        {
            startInfo.Verb = "runas";
        }
        startInfo.ArgumentList.Add("--window");
        startInfo.ArgumentList.Add("new");
        startInfo.ArgumentList.Add("new-tab");
        startInfo.ArgumentList.Add("--title");
        startInfo.ArgumentList.Add($"Codex · {new DirectoryInfo(cwd).Name}{(elevated ? " · 管理员" : "")}");
        startInfo.ArgumentList.Add("--startingDirectory");
        startInfo.ArgumentList.Add(cwd);
        AddManagedTerminalArguments(startInfo, terminalHost, terminalId, cwd, codexArguments);
        return startInfo;
    }

    private ProcessStartInfo BuildClassicTerminalStartInfo(
        string terminalHost,
        string terminalId,
        string cwd,
        bool elevated,
        string codexArguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = terminalHost,
            WorkingDirectory = cwd,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        };
        if (elevated)
        {
            startInfo.Verb = "runas";
        }
        AddManagedTerminalArguments(startInfo, null, terminalId, cwd, codexArguments);
        return startInfo;
    }

    private void AddManagedTerminalArguments(
        ProcessStartInfo startInfo,
        string? executable,
        string terminalId,
        string cwd,
        string codexArguments)
    {
        if (executable is not null)
        {
            startInfo.ArgumentList.Add(executable);
        }
        startInfo.ArgumentList.Add("--managed-terminal");
        startInfo.ArgumentList.Add("--id");
        startInfo.ArgumentList.Add(terminalId);
        startInfo.ArgumentList.Add("--cwd");
        startInfo.ArgumentList.Add(cwd);
        startInfo.ArgumentList.Add("--bridge-url");
        startInfo.ArgumentList.Add($"http://127.0.0.1:{Port}");
        if (!string.IsNullOrWhiteSpace(codexArguments))
        {
            startInfo.ArgumentList.Add("--codex-args");
            startInfo.ArgumentList.Add(codexArguments);
        }
    }

    private static string? FindWindowsTerminal()
    {
        var alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "wt.exe");
        return File.Exists(alias) ? alias : null;
    }

    private static string FindTerminalHost(string controlExecutable)
    {
        var directory = Path.GetDirectoryName(controlExecutable) ?? AppContext.BaseDirectory;
        var version = typeof(BridgeClient).Assembly.GetName().Version;
        if (version is not null)
        {
            var versionedPath = Path.Combine(
                directory,
                $"CodexFeishuTerminalHost-{version.Major}.{version.Minor}.{version.Build}.exe");
            if (File.Exists(versionedPath))
            {
                return versionedPath;
            }
        }
        return Path.Combine(directory, "CodexFeishuTerminalHost.exe");
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
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail) ? "操作失败。" : detail.Trim());
        }
        return result;
    }

    private static string FindBridgeRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 10; depth++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "scripts", "start-bridge.ps1")) &&
                File.Exists(Path.Combine(current.FullName, "dist", "index.js")))
            {
                return current.FullName;
            }
        }

        var configuredPath = Environment.GetEnvironmentVariable("CODEX_FEISHU_BRIDGE_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredPath) &&
            Directory.Exists(configuredPath) &&
            File.Exists(Path.Combine(configuredPath, "scripts", "start-bridge.ps1")) &&
            File.Exists(Path.Combine(configuredPath, "dist", "index.js")))
        {
            return Path.GetFullPath(configuredPath);
        }
        throw new DirectoryNotFoundException("找不到 Codex 飞书桥接器目录。");
    }

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

    private static string ReadControlToken(string bridgeRoot)
    {
        var tokenPath = Path.Combine(bridgeRoot, "data", "control-token.json");
        try
        {
            var file = JsonSerializer.Deserialize<ControlTokenFile>(File.ReadAllText(tokenPath));
            if (!string.IsNullOrWhiteSpace(file?.Token) && file.Token.Length == 64)
            {
                return file.Token;
            }
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        throw new InvalidOperationException(
            "找不到本机审批控制令牌。请先点击“连接”，再重新打开 Codex 飞书助手。");
    }

    public void Dispose() => httpClient.Dispose();

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed class ControlTokenFile
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = "";
    }
}
