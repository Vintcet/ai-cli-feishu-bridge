using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace CodexFeishuControl;

internal sealed class MainForm : Form
{
    private static readonly Color PageBackground = Color.FromArgb(244, 247, 251);
    private static readonly Color HeaderBackground = Color.FromArgb(15, 23, 42);
    private static readonly Color Primary = Color.FromArgb(37, 99, 235);
    private static readonly Color Success = Color.FromArgb(22, 163, 74);
    private static readonly Color Warning = Color.FromArgb(217, 119, 6);
    private static readonly Color Danger = Color.FromArgb(220, 38, 38);
    private static readonly Color Muted = Color.FromArgb(100, 116, 139);
    private static readonly Color Border = Color.FromArgb(226, 232, 240);

    private readonly BridgeClient bridgeClient = new();
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 2_000 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Font gridBoldFont = new("Microsoft YaHei UI", 9F, FontStyle.Bold);

    private readonly Label headerStatusLabel = new();
    private readonly Panel headerStatusDot = new();
    private readonly Label serviceValue = new();
    private readonly Label feishuValue = new();
    private readonly Label bindingsValue = new();
    private readonly Label sessionsValue = new();
    private readonly Label operationLabel = new();
    private readonly Label lastRefreshLabel = new();
    private readonly DataGridView sessionGrid = new();
    private readonly Button connectButton = new();
    private readonly Button disconnectButton = new();
    private readonly Button newCodexButton = new();
    private readonly Button aliasButton = new();
    private readonly Button refreshButton = new();
    private readonly Button folderButton = new();

    private bool refreshing;
    private bool operating;
    private string? lastProjectDirectory;

    public MainForm()
    {
        Text = "Codex 飞书助手";
        Icon = SystemIcons.Application;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 760);
        MinimumSize = new Size(1040, 640);
        BackColor = PageBackground;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildLayout();
        refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        Shown += async (_, _) =>
        {
            await RefreshStatusAsync();
            refreshTimer.Start();
        };
        FormClosed += (_, _) =>
        {
            refreshTimer.Stop();
            lifetime.Cancel();
            bridgeClient.Dispose();
            lifetime.Dispose();
            gridBoldFont.Dispose();
        };
    }

    private void BuildLayout()
    {
        Controls.Add(BuildMainArea());
        Controls.Add(BuildHeader());
    }

    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 96,
            BackColor = HeaderBackground,
            Padding = new Padding(28, 18, 28, 16),
        };

        var title = new Label
        {
            AutoSize = true,
            Text = "Codex 飞书助手",
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold),
            Location = new Point(26, 16),
        };
        var subtitle = new Label
        {
            AutoSize = true,
            Text = "管理飞书连接，并同步 Codex 插话、排队、确认、进度与文件",
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Microsoft YaHei UI", 9.5F),
            Location = new Point(29, 59),
        };

        var statusPanel = new Panel
        {
            Size = new Size(200, 42),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(ClientSize.Width - 228, 27),
            BackColor = Color.FromArgb(30, 41, 59),
        };
        statusPanel.Resize += (_, _) => statusPanel.Region = RoundedRegion(statusPanel.ClientRectangle, 20);
        statusPanel.Region = RoundedRegion(statusPanel.ClientRectangle, 20);

        headerStatusDot.Size = new Size(11, 11);
        headerStatusDot.Location = new Point(18, 15);
        headerStatusDot.BackColor = Muted;
        headerStatusDot.Paint += (_, eventArgs) =>
        {
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(headerStatusDot.BackColor);
            eventArgs.Graphics.FillEllipse(brush, 0, 0, 10, 10);
        };
        headerStatusLabel.AutoSize = false;
        headerStatusLabel.Location = new Point(39, 10);
        headerStatusLabel.Size = new Size(145, 24);
        headerStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
        headerStatusLabel.Text = "正在检查…";
        headerStatusLabel.ForeColor = Color.White;
        headerStatusLabel.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        statusPanel.Controls.Add(headerStatusDot);
        statusPanel.Controls.Add(headerStatusLabel);

        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        header.Controls.Add(statusPanel);
        header.Resize += (_, _) => statusPanel.Left = header.ClientSize.Width - statusPanel.Width - 28;
        return header;
    }

    private Control BuildMainArea()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 20, 24, 14),
            BackColor = PageBackground,
            ColumnCount = 1,
            RowCount = 4,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        root.Controls.Add(BuildToolbar(), 0, 0);
        root.Controls.Add(BuildMetrics(), 0, 1);
        root.Controls.Add(BuildSessionSection(), 0, 2);
        root.Controls.Add(BuildFooter(), 0, 3);
        return root;
    }

    private Control BuildToolbar()
    {
        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        operationLabel.Text = "桥接状态会每 2 秒自动刷新";
        operationLabel.AutoSize = true;
        operationLabel.ForeColor = Muted;
        operationLabel.Anchor = AnchorStyles.Left;
        operationLabel.Font = new Font("Microsoft YaHei UI", 9.5F);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty,
        };

        ConfigureButton(connectButton, "连接", Primary, Color.White);
        ConfigureButton(disconnectButton, "断开", Color.White, Danger, Border);
        ConfigureButton(newCodexButton, "新建 Codex", Success, Color.White);
        newCodexButton.Size = new Size(112, 38);
        ConfigureButton(refreshButton, "刷新", Color.White, Color.FromArgb(51, 65, 85), Border);
        ConfigureButton(folderButton, "打开目录", Color.White, Color.FromArgb(51, 65, 85), Border);

        connectButton.Click += async (_, _) => await ConnectAsync();
        disconnectButton.Click += async (_, _) => await DisconnectAsync();
        newCodexButton.Click += async (_, _) => await NewCodexAsync();
        refreshButton.Click += async (_, _) => await RefreshStatusAsync(force: true);
        folderButton.Click += (_, _) => bridgeClient.OpenBridgeFolder();

        buttons.Controls.Add(connectButton);
        buttons.Controls.Add(disconnectButton);
        buttons.Controls.Add(newCodexButton);
        buttons.Controls.Add(refreshButton);
        buttons.Controls.Add(folderButton);
        toolbar.Controls.Add(operationLabel, 0, 0);
        toolbar.Controls.Add(buttons, 1, 0);
        return toolbar;
    }

    private Control BuildMetrics()
    {
        var metrics = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 14),
        };
        for (var index = 0; index < 4; index++)
        {
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }

        metrics.Controls.Add(CreateMetricCard("桥接服务", serviceValue, Primary), 0, 0);
        metrics.Controls.Add(CreateMetricCard("飞书连接", feishuValue, Success), 1, 0);
        metrics.Controls.Add(CreateMetricCard("已绑定账号", bindingsValue, Color.FromArgb(124, 58, 237)), 2, 0);
        metrics.Controls.Add(CreateMetricCard("Codex 会话", sessionsValue, Warning), 3, 0);
        return metrics;
    }

    private Control CreateMetricCard(string title, Label valueLabel, Color accent)
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 12, 0),
            BackColor = Color.White,
            BorderColor = Border,
            CornerRadius = 12,
        };
        var stripe = new Panel
        {
            Dock = DockStyle.Left,
            Width = 5,
            BackColor = accent,
        };
        var titleLabel = new Label
        {
            AutoSize = true,
            Text = title,
            ForeColor = Muted,
            Font = new Font("Microsoft YaHei UI", 9F),
            Location = new Point(20, 18),
        };
        valueLabel.AutoSize = false;
        valueLabel.Text = "—";
        valueLabel.ForeColor = Color.FromArgb(15, 23, 42);
        valueLabel.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
        valueLabel.Location = new Point(19, 46);
        valueLabel.Size = new Size(185, 36);
        valueLabel.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        card.Controls.Add(valueLabel);
        card.Controls.Add(titleLabel);
        card.Controls.Add(stripe);
        return card;
    }

    private Control BuildSessionSection()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            BorderColor = Border,
            CornerRadius = 14,
            Padding = new Padding(18, 14, 18, 18),
            Margin = new Padding(0, 2, 0, 8),
        };

        var titlePanel = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.White };
        var title = new Label
        {
            AutoSize = true,
            Text = "活跃 Codex 会话",
            ForeColor = Color.FromArgb(15, 23, 42),
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
            Location = new Point(2, 3),
        };
        var hint = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Text = "托管窗口支持实时插话和下一轮排队；外部会话使用桥接队列。双击会话可打开目录",
            ForeColor = Muted,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            Location = new Point(3, 28),
            Size = new Size(850, 20),
        };

        ConfigureButton(aliasButton, "设置别名", Color.White, Primary, Border);
        aliasButton.Size = new Size(100, 34);
        aliasButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        aliasButton.Location = new Point(titlePanel.Width - aliasButton.Width - 2, 7);
        aliasButton.Enabled = false;
        aliasButton.Click += async (_, _) => await EditSelectedAliasAsync();

        titlePanel.Controls.Add(title);
        titlePanel.Controls.Add(hint);
        titlePanel.Controls.Add(aliasButton);
        titlePanel.Resize += (_, _) =>
        {
            aliasButton.Left = titlePanel.ClientSize.Width - aliasButton.Width - 2;
            hint.Width = Math.Max(160, aliasButton.Left - hint.Left - 12);
        };

        ConfigureSessionGrid();
        card.Controls.Add(sessionGrid);
        card.Controls.Add(titlePanel);
        return card;
    }

    private void ConfigureSessionGrid()
    {
        sessionGrid.Dock = DockStyle.Fill;
        sessionGrid.BackgroundColor = Color.White;
        sessionGrid.BorderStyle = BorderStyle.None;
        sessionGrid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        sessionGrid.GridColor = Color.FromArgb(241, 245, 249);
        sessionGrid.EnableHeadersVisualStyles = false;
        sessionGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        sessionGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(248, 250, 252),
            ForeColor = Color.FromArgb(71, 85, 105),
            Font = new Font("Microsoft YaHei UI", 8.8F, FontStyle.Bold),
            Padding = new Padding(6, 0, 6, 0),
            SelectionBackColor = Color.FromArgb(248, 250, 252),
            SelectionForeColor = Color.FromArgb(71, 85, 105),
        };
        sessionGrid.ColumnHeadersHeight = 40;
        sessionGrid.RowHeadersVisible = false;
        sessionGrid.RowTemplate.Height = 42;
        sessionGrid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.White,
            ForeColor = Color.FromArgb(30, 41, 59),
            Font = new Font("Microsoft YaHei UI", 9F),
            Padding = new Padding(6, 0, 6, 0),
            SelectionBackColor = Color.FromArgb(219, 234, 254),
            SelectionForeColor = Color.FromArgb(30, 64, 175),
        };
        sessionGrid.AllowUserToAddRows = false;
        sessionGrid.AllowUserToDeleteRows = false;
        sessionGrid.AllowUserToResizeRows = false;
        sessionGrid.ReadOnly = true;
        sessionGrid.MultiSelect = false;
        sessionGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        sessionGrid.AutoGenerateColumns = false;

        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Alias", HeaderText = "别名", Width = 120 });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Project", HeaderText = "项目", Width = 135 });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ShortId", HeaderText = "会话 ID", Width = 95 });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "状态", Width = 105 });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Queue", HeaderText = "排队", Width = 65 });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Model", HeaderText = "模型", Width = 125 });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "方式", Width = 105 });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Cwd",
            HeaderText = "工作目录",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 220,
        });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "OpenedAt", HeaderText = "打开时间", Width = 135 });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "LastSeen", HeaderText = "最近活动", Width = 140 });
        sessionGrid.SelectionChanged += (_, _) => UpdateAliasButtonState();
        sessionGrid.CellDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex < 0 ||
                sessionGrid.Rows[eventArgs.RowIndex].Tag is not CodexSession session ||
                !Directory.Exists(session.Cwd))
            {
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", session.Cwd) { UseShellExecute = true });
        };
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var note = new Label
        {
            AutoSize = true,
            Text = "关闭控制面板不会断开桥接，也不会关闭已启动的 Codex 窗口",
            ForeColor = Muted,
            Anchor = AnchorStyles.Left,
            Font = new Font("Microsoft YaHei UI", 8.5F),
        };
        lastRefreshLabel.AutoSize = true;
        lastRefreshLabel.Text = "尚未刷新";
        lastRefreshLabel.ForeColor = Muted;
        lastRefreshLabel.Anchor = AnchorStyles.Right;
        lastRefreshLabel.Font = new Font("Microsoft YaHei UI", 8.5F);
        footer.Controls.Add(note, 0, 0);
        footer.Controls.Add(lastRefreshLabel, 1, 0);
        return footer;
    }

    private async Task EditSelectedAliasAsync()
    {
        if (operating || sessionGrid.CurrentRow?.Tag is not CodexSession session)
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
            UpdateAliasButtonState();
        }
    }

    private async Task NewCodexAsync()
    {
        if (operating) return;
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
                throw new InvalidOperationException("请先连接飞书桥接服务，再新建 Codex 窗口。");
            }

            var initialDirectory = lastProjectDirectory ??
                Directory.GetParent(bridgeClient.BridgeRoot)?.FullName ??
                bridgeClient.BridgeRoot;
            using var dialog = new NewCodexDialog(initialDirectory);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            lastProjectDirectory = dialog.SelectedDirectory;
            bridgeClient.StartManagedTerminal(
                dialog.SelectedDirectory,
                dialog.RunAsAdministrator,
                dialog.CodexArguments);
            operationLabel.Text = dialog.RunAsAdministrator
                ? "已请求管理员启动；完成 UAC 确认后，Windows Terminal 窗口会自动登记"
                : "Windows Terminal / Codex 窗口已启动，正在等待会话登记";
        }
        catch (OperationCanceledException error) when (!lifetime.IsCancellationRequested)
        {
            operationLabel.Text = error.Message;
        }
        catch (Exception error)
        {
            ShowOperationError("新建 Codex 失败", error);
        }
    }

    private async Task ConnectAsync()
    {
        if (operating) return;
        SetOperating(true, "正在启动桥接服务…");
        try
        {
            await bridgeClient.StartAsync();
            BridgeStatus? status = null;
            for (var attempt = 0; attempt < 20 && status is null; attempt++)
            {
                await Task.Delay(400, lifetime.Token);
                status = await bridgeClient.GetStatusAsync(lifetime.Token);
            }
            if (status is null)
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
            await bridgeClient.StopAsync();
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
            var status = await bridgeClient.GetStatusAsync(lifetime.Token);
            if (status is null)
            {
                ApplyOfflineStatus();
            }
            else
            {
                ApplyStatus(status);
            }
            lastRefreshLabel.Text = $"最后刷新：{DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            operationLabel.Text = $"刷新失败：{error.Message}";
        }
        finally
        {
            refreshing = false;
        }
    }

    private void ApplyStatus(BridgeStatus status)
    {
        var feishuState = status.Feishu.State.ToLowerInvariant();
        var feishuConnected = feishuState == "connected";
        serviceValue.Text = "运行中";
        feishuValue.Text = FeishuStateLabel(feishuState);
        bindingsValue.Text = status.Bindings.ToString();
        sessionsValue.Text = status.ActiveSessions.ToString();

        if (feishuConnected)
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

        if (status.PendingApprovals > 0)
        {
            operationLabel.Text = $"有 {status.PendingApprovals} 个操作等待飞书审批";
        }
        else if (status.PendingInputs > 0)
        {
            operationLabel.Text = $"有 {status.PendingInputs} 组问题等待你在飞书补充";
        }
        else if (status.QueuedPrompts > 0)
        {
            operationLabel.Text = $"有 {status.QueuedPrompts} 条 Codex 消息正在排队";
        }
        else if (status.Bindings == 0 && !string.IsNullOrWhiteSpace(status.BindingCommand))
        {
            operationLabel.Text = $"首次绑定：请在飞书私聊机器人发送“{status.BindingCommand}”";
        }
        else if (status.Bindings == 0 && status.OwnerConfigured)
        {
            operationLabel.Text = "管理员当前已解绑，请由原管理员私聊机器人发送“绑定”恢复";
        }
        else
        {
            operationLabel.Text = $"桥接版本 {status.Version} · 服务运行正常";
        }
        connectButton.Enabled = !operating && !feishuConnected;
        disconnectButton.Enabled = !operating;
        newCodexButton.Enabled = !operating;
        refreshButton.Enabled = !operating;

        sessionGrid.Rows.Clear();
        foreach (var session in status.Sessions.OrderByDescending(item => ParseTime(item.LastSeenAt)))
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
            var mode = session.ManagedTerminal
                ? session.ManagedTerminalElevated ? "管理员同步" : "窗口同步"
                : SourceLabel(session.Source);
            var rowIndex = sessionGrid.Rows.Add(
                string.IsNullOrWhiteSpace(session.Alias) ? "—" : $"@{session.Alias}",
                session.ProjectName,
                $"#{session.ShortId}",
                displayedStatus,
                session.QueuedPrompts > 0 ? session.QueuedPrompts.ToString() : "—",
                session.Model,
                mode,
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
        }
        UpdateAliasButtonState();
    }

    private void ApplyOfflineStatus()
    {
        serviceValue.Text = "未运行";
        feishuValue.Text = "已断开";
        bindingsValue.Text = "—";
        sessionsValue.Text = "0";
        SetHeaderStatus("服务未运行", Danger);
        connectButton.Enabled = !operating;
        disconnectButton.Enabled = false;
        newCodexButton.Enabled = !operating;
        refreshButton.Enabled = !operating;
        sessionGrid.Rows.Clear();
        UpdateAliasButtonState();
        if (!operating)
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

    private void SetOperating(bool value, string? message = null)
    {
        operating = value;
        connectButton.Enabled = !value;
        disconnectButton.Enabled = !value;
        newCodexButton.Enabled = !value;
        aliasButton.Enabled = !value && sessionGrid.CurrentRow?.Tag is CodexSession;
        refreshButton.Enabled = !value;
        folderButton.Enabled = !value;
        if (!string.IsNullOrWhiteSpace(message))
        {
            operationLabel.Text = message;
        }
    }

    private void UpdateAliasButtonState()
    {
        aliasButton.Enabled = !operating && sessionGrid.CurrentRow?.Tag is CodexSession;
    }

    private static void ConfigureButton(
        Button button,
        string text,
        Color background,
        Color foreground,
        Color? border = null)
    {
        button.Text = text;
        button.AutoSize = false;
        button.Size = new Size(94, 38);
        button.Margin = new Padding(8, 0, 0, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = border ?? background;
        button.BackColor = background;
        button.ForeColor = foreground;
        button.Cursor = Cursors.Hand;
        button.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
    }

    private static string FeishuStateLabel(string state) => state switch
    {
        "connected" => "已连接",
        "connecting" => "连接中",
        "reconnecting" => "重连中",
        "failed" => "连接失败",
        _ => "未连接",
    };

    private static string SourceLabel(string source) => source switch
    {
        "startup" => "外部会话",
        "resume" => "外部会话",
        "clear" => "外部会话",
        "compact" => "外部会话",
        _ => source,
    };

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.TryParse(value, out var time) ? time : DateTimeOffset.MinValue;

    private static string FormatTime(string value) =>
        DateTimeOffset.TryParse(value, out var time)
            ? time.ToLocalTime().ToString("MM-dd HH:mm:ss")
            : "—";

    private void ShowOperationError(string title, Exception error)
    {
        operationLabel.Text = $"{title}：{error.Message}";
        MessageBox.Show(
            this,
            error.Message,
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static Region RoundedRegion(Rectangle rectangle, int radius)
    {
        using var path = RoundedPath(rectangle, radius);
        return new Region(path);
    }

    private static GraphicsPath RoundedPath(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        var arc = new Rectangle(rectangle.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = rectangle.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rectangle.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rectangle.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class CardPanel : Panel
    {
        public int CornerRadius { get; set; } = 12;
        public Color BorderColor { get; set; } = Color.LightGray;

        public CardPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundedPath(bounds, CornerRadius);
            using var pen = new Pen(BorderColor, 1F);
            eventArgs.Graphics.DrawPath(pen, path);
        }

        protected override void OnResize(EventArgs eventArgs)
        {
            base.OnResize(eventArgs);
            if (Width > 0 && Height > 0)
            {
                Region?.Dispose();
                Region = RoundedRegion(ClientRectangle, CornerRadius);
            }
        }
    }
}
