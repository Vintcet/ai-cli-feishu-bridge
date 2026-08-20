using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AiCliFeishuControl;

internal sealed partial class MainForm
{
    private async Task EditSelectedAliasAsync()
    {
        var session = sessionTabs.SelectedIndex == 1
            ? historyGrid.CurrentRow?.Tag as AssistantSession
            : sessionGrid.CurrentRow?.Tag as AssistantSession;
        if (operating || session is null)
        {
            return;
        }

        using var dialog = new SessionAliasDialog(session);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SetOperating(true, "正在保存会话别名…");
        try
        {
            await bridgeClient.SetSessionAliasAsync(
                session.SessionId,
                dialog.Alias,
                lifetime.Token);
            operationLabel.Text = dialog.Alias is null
                ? $"已清除 #{session.ShortId} 的别名"
                : $"已将 #{session.ShortId} 的别名设为 @{dialog.Alias}";
            await RefreshStatusAsync(force: true);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            ShowOperationError("设置别名失败", error);
        }
        finally
        {
            SetOperating(false);
            UpdateSessionActionState();
        }
    }

    private void OpenSelectedSessionDirectory()
    {
        if (operating)
        {
            return;
        }
        var session = sessionTabs.SelectedIndex == 1
            ? historyGrid.CurrentRow?.Tag as AssistantSession
            : sessionGrid.CurrentRow?.Tag as AssistantSession;
        if (session is null)
        {
            return;
        }
        if (!Directory.Exists(session.Cwd))
        {
            MessageBox.Show(
                this,
                $"工作目录不存在：\r\n{session.Cwd}",
                "无法打开目录",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", session.Cwd)
        {
            UseShellExecute = true,
        });
    }

    private async Task RetrySelectedSessionGroupAsync()
    {
        if (operating || sessionGrid.CurrentRow?.Tag is not AssistantSession session)
        {
            return;
        }
        SetOperating(true, $"正在为 {SessionDisplayName(session)} 创建飞书群…");
        try
        {
            await bridgeClient.RetrySessionGroupAsync(session.SessionId, lifetime.Token);
            operationLabel.Text = $"已为 {SessionDisplayName(session)} 创建飞书群";
            await RefreshStatusAsync(force: true);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            ShowOperationError("创建飞书群失败", error);
        }
        finally
        {
            SetOperating(false);
            UpdateSessionActionState();
        }
    }

    private async Task EditSettingsAsync()
    {
        if (operating)
        {
            return;
        }
        var status = lastStatus ?? await bridgeClient.GetStatusAsync(lifetime.Token);
        if (status is null)
        {
            MessageBox.Show(
                this,
                "请先点击“连接”启动桥接服务，再修改设置。",
                "桥接服务未运行",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        using var dialog = new SettingsDialog(status.Settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        SetOperating(true, "正在保存通知设置…");
        try
        {
            status.Settings = await bridgeClient.UpdateSettingsAsync(
                dialog.Settings,
                lifetime.Token);
            SyncApprovalDialog(status);
            operationLabel.Text = "设置已保存并立即生效";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            ShowOperationError("保存设置失败", error);
        }
        finally
        {
            SetOperating(false);
        }
    }

    private async Task NewRuntimeAsync(RuntimeProfile runtime)
    {
        if (operating || launching) return;
        try
        {
            var status = await bridgeClient.GetStatusAsync(lifetime.Token);
            if (status is null)
            {
                await ConnectAsync();
                status = await bridgeClient.GetStatusAsync(lifetime.Token);
            }
            if (status is null)
            {
                throw new InvalidOperationException(
                    $"请先连接飞书桥接服务，再新建 {runtime.DisplayName} 窗口。");
            }

            var initialDirectory = lastProjectDirectory ??
                Directory.GetParent(bridgeClient.BridgeRoot)?.FullName ??
                bridgeClient.BridgeRoot;
            var knownSessions = status.Sessions
                .Concat(status.HistorySessions)
                .GroupBy(session => session.SessionId)
                .Select(group => group.First())
                .ToList();
            using var dialog = new NewRuntimeDialog(
                runtime,
                initialDirectory,
                knownSessions);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            lastProjectDirectory = dialog.SelectedDirectory;
            SetLaunching(
                true,
                dialog.RunAsAdministrator
                    ? $"正在请求管理员启动 {runtime.DisplayName}…"
                    : $"正在启动 {runtime.DisplayName} 窗口…");
            try
            {
                await bridgeClient.LaunchRuntimeAsync(
                    runtime,
                    dialog.SelectedDirectory,
                    dialog.RunAsAdministrator,
                    dialog.Arguments,
                    lifetime.Token);
            }
            finally
            {
                SetLaunching(false);
            }
            operationLabel.Text = dialog.RunAsAdministrator
                ? $"已请求管理员启动；完成 UAC 确认后，Windows Terminal 窗口会自动登记 {runtime.DisplayName}"
                : $"Windows Terminal / {runtime.DisplayName} 窗口已启动，正在等待会话登记";
        }
        catch (OperationCanceledException error) when (!lifetime.IsCancellationRequested)
        {
            operationLabel.Text = error.Message;
        }
        catch (Exception error)
        {
            ShowOperationError($"新建 {runtime.DisplayName} 失败", error);
        }
    }

    private async Task ConnectAsync()
    {
        if (operating) return;
        SetOperating(true, "正在启动桥接服务…");
        try
        {
            await bridgeClient.RefreshTargetAsync(lifetime.Token);
            await bridgeClient.StartAsync(lifetime.Token);
            BridgeStatus? status = null;
            for (var attempt = 0; attempt < 20 && status?.Ok != true; attempt++)
            {
                await Task.Delay(400, lifetime.Token);
                status = await bridgeClient.GetStatusAsync(lifetime.Token);
            }
            if (status?.Ok != true)
            {
                throw new InvalidOperationException("桥接服务没有在预期时间内启动。");
            }
            ApplyStatus(status);
            operationLabel.Text = "连接命令已执行，飞书状态会自动更新";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            ShowOperationError("连接失败", error);
        }
        finally
        {
            SetOperating(false);
        }
    }

    private async Task DisconnectAsync()
    {
        if (operating) return;
        SetOperating(true, "正在断开桥接服务…");
        try
        {
            await bridgeClient.StopAsync(lifetime.Token);
            var stopped = false;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (await bridgeClient.GetStatusAsync(lifetime.Token) is null)
                {
                    stopped = true;
                    break;
                }
                await Task.Delay(250, lifetime.Token);
            }
            if (!stopped)
            {
                throw new InvalidOperationException(
                    "桥接服务仍在运行，界面不会把它误报为已断开。请稍后重试。");
            }
            ApplyOfflineStatus();
            operationLabel.Text = "已断开；下次使用时请手动点击“连接”";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            ShowOperationError("断开失败", error);
        }
        finally
        {
            SetOperating(false);
        }
    }

    private async Task RefreshStatusAsync(bool force = false)
    {
        if ((refreshing || operating) && !force) return;
        refreshing = true;
        try
        {
            var status = await bridgeClient.GetStatusAsync(
                lifetime.Token,
                forceRefresh: force);
            if (status is null)
            {
                ApplyOfflineStatus();
            }
            else
            {
                ApplyStatus(status);
                if (bridgeClient.IsProductionTarget)
                {
                    BeginPendingRuntimeLaunch();
                }
            }
            lastRefreshLabel.Text = $"最后刷新：{DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            AppLog.Error("刷新状态异常", error);
            operationLabel.Text = $"刷新失败：{error.Message}";
        }
        finally
        {
            refreshing = false;
        }
    }

    // Claiming and launching a runtime can take minutes, so it must not be awaited
    // inside the refresh cycle: that would keep `refreshing` set and make the 15s
    // timer skip every tick, freezing sessions and approvals for the whole startup.
    private void BeginPendingRuntimeLaunch()
    {
        if (operating || launching || closing || lifetime.IsCancellationRequested)
        {
            return;
        }
        _ = ProcessPendingRuntimeLaunchAsync();
    }

    private async Task ProcessPendingRuntimeLaunchAsync()
    {
        if (operating || launching || closing || lifetime.IsCancellationRequested)
        {
            return;
        }

        RuntimeLaunchRequest? request;
        try
        {
            request = await bridgeClient.ClaimRuntimeLaunchAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error)
        {
            AppLog.WarnThrottled(
                $"读取自动恢复请求失败：{error.Message}",
                TimeSpan.FromSeconds(10));
            return;
        }
        if (request is null)
        {
            return;
        }

        var isNewLaunch = request.Kind.Equals("new", StringComparison.OrdinalIgnoreCase);
        SetLaunching(
            true,
            isNewLaunch
                ? $"正在从飞书新建项目 {request.ProjectName}…"
                : $"正在从飞书自动恢复 #{ShortSessionId(request.SessionId)}…");
        try
        {
            if (!RuntimeCatalog.TryGet(request.Runtime, out var runtime))
            {
                throw new InvalidOperationException($"不支持的运行时：{request.Runtime}");
            }
            await bridgeClient.LaunchRuntimeAsync(
                runtime,
                request.Cwd,
                request.Elevated,
                isNewLaunch ? null : runtime.BuildResumeArguments(request.SessionId),
                lifetime.Token,
                request.SessionId);
            await bridgeClient.CompleteRuntimeLaunchAsync(
                request.RequestId,
                success: true,
                cancellationToken: lifetime.Token);
            operationLabel.Text = isNewLaunch
                ? $"已从飞书启动 {runtime.DisplayName} 项目 {request.ProjectName}"
                : $"已从飞书请求自动打开 {runtime.DisplayName} #{ShortSessionId(request.SessionId)}";
        }
        catch (OperationCanceledException error) when (!lifetime.IsCancellationRequested)
        {
            await ReportRuntimeLaunchFailureAsync(request, error.Message);
            operationLabel.Text = error.Message;
        }
        catch (Exception error)
        {
            await ReportRuntimeLaunchFailureAsync(request, error.Message);
            AppLog.Error(isNewLaunch ? "飞书新建会话失败" : "自动恢复会话失败", error);
            operationLabel.Text = isNewLaunch
                ? $"飞书新建会话失败：{error.Message}"
                : $"自动恢复会话失败：{error.Message}";
        }
        finally
        {
            SetLaunching(false);
        }
    }

    private async Task ReportRuntimeLaunchFailureAsync(
        RuntimeLaunchRequest request,
        string error)
    {
        try
        {
            await bridgeClient.CompleteRuntimeLaunchAsync(
                request.RequestId,
                success: false,
                error: error,
                cancellationToken: CancellationToken.None);
        }
        catch (Exception reportError)
        {
            AppLog.Error("提交自动恢复失败结果异常", reportError);
        }
    }

}
