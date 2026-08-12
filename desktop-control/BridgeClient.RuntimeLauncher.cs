using System.Diagnostics;
using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiCliFeishuControl;

internal sealed partial class BridgeClient
{
    public async Task LaunchRuntimeAsync(
        RuntimeProfile runtime,
        string cwd,
        bool elevated,
        string? rawArguments = null,
        CancellationToken cancellationToken = default,
        string? launchCorrelationId = null)
    {
        var fullPath = Path.GetFullPath(cwd);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("选择的项目目录不存在。");
        }

        var toolCommand = string.Equals(
                runtime.Id,
                RuntimeCatalog.Codex.Id,
                StringComparison.Ordinal)
            ? BridgeEnvironmentReader.Read(BridgeRoot, "CODEX_COMMAND") ??
                runtime.CommandName
            : runtime.RequiresResolvedCommand
                ? FindRuntimeCommand(runtime)
                : null;
        if (runtime.RequiresResolvedCommand && toolCommand is null)
        {
            throw new InvalidOperationException(
                $"找不到 {runtime.DisplayName} CLI。请先安装 {runtime.CommandName}，并确保其在 PATH 或常见用户安装目录中。");
        }

        await EnsureHooksInstalledAsync(runtime, cancellationToken);

        if (runtime.UsesManagedTerminal)
        {
            var managedArguments = RuntimeArgumentParser.Parse(runtime, rawArguments);
            var managedResumeSessionId = RuntimeArgumentParser.ExtractResumeSessionId(
                runtime,
                managedArguments);
            if (!string.IsNullOrWhiteSpace(managedResumeSessionId))
            {
                var currentStatus = await GetStatusAsync(
                    cancellationToken,
                    forceRefresh: true);
                var existingSession = currentStatus?.Sessions.FirstOrDefault(session =>
                    string.Equals(session.SessionId, managedResumeSessionId, StringComparison.Ordinal) &&
                    session.ManagedTerminalOnline);
                if (existingSession is not null)
                {
                    throw new InvalidOperationException(
                        $"会话 #{existingSession.ShortId} 已在托管窗口运行，请直接切换到现有 Codex 窗口。");
                }
            }
            var launch = StartManagedTerminalCore(
                fullPath,
                elevated,
                runtime,
                managedArguments.Count == 0
                    ? null
                    : rawArguments,
                toolCommand);
            using (launch.Launcher)
            {
                var status = await ManagedTerminalLaunchWaiter.WaitAsync(
                    launch.TerminalId,
                    token => ProbeManagedTerminalStatusAsync(launch.TerminalId, token),
                    () => LauncherFailure(
                        launch.Launcher,
                        launch.AllowsCleanEarlyExit,
                        runtime),
                    confirmation: launchCorrelationId is not null
                            ? ManagedTerminalLaunchConfirmation.SessionBound
                            : ManagedTerminalLaunchConfirmation.TerminalReady,
                    expectedSessionExternalId: launchCorrelationId is not null
                        ? managedResumeSessionId
                        : null,
                    cancellationToken: cancellationToken);
                AppLog.Info(
                    $"Bridge 已确认 {runtime.DisplayName} 托管终端 ready：" +
                    $"terminal={launch.TerminalId} " +
                    $"session={status.SessionExternalId ?? "pending"}。");
            }
            return;
        }

        var parsedArguments = RuntimeArgumentParser.Parse(runtime, rawArguments);
        var resumeSessionId = RuntimeArgumentParser.ExtractResumeSessionId(
            runtime,
            parsedArguments);
        var reservation = await ReserveOpenCodePortAsync(
            fullPath,
            resumeSessionId ?? launchCorrelationId,
            cancellationToken);
        var port = reservation.Port;
        var controlExecutable = Application.ExecutablePath;
        if (!File.Exists(controlExecutable))
        {
            await ReleaseOpenCodePortAsync(port, CancellationToken.None);
            throw new FileNotFoundException("找不到 AI CLI 飞书助手程序。", controlExecutable);
        }
        var terminalHost = FindTerminalHost(controlExecutable);
        if (!File.Exists(terminalHost))
        {
            await ReleaseOpenCodePortAsync(port, CancellationToken.None);
            throw new FileNotFoundException(
                "找不到 Windows Terminal 同步宿主，请重新安装或更新 AI CLI 飞书助手。",
                terminalHost);
        }
        var windowsTerminal = FindWindowsTerminal();
        var startInfo = BuildHttpRuntimeTerminalStartInfo(
            runtime,
            windowsTerminal,
            terminalHost,
            toolCommand!,
            port,
            fullPath,
            elevated,
            parsedArguments);
        try
        {
            using var launcher = Process.Start(startInfo) ??
                throw new InvalidOperationException($"无法启动 {runtime.DisplayName} 终端宿主。");
            await ConfirmLauncherStartedAsync(
                launcher,
                windowsTerminal is not null,
                runtime,
                cancellationToken);
            var status = await OpenCodeLaunchWaiter.WaitAsync(
                port,
                reservation.Generation,
                token => ProbeOpenCodeStatusAsync(port, token),
                () => LauncherFailure(
                    launcher,
                    windowsTerminal is not null,
                    runtime),
                cancellationToken: cancellationToken);
            AppLog.Info(
                $"Bridge 已确认 {runtime.DisplayName} 端点 ready" +
                $"（cwd={fullPath}，port={port}，generation={status.Generation}）。");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            await ReleaseOpenCodePortAsync(port, CancellationToken.None);
            throw new OperationCanceledException("已取消管理员权限确认。", error);
        }
        catch
        {
            await ReleaseOpenCodePortAsync(port, CancellationToken.None);
            throw;
        }
    }

    private ManagedTerminalLaunchReceipt StartManagedTerminalCore(
        string cwd,
        bool elevated,
        RuntimeProfile runtime,
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
            throw new FileNotFoundException("找不到 AI CLI 飞书助手程序。", controlExecutable);
        }
        var terminalHost = FindTerminalHost(controlExecutable);
        if (!File.Exists(terminalHost))
        {
            throw new FileNotFoundException(
                "找不到 Windows Terminal 同步宿主，请重新安装或更新 AI CLI 飞书助手。",
                terminalHost);
        }

        var terminalId = Guid.NewGuid().ToString("N");
        var normalizedArguments = toolArguments?.Trim() ?? "";
        if (normalizedArguments.Length > 4_000 ||
            normalizedArguments.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new InvalidOperationException($"{runtime.DisplayName} 启动参数无效或过长。");
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
            var launcher = Process.Start(startInfo) ??
                throw new InvalidOperationException($"无法启动 {runtime.DisplayName} 终端宿主。");
            return new(terminalId, launcher, windowsTerminal is not null);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("已取消管理员权限确认。", error);
        }
    }

    private static async Task ConfirmLauncherStartedAsync(
        Process launcher,
        bool allowsCleanEarlyExit,
        RuntimeProfile runtime,
        CancellationToken cancellationToken)
    {
        var exited = launcher.WaitForExitAsync(cancellationToken);
        var confirmation = Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        var completed = await Task.WhenAny(exited, confirmation);
        if (completed == confirmation)
        {
            await confirmation;
            return;
        }
        await exited;
        if (launcher.ExitCode != 0 || !allowsCleanEarlyExit)
        {
            throw new InvalidOperationException(
                $"{runtime.DisplayName} 终端宿主在启动确认前退出（代码 {launcher.ExitCode}）。");
        }
    }

    private async Task<ManagedTerminalLaunchStatus?> ProbeManagedTerminalStatusAsync(
        string terminalId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"managed-terminals/{Uri.EscapeDataString(terminalId)}/status");
            request.Headers.Add(ControlTokenHeader, ReadControlToken(BridgeRoot));
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or
                System.Net.HttpStatusCode.ServiceUnavailable)
            {
                AppLog.WarnThrottled(
                    $"等待托管终端 ready 时 Bridge 返回 HTTP {(int)response.StatusCode}，将重试。",
                    TimeSpan.FromSeconds(2));
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"读取托管终端状态失败：HTTP {(int)response.StatusCode}。");
            }
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<ManagedTerminalLaunchStatus>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (Exception error) when (
            !cancellationToken.IsCancellationRequested &&
            error is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            AppLog.WarnThrottled(
                $"等待托管终端 ready 的状态请求失败：{error.Message}",
                TimeSpan.FromSeconds(2));
            return null;
        }
    }

    private async Task<OpenCodeLaunchStatus?> ProbeOpenCodeStatusAsync(
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"opencode/endpoints/{port}/status");
            request.Headers.Add(ControlTokenHeader, ReadControlToken(BridgeRoot));
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or
                System.Net.HttpStatusCode.ServiceUnavailable)
            {
                AppLog.WarnThrottled(
                    $"等待 OpenCode 端点 ready 时 Bridge 返回 HTTP {(int)response.StatusCode}，将重试。",
                    TimeSpan.FromSeconds(2));
                return null;
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"读取 OpenCode 端点状态失败：HTTP {(int)response.StatusCode}。");
            }
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<OpenCodeLaunchStatus>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch (Exception error) when (
            !cancellationToken.IsCancellationRequested &&
            error is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            AppLog.WarnThrottled(
                $"等待 OpenCode 端点 ready 的状态请求失败：{error.Message}",
                TimeSpan.FromSeconds(2));
            return null;
        }
    }

    private static Exception? LauncherFailure(
        Process launcher,
        bool allowsCleanEarlyExit,
        RuntimeProfile runtime)
    {
        try
        {
            if (!launcher.HasExited || allowsCleanEarlyExit && launcher.ExitCode == 0)
            {
                return null;
            }
            return new InvalidOperationException(
                $"{runtime.DisplayName} 终端宿主在 SessionStart 前退出（代码 {launcher.ExitCode}）。");
        }
        catch (Exception error) when (
            error is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return new InvalidOperationException(
                $"无法确认 {runtime.DisplayName} 终端宿主仍在运行。",
                error);
        }
    }

    private async Task<OpenCodeLaunchResult> ReserveOpenCodePortAsync(
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
            "X-AI-CLI-Feishu-Control-Token",
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
        if (result.Generation <= 0)
        {
            throw new InvalidOperationException("Bridge 返回了无效的 opencode 端点代际。");
        }
        return result;
    }

    private async Task ReleaseOpenCodePortAsync(
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "opencode/unregister")
            {
                Content = JsonContent.Create(new { port }),
            };
            request.Headers.Add(
                "X-AI-CLI-Feishu-Control-Token",
                ReadControlToken(BridgeRoot));
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Warn($"释放 opencode 端口 {port} 失败：HTTP {(int)response.StatusCode}。");
            }
        }
        catch (Exception error)
        {
            AppLog.Warn($"释放 opencode 端口 {port} 失败：{error.Message}");
        }
    }

    private ProcessStartInfo BuildHttpRuntimeTerminalStartInfo(
        RuntimeProfile runtime,
        string? windowsTerminal,
        string terminalHost,
        string toolCommand,
        int port,
        string cwd,
        bool elevated,
        IReadOnlyList<string> toolArguments)
    {
        var terminalId = Guid.NewGuid().ToString("N");
        var startInfo = new ProcessStartInfo
        {
            FileName = windowsTerminal ?? terminalHost,
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
            startInfo.ArgumentList.Add($"{runtime.DisplayName} · {new DirectoryInfo(cwd).Name}{(elevated ? " · 管理员" : "")}");
            startInfo.ArgumentList.Add("--startingDirectory");
            startInfo.ArgumentList.Add(cwd);
            startInfo.ArgumentList.Add(terminalHost);
        }
        startInfo.ArgumentList.Add("--managed-terminal");
        startInfo.ArgumentList.Add("--id");
        startInfo.ArgumentList.Add(terminalId);
        startInfo.ArgumentList.Add("--cwd");
        startInfo.ArgumentList.Add(cwd);
        startInfo.ArgumentList.Add("--bridge-url");
        startInfo.ArgumentList.Add($"http://127.0.0.1:{Port}");
        startInfo.ArgumentList.Add("--bridge-root");
        startInfo.ArgumentList.Add(BridgeRoot);
        startInfo.ArgumentList.Add("--runtime");
        startInfo.ArgumentList.Add(runtime.Id);
        startInfo.ArgumentList.Add("--tool-command");
        startInfo.ArgumentList.Add(toolCommand);
        startInfo.ArgumentList.Add("--tool-arg");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add("--tool-arg");
        startInfo.ArgumentList.Add(
            port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var argument in toolArguments)
        {
            startInfo.ArgumentList.Add("--tool-arg");
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static string? FindRuntimeCommand(RuntimeProfile runtime)
    {
        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(entry.Trim('"'), $"{runtime.CommandName}.exe"));
            candidates.Add(Path.Combine(entry.Trim('"'), $"{runtime.CommandName}.cmd"));
        }
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(userProfile, ".local", "bin", $"{runtime.CommandName}.exe"));
        candidates.Add(Path.Combine(userProfile, ".local", "bin", $"{runtime.CommandName}.cmd"));
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        candidates.Add(Path.Combine(appData, "npm", $"{runtime.CommandName}.cmd"));
        if (!string.IsNullOrWhiteSpace(runtime.LocalProgramDirectory))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(
                localAppData,
                "Programs",
                runtime.LocalProgramDirectory,
                $"{runtime.CommandName}.exe"));
        }
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
        RuntimeProfile runtime,
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
        startInfo.ArgumentList.Add($"{runtime.DisplayName} · {new DirectoryInfo(cwd).Name}{(elevated ? " · 管理员" : "")}");
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
        RuntimeProfile runtime,
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
        RuntimeProfile runtime,
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
        startInfo.ArgumentList.Add("--bridge-root");
        startInfo.ArgumentList.Add(BridgeRoot);
        startInfo.ArgumentList.Add("--runtime");
        startInfo.ArgumentList.Add(runtime.Id);
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
                $"AiCliFeishuTerminalHost-{version.Major}.{version.Minor}.{version.Build}.exe");
            if (File.Exists(versionedPath))
            {
                return versionedPath;
            }
        }
        return Path.Combine(directory, "AiCliFeishuTerminalHost.exe");
    }

}
