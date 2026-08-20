using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AiCliFeishuControl;

internal sealed partial class MainForm
{
    private void ApplyStatus(BridgeStatus status)
    {
        if (lastStatus is null)
        {
            AppLog.Info(
                $"桥接在线：version={status.Version} feishu={status.Feishu.State} " +
                $"activeSessions={status.ActiveSessions} sessions={status.Sessions.Count} " +
                $"history={status.HistorySessions.Count}");
        }
        var selectedSessionId =
            (sessionGrid.CurrentRow?.Tag as AssistantSession)?.SessionId;
        var selectedHistorySessionId =
            (historyGrid.CurrentRow?.Tag as AssistantSession)?.SessionId;
        var sessionScrollIndex = sessionGrid.Rows.Count > 0
            ? sessionGrid.FirstDisplayedScrollingRowIndex
            : -1;
        var historyScrollIndex = historyGrid.Rows.Count > 0
            ? historyGrid.FirstDisplayedScrollingRowIndex
            : -1;

        lastStatus = status;
        var feishuState = status.Feishu.State.ToLowerInvariant();
        serviceValue.Text = "运行中";
        feishuValue.Text = FeishuStateLabel(feishuState);
        bindingsValue.Text = status.Bindings.ToString();
        sessionsValue.Text = status.ActiveSessions.ToString();

        if (feishuState == "connected")
        {
            SetHeaderStatus("飞书已连接", Success);
        }
        else if (feishuState is "connecting" or "reconnecting")
        {
            SetHeaderStatus("飞书连接中", Warning);
        }
        else
        {
            SetHeaderStatus("飞书未连接", Danger);
        }

        // A background launch owns the label until it finishes; a refresh landing
        // mid-launch must not overwrite its progress message.
        if (!launching)
        {
            operationLabel.Text = StatusMessage(status);
        }
        connectionButton.Text = "断开";
        connectionButton.BackColor = Color.White;
        connectionButton.ForeColor = Danger;
        connectionButton.FlatAppearance.BorderColor = Border;
        connectionButton.Enabled = !operating;
        newCodexButton.Enabled = LaunchEntriesEnabled;
        newClaudeCodeButton.Enabled = LaunchEntriesEnabled;
        newOpenCodeButton.Enabled = LaunchEntriesEnabled;
        approvalButton.Enabled =
            !operating && bridgeClient.IsProductionTarget && status.PendingDesktopApprovals > 0;
        refreshButton.Enabled = !operating;
        settingsButton.Enabled = !operating && bridgeClient.IsProductionTarget;

        sessionTabs.TabPages[0].Text = $"活跃会话 ({status.Sessions.Count})";
        sessionTabs.TabPages[1].Text = $"历史记录 ({status.HistorySessions.Count})";

        sessionGrid.Rows.Clear();
        foreach (var session in status.Sessions)
        {
            var displayedStatus = session.RemoteResumeRunning
                ? "远程运行中"
                : session.ManagedTerminal && !session.ManagedTerminalOnline
                    ? "窗口已关闭"
                    : session.ManagedTerminal && !session.ManagedTerminalReady
                        ? "正在启动"
                        : session.ManagedTerminal && session.Status == "running"
                            ? "窗口运行中"
                            : session.StatusLabel;
            var mode = SessionModeLabel(session);
            var rowIndex = sessionGrid.Rows.Add(
                string.IsNullOrWhiteSpace(session.Alias) ? "—" : $"@{session.Alias}",
                session.ProjectName,
                $"#{session.ShortId}",
                displayedStatus,
                session.QueuedPrompts > 0 ? session.QueuedPrompts.ToString() : "—",
                session.Model,
                mode,
                FeishuGroupLabel(session),
                session.Cwd,
                FormatTime(session.OpenedAt),
                FormatTime(session.LastSeenAt));
            var row = sessionGrid.Rows[rowIndex];
            row.Tag = session;
            if (!string.IsNullOrWhiteSpace(session.Alias))
            {
                row.Cells["Alias"].Style.ForeColor = Primary;
                row.Cells["Alias"].Style.Font = gridBoldFont;
            }
            if (session.ManagedTerminal && !session.ManagedTerminalOnline)
            {
                row.Cells["Status"].Style.ForeColor = Danger;
                row.Cells["Status"].Style.Font = gridBoldFont;
            }
            else if (session.ManagedTerminal && !session.ManagedTerminalReady)
            {
                row.Cells["Status"].Style.ForeColor = Warning;
                row.Cells["Status"].Style.Font = gridBoldFont;
            }
            else if (session.Status is "pending_approval" or "pending_input")
            {
                row.Cells["Status"].Style.ForeColor = Warning;
                row.Cells["Status"].Style.Font = gridBoldFont;
            }
            else if (session.Status == "error")
            {
                row.Cells["Status"].Style.ForeColor = Danger;
                row.Cells["Status"].Style.Font = gridBoldFont;
            }
            else if (session.Status == "running")
            {
                row.Cells["Status"].Style.ForeColor = Success;
            }
            if (session.FeishuChatStatus == "error")
            {
                row.Cells["FeishuGroup"].Style.ForeColor = Danger;
            }
            else if (session.FeishuChatStatus == "pending")
            {
                row.Cells["FeishuGroup"].Style.ForeColor = Warning;
            }
        }
        RestoreGridState(sessionGrid, selectedSessionId, sessionScrollIndex);
        PopulateHistoryGrid(
            status.HistorySessions,
            selectedHistorySessionId,
            historyScrollIndex);
        UpdateSessionActionState();
        SyncApprovalDialog(status);
    }

    private void ApplyOfflineStatus()
    {
        if (lastStatus is not null)
        {
            AppLog.Warn("桥接服务离线（/health 失败）。");
        }
        lastStatus = null;
        serviceValue.Text = "未运行";
        feishuValue.Text = "已断开";
        bindingsValue.Text = "—";
        sessionsValue.Text = "0";
        SetHeaderStatus("服务未运行", Danger);
        connectionButton.Text = "连接";
        connectionButton.BackColor = Primary;
        connectionButton.ForeColor = Color.White;
        connectionButton.FlatAppearance.BorderColor = Primary;
        connectionButton.Enabled = !operating;
        newCodexButton.Enabled = LaunchEntriesEnabled;
        newClaudeCodeButton.Enabled = LaunchEntriesEnabled;
        newOpenCodeButton.Enabled = LaunchEntriesEnabled;
        approvalButton.Text = "本机审批";
        approvalButton.Enabled = false;
        refreshButton.Enabled = !operating;
        settingsButton.Enabled = !operating && bridgeClient.IsProductionTarget;
        sessionTabs.TabPages[0].Text = "活跃会话";
        sessionTabs.TabPages[1].Text = "历史记录";
        sessionGrid.Rows.Clear();
        historyGrid.Rows.Clear();
        UpdateSessionActionState();
        dismissedApprovalIds.Clear();
        if (approvalDialog is not null)
        {
            var dialog = approvalDialog;
            approvalDialog = null;
            dialog.MarkResolved();
        }
        if (!operating && !launching)
        {
            operationLabel.Text = "点击“连接”启动飞书桥接服务";
        }
    }

    private void SetHeaderStatus(string text, Color color)
    {
        headerStatusLabel.Text = text;
        headerStatusDot.BackColor = color;
        headerStatusDot.Invalidate();
    }

    private static string StatusMessage(BridgeStatus status) => status switch
    {
        { PendingDesktopApprovals: > 0 } =>
            $"有 {status.PendingDesktopApprovals} 个操作已转回本机审批",
        { PendingApprovals: > 0 } =>
            $"有 {status.PendingApprovals} 个操作正在飞书等待审批",
        { PendingInputs: > 0 } =>
            $"有 {status.PendingInputs} 组问题等待你在飞书补充",
        { QueuedPrompts: > 0 } =>
            $"有 {status.QueuedPrompts} 条助手消息正在排队",
        { Bindings: 0 } when !string.IsNullOrWhiteSpace(status.BindingCommand) =>
            $"首次绑定：请在飞书私聊机器人发送“{status.BindingCommand}”",
        { Bindings: 0, OwnerConfigured: true } =>
            "管理员当前已解绑，请由原管理员私聊机器人发送“绑定”恢复",
        _ => $"桥接版本 {status.Version} · 服务运行正常",
    };

    private bool LaunchEntriesEnabled =>
        !operating && !launching && bridgeClient.IsProductionTarget;

    private void SetOperating(bool value, string? message = null)
    {
        operating = value;
        connectionButton.Enabled = !value;
        newCodexButton.Enabled = LaunchEntriesEnabled;
        newClaudeCodeButton.Enabled = LaunchEntriesEnabled;
        newOpenCodeButton.Enabled = LaunchEntriesEnabled;
        approvalButton.Enabled =
            !value && bridgeClient.IsProductionTarget &&
            (lastStatus?.Approvals.Count(
                item =>
                    item.Status == "pending" &&
                    item.RequiresManualApproval &&
                    item.DesktopApprovalRequested) ?? 0) > 0;
        UpdateSessionActionState();
        refreshButton.Enabled = !value;
        settingsButton.Enabled = !value && bridgeClient.IsProductionTarget;
        folderButton.Enabled = !value;
        if (!string.IsNullOrWhiteSpace(message))
        {
            operationLabel.Text = message;
        }
    }

    // Keeps the launch entries disabled for the whole background launch without
    // suppressing the refresh cycle the way SetOperating does.
    private void SetLaunching(bool value, string? message = null)
    {
        launching = value;
        newCodexButton.Enabled = LaunchEntriesEnabled;
        newClaudeCodeButton.Enabled = LaunchEntriesEnabled;
        newOpenCodeButton.Enabled = LaunchEntriesEnabled;
        UpdateSessionActionState();
        if (!string.IsNullOrWhiteSpace(message))
        {
            operationLabel.Text = message;
        }
    }

    private void UpdateSessionActionState()
    {
        var historySelected = sessionTabs.SelectedIndex == 1;
        aliasButton.Visible = true;
        retryGroupButton.Visible = !historySelected;
        folderButton.Visible = true;
        resumeSessionButton.Visible = historySelected;
        deleteHistoryButton.Visible = historySelected;
        aliasButton.Enabled =
            !operating && bridgeClient.IsProductionTarget &&
            (historySelected
                ? historyGrid.CurrentRow?.Tag is AssistantSession
                : sessionGrid.CurrentRow?.Tag is AssistantSession);
        retryGroupButton.Enabled =
            !operating && bridgeClient.IsProductionTarget &&
            !historySelected &&
            sessionGrid.CurrentRow?.Tag is AssistantSession session &&
            session.ManagedByAssistant &&
            session.FeishuChatStatus != "connected";
        resumeSessionButton.Enabled =
            !operating && !launching && bridgeClient.IsProductionTarget &&
            historySelected &&
            historyGrid.CurrentRow?.Tag is AssistantSession;
        deleteHistoryButton.Enabled = resumeSessionButton.Enabled;
        folderButton.Enabled = !operating &&
            (historySelected
                ? historyGrid.CurrentRow?.Tag is AssistantSession
                : sessionGrid.CurrentRow?.Tag is AssistantSession);
    }

    private static string FeishuGroupLabel(AssistantSession session) =>
        session.FeishuChatStatus switch
        {
            "connected" when !string.IsNullOrWhiteSpace(session.FeishuChatName) =>
                session.FeishuChatName,
            "connected" => "已连接",
            "pending" => "待创建",
            "error" => "创建失败",
            _ => "—",
        };

}
