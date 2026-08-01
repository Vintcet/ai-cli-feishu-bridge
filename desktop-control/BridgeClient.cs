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
                AppLog.WarnThrottled(
                    $"/health 返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                    TimeSpan.FromSeconds(10));
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
            AppLog.WarnThrottled(
                $"/health 请求失败 {error.GetType().Name}: {error.Message}",
                TimeSpan.FromSeconds(10));
            return null;
        }
    }

    public async Task StartAsync()
    {
        AppLog.Info($"启动桥接（root={BridgeRoot}，port={Port}）...");
        await RunPowerShellScriptAsync("install-hooks.ps1", TimeSpan.FromSeconds(10));
        await RunPowerShellScriptAsync("install-claude-code-hooks.ps1", TimeSpan.FromSeconds(10));
        await RunPowerShellScriptAsync("start-bridge.ps1", TimeSpan.FromSeconds(10));
        AppLog.Info("桥接启动命令已执行。");
    }

    public Task StopAsync()
    {
        AppLog.Info("停止桥接...");
        return RunPowerShellScriptAsync("stop-bridge.ps1", TimeSpan.FromSeconds(10));
    }

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

    public async Task RetrySessionGroupAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "sessions/feishu-group/retry")
        {
            Content = JsonContent.Create(new { sessionId }),
        };
        request.Headers.Add(
            "X-Codex-Feishu-Control-Token",
            ReadControlToken(BridgeRoot));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        SessionGroupRetryResult? result = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            result = await JsonSerializer.DeserializeAsync<SessionGroupRetryResult>(
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
                string.IsNullOrWhiteSpace(result?.Error) ? "创建飞书会话群失败。" : result.Error);
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

    public async Task HideSessionFromHistoryAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException("会话 ID 参数不正确。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "sessions/history/hide")
        {
            Content = JsonContent.Create(new { sessionId }),
        };
        request.Headers.Add(
            "X-Codex-Feishu-Control-Token",
            ReadControlToken(BridgeRoot));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        HistoryHideResult? result = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            result = await JsonSerializer.DeserializeAsync<HistoryHideResult>(
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
                string.IsNullOrWhiteSpace(result?.Error) ? "删除历史记录失败。" : result.Error);
        }
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
                notifyUserPrompts = settings.NotifyUserPrompts,
                autoRetryErrors = settings.AutoRetryErrors,
                retryMaxAttempts = settings.RetryMaxAttempts,
                retryIntervalSeconds = settings.RetryIntervalSeconds,
                retryJitterSeconds = settings.RetryJitterSeconds,
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

    public string StartManagedTerminal(string cwd, bool elevated, string? codexArguments = null) =>
        StartManagedTerminalCore(cwd, elevated, "codex", codexArguments, null);

    public string StartManagedClaudeCodeTerminal(
        string cwd,
        bool elevated,
        string? claudeCodeArguments = null)
    {
        var claudeCodeCommand = FindClaudeCodeCommand();
        if (claudeCodeCommand is null)
        {
            throw new InvalidOperationException(
                "找不到 Claude Code CLI。请先安装 claude，并确保其在 PATH 或常见用户安装目录中。");
        }
        return StartManagedTerminalCore(
            cwd,
            elevated,
            "claudecode",
            claudeCodeArguments,
            claudeCodeCommand);
    }

    private string StartManagedTerminalCore(
        string cwd,
        bool elevated,
        string runtime,
        string? toolArguments,
        string? toolCommand)
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
        var displayName = runtime == "claudecode" ? "Claude Code" : "Codex";
        var normalizedArguments = toolArguments?.Trim() ?? "";
        if (normalizedArguments.Length > 4_000 ||
            normalizedArguments.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new InvalidOperationException($"{displayName} 启动参数无效或过长。");
        }
        var windowsTerminal = FindWindowsTerminal();
        var startInfo = windowsTerminal is not null
            ? BuildWindowsTerminalStartInfo(
                windowsTerminal,
                terminalHost,
                terminalId,
                fullPath,
                elevated,
                runtime,
                normalizedArguments,
                toolCommand)
            : BuildClassicTerminalStartInfo(
                terminalHost,
                terminalId,
                fullPath,
                elevated,
                runtime,
                normalizedArguments,
                toolCommand);

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

    public async Task<int> LaunchOpenCodeAsync(
        string cwd,
        bool elevated,
        string? openCodeArguments = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(cwd);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("选择的项目目录不存在。");
        }
        var parsedArguments = CodexArgumentParser.ParseOpenCode(openCodeArguments);
        var resumeSessionId = ExtractResumeSessionId(parsedArguments);

        var port = await ReserveOpenCodePortAsync(
            fullPath,
            resumeSessionId,
            cancellationToken);
        var openCodeCommand = FindOpenCodeCommand();
        if (openCodeCommand is null)
        {
            throw new InvalidOperationException(
                "找不到 opencode 命令。请先安装 opencode，并确保其在 PATH 中。");
        }

        var windowsTerminal = FindWindowsTerminal();
        var startInfo = BuildOpenCodeTerminalStartInfo(
            windowsTerminal,
            openCodeCommand,
            port,
            fullPath,
            elevated,
            parsedArguments);
        try
        {
            Process.Start(startInfo);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("已取消管理员权限确认。", error);
        }
        return port;
    }

    private static string? ExtractResumeSessionId(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i] is "-s" or "--session"
                && !string.IsNullOrWhiteSpace(arguments[i + 1]))
            {
                return arguments[i + 1];
            }
        }
        return null;
    }

    private async Task<int> ReserveOpenCodePortAsync(
        string cwd,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object> { ["cwd"] = cwd };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            payload["sessionId"] = sessionId;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "opencode/launch")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add(
            "X-Codex-Feishu-Control-Token",
            ReadControlToken(BridgeRoot));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        OpenCodeLaunchResult? result = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            result = await JsonSerializer.DeserializeAsync<OpenCodeLaunchResult>(
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
                string.IsNullOrWhiteSpace(result?.Error) ? "申请 opencode 端口失败。" : result.Error);
        }
        return result.Port;
    }

    private ProcessStartInfo BuildOpenCodeTerminalStartInfo(
        string? windowsTerminal,
        string openCodeCommand,
        int port,
        string cwd,
        bool elevated,
        IReadOnlyList<string> openCodeArguments)
    {
        var isScript = openCodeCommand.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            openCodeCommand.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = windowsTerminal ?? openCodeCommand,
            WorkingDirectory = cwd,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        };
        if (elevated)
        {
            startInfo.Verb = "runas";
        }
        if (windowsTerminal is not null)
        {
            startInfo.ArgumentList.Add("--window");
            startInfo.ArgumentList.Add("new");
            startInfo.ArgumentList.Add("new-tab");
            startInfo.ArgumentList.Add("--title");
            startInfo.ArgumentList.Add($"opencode · {new DirectoryInfo(cwd).Name}{(elevated ? " · 管理员" : "")}");
            startInfo.ArgumentList.Add("--startingDirectory");
            startInfo.ArgumentList.Add(cwd);
            if (isScript)
            {
                startInfo.ArgumentList.Add("cmd.exe");
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add("/c");
            }
        }
        startInfo.ArgumentList.Add(openCodeCommand);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var argument in openCodeArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static string? FindOpenCodeCommand()
    {
        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(entry.Trim('"'), "opencode.exe"));
            candidates.Add(Path.Combine(entry.Trim('"'), "opencode.cmd"));
        }
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(userProfile, ".local", "bin", "opencode.exe"));
        candidates.Add(Path.Combine(userProfile, ".local", "bin", "opencode.cmd"));
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        candidates.Add(Path.Combine(appData, "npm", "opencode.cmd"));
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(localAppData, "Programs", "opencode", "opencode.exe"));
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string? FindClaudeCodeCommand()
    {
        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(entry.Trim('"'), "claude.exe"));
            candidates.Add(Path.Combine(entry.Trim('"'), "claude.cmd"));
        }
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(userProfile, ".local", "bin", "claude.exe"));
        candidates.Add(Path.Combine(userProfile, ".local", "bin", "claude.cmd"));
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        candidates.Add(Path.Combine(appData, "npm", "claude.cmd"));
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(localAppData, "Programs", "Claude", "claude.exe"));
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private ProcessStartInfo BuildWindowsTerminalStartInfo(
        string windowsTerminal,
        string terminalHost,
        string terminalId,
        string cwd,
        bool elevated,
        string runtime,
        string toolArguments,
        string? toolCommand)
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
        var displayName = runtime == "claudecode" ? "Claude Code" : "Codex";
        startInfo.ArgumentList.Add($"{displayName} · {new DirectoryInfo(cwd).Name}{(elevated ? " · 管理员" : "")}");
        startInfo.ArgumentList.Add("--startingDirectory");
        startInfo.ArgumentList.Add(cwd);
        AddManagedTerminalArguments(
            startInfo,
            terminalHost,
            terminalId,
            cwd,
            runtime,
            toolArguments,
            toolCommand);
        return startInfo;
    }

    private ProcessStartInfo BuildClassicTerminalStartInfo(
        string terminalHost,
        string terminalId,
        string cwd,
        bool elevated,
        string runtime,
        string toolArguments,
        string? toolCommand)
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
        AddManagedTerminalArguments(
            startInfo,
            null,
            terminalId,
            cwd,
            runtime,
            toolArguments,
            toolCommand);
        return startInfo;
    }

    private void AddManagedTerminalArguments(
        ProcessStartInfo startInfo,
        string? executable,
        string terminalId,
        string cwd,
        string runtime,
        string toolArguments,
        string? toolCommand)
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
        startInfo.ArgumentList.Add("--runtime");
        startInfo.ArgumentList.Add(runtime);
        if (!string.IsNullOrWhiteSpace(toolCommand))
        {
            startInfo.ArgumentList.Add("--tool-command");
            startInfo.ArgumentList.Add(toolCommand);
        }
        if (!string.IsNullOrWhiteSpace(toolArguments))
        {
            startInfo.ArgumentList.Add("--tool-args");
            startInfo.ArgumentList.Add(toolArguments);
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
            "找不到本机控制令牌。请先点击“连接”，再重新打开 Codex 飞书助手。");
    }

    public void Dispose() => httpClient.Dispose();

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private sealed class ControlTokenFile
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = "";
    }
}
