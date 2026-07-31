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
    private static readonly Color OpenCodeAccent = Color.FromArgb(234, 88, 12);
    private static readonly Color Muted = Color.FromArgb(100, 116, 139);
    private static readonly Color Border = Color.FromArgb(226, 232, 240);

    private readonly BridgeClient bridgeClient = new();
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 2_000 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly Font gridBoldFont = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    private readonly EventWaitHandle activateEvent;
    private readonly RegisteredWaitHandle activationRegistration;
    private readonly System.Windows.Forms.Timer activationTimer = new() { Interval = 200 };
    private readonly NotifyIcon trayIcon = new();
    private readonly ContextMenuStrip trayMenu = new();

    private readonly Label headerStatusLabel = new();
    private readonly Panel headerStatusDot = new();
    private readonly Label serviceValue = new();
    private readonly Label feishuValue = new();
    private readonly Label bindingsValue = new();
    private readonly Label sessionsValue = new();
    private readonly Label operationLabel = new();
    private readonly Label lastRefreshLabel = new();
    private readonly DataGridView sessionGrid = new();
    private readonly DataGridView historyGrid = new();
    private readonly TabControl sessionTabs = new();
    private readonly Button connectionButton = new();
    private readonly Button newCodexButton = new();
    private readonly Button newOpenCodeButton = new();
    private readonly Button approvalButton = new();
    private readonly Button aliasButton = new();
    private readonly Button retryGroupButton = new();
    private readonly Button resumeSessionButton = new();
    private readonly Button deleteHistoryButton = new();
    private readonly Button settingsButton = new();
    private readonly Button refreshButton = new();
    private readonly Button folderButton = new();

    private bool refreshing;
    private bool operating;
    private bool closing;
    private bool exitRequested;
    private bool trayHintShown;
    private int activationRequested;
    private string? lastProjectDirectory;
    private BridgeStatus? lastStatus;
    private ApprovalDialog? approvalDialog;
    private readonly HashSet<string> dismissedApprovalIds = [];

    public MainForm(EventWaitHandle activateEvent)
    {
        this.activateEvent = activateEvent;
        try
        {
            AppLog.Initialize(Path.Combine(bridgeClient.BridgeRoot, "data"));
            AppLog.Info(
                $"面板启动 bridgeRoot={bridgeClient.BridgeRoot} " +
                $"port={bridgeClient.Port} " +
                $"controlToken存在={File.Exists(Path.Combine(bridgeClient.BridgeRoot, "data", "control.token"))}");
        }
        catch (Exception error)
        {
            AppLog.Error("面板启动日志初始化异常", error);
        }
        Text = "Codex 飞书助手";
        Icon = LoadApplicationIcon();
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 760);
        MinimumSize = new Size(1040, 640);
        BackColor = PageBackground;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildLayout();
        ConfigureTrayIcon();
        activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            activateEvent,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    Interlocked.Exchange(ref activationRequested, 1);
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        activationTimer.Tick += (_, _) =>
        {
            if (Interlocked.Exchange(ref activationRequested, 0) == 1)
            {
                RestoreFromTray();
            }
        };
        activationTimer.Start();
        refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        Shown += async (_, _) =>
        {
            RestoreFromTray();
            AppLog.Info("面板已显示，开始自动刷新。");
            await RefreshStatusAsync();
            refreshTimer.Start();
        };
        FormClosing += (_, eventArgs) =>
        {
            if (!exitRequested && eventArgs.CloseReason == CloseReason.UserClosing)
            {
                eventArgs.Cancel = true;
                HideToTray(showHint: true);
            }
        };
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                HideToTray(showHint: false);
            }
        };
        FormClosed += (_, _) =>
        {
            closing = true;
            approvalDialog?.MarkResolved();
            refreshTimer.Stop();
            lifetime.Cancel();
            bridgeClient.Dispose();
            lifetime.Dispose();
            gridBoldFont.Dispose();
            activationRegistration.Unregister(null);
            activationTimer.Stop();
            activationTimer.Dispose();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            trayMenu.Dispose();
        };
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath)
                ?? (Icon)SystemIcons.Application.Clone();
        }
        catch (Exception)
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    private void ConfigureTrayIcon()
    {
        var openItem = new ToolStripMenuItem("打开 Codex 飞书助手");
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);
        openItem.Click += (_, _) => RestoreFromTray();
        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitFromTray();
        trayMenu.Items.Add(openItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(exitItem);

        trayIcon.Icon = Icon ?? SystemIcons.Application;
        trayIcon.Text = "Codex 飞书助手";
        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.Visible = true;
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void HideToTray(bool showHint)
    {
        ShowInTaskbar = false;
        Hide();
        if (showHint && !trayHintShown)
        {
            trayHintShown = true;
            trayIcon.ShowBalloonTip(
                2500,
                "Codex 飞书助手仍在运行",
                "双击托盘图标可重新打开；右键选择“退出”才会完全关闭。",
                ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        if (closing || IsDisposed)
        {
            return;
        }

        var currentBoundsAreVisible = IsOnScreen(Bounds);
        var restoreBounds = currentBoundsAreVisible
            ? Bounds
            : VisibleRestoreBounds();

        // Windows may leave a hidden minimized form at (-32000, -32000).
        // Restore its state and bounds before making it interactive again.
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        if (!currentBoundsAreVisible)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = restoreBounds;
        }
        ShowInTaskbar = true;
        Show();
        if (!IsOnScreen(Bounds))
        {
            Bounds = restoreBounds;
        }
        BringToFront();
        Activate();
    }

    private Rectangle VisibleRestoreBounds()
    {
        if (IsOnScreen(RestoreBounds))
        {
            return RestoreBounds;
        }

        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var width = Math.Min(Math.Max(MinimumSize.Width, Width), area.Width);
        var height = Math.Min(Math.Max(MinimumSize.Height, Height), area.Height);
        return new Rectangle(
            area.Left + Math.Max(0, (area.Width - width) / 2),
            area.Top + Math.Max(0, (area.Height - height) / 2),
            width,
            height);
    }

    private static bool IsOnScreen(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        return Screen.AllScreens.Any(screen =>
        {
            var visible = Rectangle.Intersect(screen.WorkingArea, bounds);
            return visible.Width >= Math.Min(160, bounds.Width) &&
                visible.Height >= Math.Min(80, bounds.Height);
        });
    }

    private void ExitFromTray()
    {
        exitRequested = true;
        Close();
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

        ConfigureButton(connectionButton, "连接", Primary, Color.White);
        ConfigureButton(newCodexButton, "新建 Codex", Success, Color.White);
        newCodexButton.Size = new Size(112, 38);
        ConfigureButton(newOpenCodeButton, "新建 opencode", OpenCodeAccent, Color.White);
        newOpenCodeButton.Size = new Size(124, 38);
        ConfigureButton(approvalButton, "本机审批", Color.White, Warning, Warning);
        approvalButton.Size = new Size(110, 38);
        approvalButton.Enabled = false;
        ConfigureButton(refreshButton, "刷新", Color.White, Color.FromArgb(51, 65, 85), Border);
        ConfigureButton(settingsButton, "设置", Color.White, Color.FromArgb(51, 65, 85), Border);

        connectionButton.Click += async (_, _) =>
        {
            if (lastStatus is null)
            {
                await ConnectAsync();
            }
            else
            {
                await DisconnectAsync();
            }
        };
        newCodexButton.Click += async (_, _) => await NewCodexAsync();
        newOpenCodeButton.Click += async (_, _) => await NewOpenCodeAsync();
        approvalButton.Click += (_, _) => OpenPendingApproval();
        refreshButton.Click += async (_, _) => await RefreshStatusAsync(force: true);
        settingsButton.Click += async (_, _) => await EditSettingsAsync();

        buttons.Controls.Add(connectionButton);
        buttons.Controls.Add(newCodexButton);
        buttons.Controls.Add(newOpenCodeButton);
        buttons.Controls.Add(approvalButton);
        buttons.Controls.Add(refreshButton);
        buttons.Controls.Add(settingsButton);
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
        metrics.Controls.Add(CreateMetricCard("活跃会话", sessionsValue, Warning), 3, 0);
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
            Text = "Codex 会话",
            ForeColor = Color.FromArgb(15, 23, 42),
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
            Location = new Point(2, 3),
        };
        var hint = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Text = "活跃会话支持同步与排队；历史记录可直接继续之前的助手会话",
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

        ConfigureButton(folderButton, "打开目录", Color.White, Color.FromArgb(51, 65, 85), Border);
        folderButton.Size = new Size(100, 34);
        folderButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        folderButton.Location = new Point(titlePanel.Width - folderButton.Width - 2, 7);
        folderButton.Enabled = false;
        folderButton.Click += (_, _) => OpenSelectedSessionDirectory();

        aliasButton.Location = new Point(
            folderButton.Left - aliasButton.Width - 8,
            folderButton.Top);

        ConfigureButton(retryGroupButton, "创建飞书群", Color.White, Primary, Border);
        retryGroupButton.Size = new Size(112, 34);
        retryGroupButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        retryGroupButton.Location = new Point(
            aliasButton.Left - retryGroupButton.Width - 8,
            aliasButton.Top);
        retryGroupButton.Enabled = false;
        retryGroupButton.Click += async (_, _) => await RetrySelectedSessionGroupAsync();

        ConfigureButton(resumeSessionButton, "继续对话", Success, Color.White);
        resumeSessionButton.Size = new Size(100, 34);
        resumeSessionButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        resumeSessionButton.Location = aliasButton.Location;
        resumeSessionButton.Enabled = false;
        resumeSessionButton.Visible = false;
        resumeSessionButton.Click += (_, _) => ContinueSelectedHistory();

        ConfigureButton(deleteHistoryButton, "删除记录", Color.White, Danger, Border);
        deleteHistoryButton.Size = new Size(100, 34);
        deleteHistoryButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        deleteHistoryButton.Location = resumeSessionButton.Location;
        deleteHistoryButton.Enabled = false;
        deleteHistoryButton.Visible = false;
        deleteHistoryButton.Click += async (_, _) => await DeleteSelectedHistoryAsync();

        titlePanel.Controls.Add(title);
        titlePanel.Controls.Add(hint);
        titlePanel.Controls.Add(folderButton);
        titlePanel.Controls.Add(aliasButton);
        titlePanel.Controls.Add(retryGroupButton);
        titlePanel.Controls.Add(resumeSessionButton);
        titlePanel.Controls.Add(deleteHistoryButton);
        titlePanel.Resize += (_, _) =>
        {
            folderButton.Left = titlePanel.ClientSize.Width - folderButton.Width - 2;
            aliasButton.Left = folderButton.Left - aliasButton.Width - 8;
            retryGroupButton.Left = aliasButton.Left - retryGroupButton.Width - 8;
            deleteHistoryButton.Left =
                folderButton.Left - deleteHistoryButton.Width - 8;
            resumeSessionButton.Left =
                deleteHistoryButton.Left - resumeSessionButton.Width - 8;
            hint.Width = Math.Max(
                160,
                Math.Min(retryGroupButton.Left, resumeSessionButton.Left) - hint.Left - 12);
        };

        ConfigureSessionGrid();
        ConfigureHistoryGrid();
        var activePage = new TabPage("活跃会话")
        {
            BackColor = Color.White,
            Padding = Padding.Empty,
        };
        var historyPage = new TabPage("历史记录")
        {
            BackColor = Color.White,
            Padding = Padding.Empty,
        };
        activePage.Controls.Add(sessionGrid);
        historyPage.Controls.Add(historyGrid);
        sessionTabs.Dock = DockStyle.Fill;
        sessionTabs.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        sessionTabs.Padding = new Point(18, 6);
        sessionTabs.Controls.Add(activePage);
        sessionTabs.Controls.Add(historyPage);
        sessionTabs.SelectedIndexChanged += (_, _) => UpdateSessionActionState();
        card.Controls.Add(sessionTabs);
        card.Controls.Add(titlePanel);
        return card;
    }

    private void ConfigureSessionGrid()
    {
        ConfigureGrid(sessionGrid);
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Alias", HeaderText = "别名", Width = 120 });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Project", HeaderText = "项目", Width = 135 });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ShortId", HeaderText = "会话 ID", Width = 95 });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "状态", Width = 105 });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Queue", HeaderText = "排队", Width = 65 });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Model", HeaderText = "模型", Width = 125 });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "方式", Width = 105 });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FeishuGroup", HeaderText = "飞书群", Width = 135 });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Cwd",
            HeaderText = "工作目录",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 220,
        });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "OpenedAt", HeaderText = "打开时间", Width = 135 });
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "LastSeen", HeaderText = "最近活动", Width = 140 });
        sessionGrid.SelectionChanged += (_, _) => UpdateSessionActionState();
        sessionGrid.CellDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex >= 0)
            {
                sessionGrid.CurrentCell = sessionGrid.Rows[eventArgs.RowIndex].Cells[0];
                OpenSelectedSessionDirectory();
            }
        };
    }

    private void ConfigureHistoryGrid()
    {
        ConfigureGrid(historyGrid);
        historyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Alias", HeaderText = "别名", Width = 120 });
        historyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Project", HeaderText = "项目", Width = 145 });
        historyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ShortId", HeaderText = "会话 ID", Width = 95 });
        historyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Model", HeaderText = "模型", Width = 125 });
        historyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Mode", HeaderText = "启动方式", Width = 90 });
        historyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FeishuGroup", HeaderText = "飞书群", Width = 135 });
        historyGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Cwd",
            HeaderText = "工作目录",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 220,
        });
        historyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "OpenedAt", HeaderText = "打开时间", Width = 135 });
        historyGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ClosedAt", HeaderText = "最后活动", Width = 140 });
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

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Color.FromArgb(241, 245, 249);
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(248, 250, 252),
            ForeColor = Color.FromArgb(71, 85, 105),
            Font = new Font("Microsoft YaHei UI", 8.8F, FontStyle.Bold),
            Padding = new Padding(6, 0, 6, 0),
            SelectionBackColor = Color.FromArgb(248, 250, 252),
            SelectionForeColor = Color.FromArgb(71, 85, 105),
        };
        grid.ColumnHeadersHeight = 40;
        grid.RowHeadersVisible = false;
        grid.RowTemplate.Height = 42;
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.White,
            ForeColor = Color.FromArgb(30, 41, 59),
            Font = new Font("Microsoft YaHei UI", 9F),
            Padding = new Padding(6, 0, 6, 0),
            SelectionBackColor = Color.FromArgb(219, 234, 254),
            SelectionForeColor = Color.FromArgb(30, 64, 175),
        };
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.ReadOnly = true;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoGenerateColumns = false;
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
            ? historyGrid.CurrentRow?.Tag as CodexSession
            : sessionGrid.CurrentRow?.Tag as CodexSession;
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
        if (operating || sessionGrid.CurrentRow?.Tag is not CodexSession session)
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

    private void ContinueSelectedHistory()
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
            bridgeClient.StartManagedTerminal(
                session.Cwd,
                session.ManagedTerminalElevated,
                $"resume {session.SessionId}");
            operationLabel.Text = session.ManagedTerminalElevated
                ? $"已请求以管理员身份继续 {SessionDisplayName(session)}；请完成 UAC 确认"
                : $"正在新窗口继续 {SessionDisplayName(session)}";
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
            "这不会删除 Codex 原始对话或项目文件，之后仍可使用完整会话 ID 手动恢复。",
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

    private async Task NewOpenCodeAsync()
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
                throw new InvalidOperationException("请先连接飞书桥接服务，再新建 opencode 窗口。");
            }

            var initialDirectory = lastProjectDirectory ??
                Directory.GetParent(bridgeClient.BridgeRoot)?.FullName ??
                bridgeClient.BridgeRoot;
            using var dialog = new NewOpenCodeDialog(initialDirectory);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            lastProjectDirectory = dialog.SelectedDirectory;
            await bridgeClient.LaunchOpenCodeAsync(
                dialog.SelectedDirectory,
                dialog.RunAsAdministrator,
                dialog.OpenCodeArguments,
                lifetime.Token);
            operationLabel.Text = dialog.RunAsAdministrator
                ? "已请求管理员启动；完成 UAC 确认后，Windows Terminal 窗口会自动登记 opencode"
                : "Windows Terminal / opencode 窗口已启动，正在等待会话登记";
        }
        catch (OperationCanceledException error) when (!lifetime.IsCancellationRequested)
        {
            operationLabel.Text = error.Message;
        }
        catch (Exception error)
        {
            ShowOperationError("新建 opencode 失败", error);
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
            AppLog.Error("刷新状态异常", error);
            operationLabel.Text = $"刷新失败：{error.Message}";
        }
        finally
        {
            refreshing = false;
        }
    }

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
            (sessionGrid.CurrentRow?.Tag as CodexSession)?.SessionId;
        var selectedHistorySessionId =
            (historyGrid.CurrentRow?.Tag as CodexSession)?.SessionId;
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

        if (status.PendingApprovals > 0 && !status.Settings.AutoApprove)
        {
            operationLabel.Text =
                $"有 {status.PendingApprovals} 个操作等待审批，可在本机或飞书处理";
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
        connectionButton.Text = "断开";
        connectionButton.BackColor = Color.White;
        connectionButton.ForeColor = Danger;
        connectionButton.FlatAppearance.BorderColor = Border;
        connectionButton.Enabled = !operating;
        newCodexButton.Enabled = !operating;
        newOpenCodeButton.Enabled = !operating;
        approvalButton.Enabled =
            !operating && !status.Settings.AutoApprove && status.PendingApprovals > 0;
        refreshButton.Enabled = !operating;
        settingsButton.Enabled = !operating;

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
                row.Cells["FeishuGroup"].ToolTipText = session.FeishuChatError;
            }
            else if (session.FeishuChatStatus == "pending")
            {
                row.Cells["FeishuGroup"].Style.ForeColor = Warning;
                row.Cells["FeishuGroup"].ToolTipText = "等待飞书建群权限或正在创建会话群。";
            }
        }
        RestoreGridState(sessionGrid, selectedSessionId, sessionScrollIndex);
        historyGrid.Rows.Clear();
        foreach (var session in status.HistorySessions.OrderByDescending(
                     item => ParseTime(string.IsNullOrWhiteSpace(item.EndedAt)
                         ? item.LastSeenAt
                         : item.EndedAt)))
        {
            var rowIndex = historyGrid.Rows.Add(
                string.IsNullOrWhiteSpace(session.Alias) ? "—" : $"@{session.Alias}",
                session.ProjectName,
                $"#{session.ShortId}",
                string.IsNullOrWhiteSpace(session.Model) ? "—" : session.Model,
                session.ManagedTerminalElevated ? "管理员" : "普通",
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
        RestoreGridState(historyGrid, selectedHistorySessionId, historyScrollIndex);
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
        newCodexButton.Enabled = !operating;
        newOpenCodeButton.Enabled = !operating;
        approvalButton.Text = "本机审批";
        approvalButton.Enabled = false;
        refreshButton.Enabled = !operating;
        settingsButton.Enabled = !operating;
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
        connectionButton.Enabled = !value;
        newCodexButton.Enabled = !value;
        newOpenCodeButton.Enabled = !value;
        approvalButton.Enabled =
            !value &&
            lastStatus?.Settings.AutoApprove != true &&
            (lastStatus?.Approvals.Count(item => item.Status == "pending") ?? 0) > 0;
        UpdateSessionActionState();
        refreshButton.Enabled = !value;
        settingsButton.Enabled = !value;
        folderButton.Enabled = !value;
        if (!string.IsNullOrWhiteSpace(message))
        {
            operationLabel.Text = message;
        }
    }

    private void UpdateSessionActionState()
    {
        var historySelected = sessionTabs.SelectedIndex == 1;
        aliasButton.Visible = !historySelected;
        retryGroupButton.Visible = !historySelected;
        folderButton.Visible = true;
        resumeSessionButton.Visible = historySelected;
        deleteHistoryButton.Visible = historySelected;
        aliasButton.Enabled =
            !operating && !historySelected && sessionGrid.CurrentRow?.Tag is CodexSession;
        retryGroupButton.Enabled =
            !operating &&
            !historySelected &&
            sessionGrid.CurrentRow?.Tag is CodexSession session &&
            session.ManagedByAssistant &&
            session.FeishuChatStatus != "connected";
        resumeSessionButton.Enabled =
            !operating &&
            historySelected &&
            historyGrid.CurrentRow?.Tag is CodexSession;
        deleteHistoryButton.Enabled = resumeSessionButton.Enabled;
        folderButton.Enabled = !operating &&
            (historySelected
                ? historyGrid.CurrentRow?.Tag is CodexSession
                : sessionGrid.CurrentRow?.Tag is CodexSession);
    }

    private static string FeishuGroupLabel(CodexSession session) =>
        session.FeishuChatStatus switch
        {
            "connected" when !string.IsNullOrWhiteSpace(session.FeishuChatName) =>
                session.FeishuChatName,
            "connected" => "已连接",
            "pending" => "待创建",
            "error" => "创建失败",
            _ => "—",
        };

    private void SyncApprovalDialog(BridgeStatus status)
    {
        if (status.Settings.AutoApprove)
        {
            approvalButton.Text = "本机审批";
            approvalButton.Enabled = false;
            dismissedApprovalIds.Clear();
            if (approvalDialog is not null)
            {
                var dialog = approvalDialog;
                approvalDialog = null;
                dialog.MarkResolved();
            }
            return;
        }
        var pending = status.Approvals
            .Where(item => item.Status == "pending")
            .OrderBy(item => ParseTime(item.CreatedAt))
            .ToList();
        approvalButton.Text = pending.Count > 0
            ? $"本机审批 ({pending.Count})"
            : "本机审批";
        approvalButton.Enabled = !operating && pending.Count > 0;

        dismissedApprovalIds.RemoveWhere(
            requestId => pending.All(item => item.RequestId != requestId));

        if (approvalDialog is not null)
        {
            var current = status.Approvals.FirstOrDefault(
                item => item.RequestId == approvalDialog.RequestId);
            if (current is null || current.Status != "pending")
            {
                operationLabel.Text = ApprovalResolutionMessage(current?.Resolution);
                var dialog = approvalDialog;
                approvalDialog = null;
                dialog.MarkResolved();
            }
        }

        if (approvalDialog is null)
        {
            var next = pending.FirstOrDefault(
                item => !dismissedApprovalIds.Contains(item.RequestId));
            if (next is not null)
            {
                ShowApprovalDialog(next);
            }
        }
    }

    private void OpenPendingApproval()
    {
        if (approvalDialog is not null)
        {
            approvalDialog.Show();
            approvalDialog.BringToFront();
            approvalDialog.Activate();
            return;
        }
        if (lastStatus is null)
        {
            return;
        }
        dismissedApprovalIds.Clear();
        SyncApprovalDialog(lastStatus);
    }

    private void ShowApprovalDialog(BridgeApproval approval)
    {
        if (closing || approvalDialog is not null)
        {
            return;
        }

        var dialog = new ApprovalDialog(
            approval,
            resolution => ResolveApprovalAsync(approval, resolution));
        dialog.Dismissed += (_, _) => dismissedApprovalIds.Add(approval.RequestId);
        dialog.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(approvalDialog, dialog))
            {
                approvalDialog = null;
            }
            if (!closing && !IsDisposed)
            {
                BeginInvoke(() =>
                {
                    if (lastStatus is not null)
                    {
                        SyncApprovalDialog(lastStatus);
                    }
                });
            }
            dialog.Dispose();
        };
        approvalDialog = dialog;
        RestoreFromTray();
        dialog.Show(this);
        dialog.BringToFront();
        dialog.Activate();
    }

    private async Task<ApprovalResolveResult> ResolveApprovalAsync(
        BridgeApproval approval,
        string resolution)
    {
        var result = await bridgeClient.ResolveApprovalAsync(
            approval.RequestId,
            resolution,
            lifetime.Token);
        if (lastStatus is not null)
        {
            var current = lastStatus.Approvals.FirstOrDefault(
                item => item.RequestId == approval.RequestId);
            if (current is not null)
            {
                current.Status = "resolved";
                current.Resolution = result.Resolution;
                current.ResolvedAt = DateTime.UtcNow.ToString("O");
                lastStatus.PendingApprovals = Math.Max(0, lastStatus.PendingApprovals - 1);
            }
        }
        operationLabel.Text = result.AlreadyResolved
            ? ApprovalResolutionMessage(result.Resolution)
            : resolution switch
            {
                "allow" => $"已在本机批准 {approval.SessionLabel} 的操作",
                "deny" => $"已在本机拒绝 {approval.SessionLabel} 的操作",
                _ => "审批已处理",
            };
        return result;
    }

    private static string ApprovalResolutionMessage(string? resolution) => resolution switch
    {
        "allow" => "审批已在飞书批准，Codex 正在继续执行",
        "deny" => "审批已在飞书拒绝，Codex 已停止这次操作",
        "local" => "桥接服务曾中断，这条审批已交还 Codex 原窗口",
        "timeout" => "审批等待超时，已交还 Codex 原窗口",
        _ => "这条审批已在另一端处理",
    };

    private static string SessionDisplayName(CodexSession session) =>
        string.IsNullOrWhiteSpace(session.Alias)
            ? $"{session.ProjectName} #{session.ShortId}"
            : $"@{session.Alias}";

    private static void RestoreGridState(
        DataGridView grid,
        string? selectedSessionId,
        int firstDisplayedRowIndex)
    {
        DataGridViewRow? selectedRow = null;
        if (!string.IsNullOrWhiteSpace(selectedSessionId))
        {
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.Tag is CodexSession session &&
                    session.SessionId == selectedSessionId)
                {
                    selectedRow = row;
                    break;
                }
            }
            if (selectedRow is null)
            {
                return;
            }
        }

        if (firstDisplayedRowIndex >= 0 && firstDisplayedRowIndex < grid.Rows.Count)
        {
            grid.FirstDisplayedScrollingRowIndex = firstDisplayedRowIndex;
        }
        if (selectedRow is null)
        {
            return;
        }

        grid.ClearSelection();
        grid.CurrentCell = selectedRow.Cells[0];
        selectedRow.Selected = true;
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
        "startup" => "外部·仅通知",
        "resume" => "外部·仅通知",
        "clear" => "外部·仅通知",
        "compact" => "外部·仅通知",
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
