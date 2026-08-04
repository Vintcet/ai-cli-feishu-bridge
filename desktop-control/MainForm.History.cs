namespace CodexFeishuControl;

internal sealed partial class MainForm
{
    private void ConfigureHistoryGrid()
    {
        ConfigureGrid(historyGrid);
        historyGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Alias",
            HeaderText = "别名",
            Width = 120,
        });
        historyGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Project",
            HeaderText = "项目",
            Width = 145,
        });
        historyGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ShortId",
            HeaderText = "会话 ID",
            Width = 95,
        });
        historyGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Mode",
            HeaderText = "启动方式",
            Width = 115,
        });
        historyGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "FeishuGroup",
            HeaderText = "飞书群",
            Width = 135,
        });
        historyGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Cwd",
            HeaderText = "工作目录",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 220,
        });
        historyGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "OpenedAt",
            HeaderText = "打开时间",
            Width = 135,
        });
        historyGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "ClosedAt",
            HeaderText = "最后活动",
            Width = 140,
        });
        historyGrid.SelectionChanged += (_, _) => UpdateSessionActionState();
        historyGrid.CellDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex < 0 ||
                historyGrid.Rows[eventArgs.RowIndex].Tag is not CodexSession)
            {
                return;
            }
            historyGrid.CurrentCell = historyGrid.Rows[eventArgs.RowIndex].Cells[0];
            ContinueSelectedHistory();
        };
    }

    private void PopulateHistoryGrid(
        IReadOnlyList<CodexSession> sessions,
        string? selectedSessionId,
        int scrollIndex)
    {
        historyGrid.Rows.Clear();
        foreach (var session in sessions.OrderByDescending(
                     item => ParseTime(string.IsNullOrWhiteSpace(item.EndedAt)
                         ? item.LastSeenAt
                         : item.EndedAt)))
        {
            var rowIndex = historyGrid.Rows.Add(
                string.IsNullOrWhiteSpace(session.Alias) ? "—" : $"@{session.Alias}",
                session.ProjectName,
                $"#{session.ShortId}",
                HistoryModeLabel(session),
                FeishuGroupLabel(session),
                session.Cwd,
                FormatTime(session.OpenedAt),
                FormatTime(string.IsNullOrWhiteSpace(session.EndedAt)
                    ? session.LastSeenAt
                    : session.EndedAt));
            var row = historyGrid.Rows[rowIndex];
            row.Tag = session;
            if (!string.IsNullOrWhiteSpace(session.Alias))
            {
                row.Cells["Alias"].Style.ForeColor = Primary;
                row.Cells["Alias"].Style.Font = gridBoldFont;
            }
        }
        RestoreGridState(historyGrid, selectedSessionId, scrollIndex);
    }

    private async void ContinueSelectedHistory()
    {
        if (operating || historyGrid.CurrentRow?.Tag is not CodexSession session)
        {
            return;
        }
        if (!Directory.Exists(session.Cwd))
        {
            MessageBox.Show(
                this,
                $"原工作目录不存在：\r\n{session.Cwd}",
                "无法继续对话",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        SetOperating(true, $"正在恢复 {SessionDisplayName(session)}…");
        try
        {
            var runtime = RuntimeCatalog.FromId(session.Runtime);
            await bridgeClient.LaunchRuntimeAsync(
                runtime,
                session.Cwd,
                session.ManagedTerminalElevated,
                runtime.BuildResumeArguments(session.SessionId),
                lifetime.Token);
            operationLabel.Text = session.ManagedTerminalElevated
                ? $"已请求以管理员身份继续 {SessionDisplayName(session)}；完成 UAC 确认后，{runtime.DisplayName} 会在新窗口恢复"
                : $"{runtime.DisplayName} 窗口已启动，正在恢复 {SessionDisplayName(session)}";
            sessionTabs.SelectedIndex = 0;
        }
        catch (OperationCanceledException error)
        {
            operationLabel.Text = error.Message;
        }
        catch (Exception error)
        {
            ShowOperationError("继续对话失败", error);
        }
        finally
        {
            SetOperating(false);
            UpdateSessionActionState();
        }
    }

    private async Task DeleteSelectedHistoryAsync()
    {
        if (operating || historyGrid.CurrentRow?.Tag is not CodexSession session)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"确定从助手的历史记录中删除 {SessionDisplayName(session)} 吗？\r\n\r\n" +
            "这不会删除原 CLI 对话或项目文件，之后仍可使用完整会话 ID 手动恢复。",
            "删除历史记录",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        SetOperating(true, $"正在删除 {SessionDisplayName(session)} 的历史记录…");
        try
        {
            await bridgeClient.HideSessionFromHistoryAsync(session.SessionId, lifetime.Token);
            await RefreshStatusAsync(force: true);
            operationLabel.Text = $"已从助手历史记录中删除 {SessionDisplayName(session)}";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            ShowOperationError("删除历史记录失败", error);
        }
        finally
        {
            SetOperating(false);
            UpdateSessionActionState();
        }
    }

    private static string HistoryModeLabel(CodexSession session) =>
        $"{RuntimeShortLabel(session)} {(session.ManagedTerminalElevated ? "管理员" : "普通")}";

    private static string RuntimeShortLabel(CodexSession session) =>
        RuntimeCatalog.FromId(session.Runtime).ShortName;
}
