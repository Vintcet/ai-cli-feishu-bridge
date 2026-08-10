using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AiCliFeishuControl;

internal sealed partial class MainForm
{
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
            Text = "AI CLI 飞书助手",
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold),
            Location = new Point(26, 16),
        };
        var subtitle = new Label
        {
            AutoSize = true,
            Text = "管理飞书连接，并同步 Codex、Claude Code 与 opencode 会话",
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

        operationLabel.Text = "桥接状态会每 15 秒自动刷新";
        operationLabel.AutoSize = false;
        operationLabel.AutoEllipsis = true;
        operationLabel.Dock = DockStyle.Fill;
        operationLabel.ForeColor = Muted;
        operationLabel.TextAlign = ContentAlignment.MiddleLeft;
        operationLabel.Margin = new Padding(0, 0, 12, 0);
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
        ConfigureButton(newClaudeCodeButton, "新建 Claude", Color.FromArgb(217, 119, 87), Color.White);
        newClaudeCodeButton.Size = new Size(118, 38);
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
        newCodexButton.Click += async (_, _) => await NewRuntimeAsync(RuntimeCatalog.Codex);
        newClaudeCodeButton.Click += async (_, _) => await NewRuntimeAsync(RuntimeCatalog.ClaudeCode);
        newOpenCodeButton.Click += async (_, _) => await NewRuntimeAsync(RuntimeCatalog.OpenCode);
        approvalButton.Click += (_, _) => OpenPendingApproval();
        refreshButton.Click += async (_, _) => await RefreshStatusAsync(force: true);
        settingsButton.Click += async (_, _) => await EditSettingsAsync();

        buttons.Controls.Add(connectionButton);
        buttons.Controls.Add(newCodexButton);
        buttons.Controls.Add(newClaudeCodeButton);
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
            Text = "助手会话",
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
        void LayoutSessionActions()
        {
            folderButton.Left = titlePanel.ClientSize.Width - folderButton.Width - 2;
            if (sessionTabs.SelectedIndex == 1)
            {
                deleteHistoryButton.Left =
                    folderButton.Left - deleteHistoryButton.Width - 8;
                resumeSessionButton.Left =
                    deleteHistoryButton.Left - resumeSessionButton.Width - 8;
                aliasButton.Left = resumeSessionButton.Left - aliasButton.Width - 8;
            }
            else
            {
                aliasButton.Left = folderButton.Left - aliasButton.Width - 8;
                retryGroupButton.Left = aliasButton.Left - retryGroupButton.Width - 8;
            }
            hint.Width = Math.Max(
                160,
                (sessionTabs.SelectedIndex == 1
                    ? aliasButton.Left
                    : retryGroupButton.Left) - hint.Left - 12);
        }
        titlePanel.Resize += (_, _) => LayoutSessionActions();

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
        sessionTabs.SelectedIndexChanged += (_, _) =>
        {
            LayoutSessionActions();
            UpdateSessionActionState();
        };
        LayoutSessionActions();
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
        sessionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Source", HeaderText = "方式", Width = 118 });
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
        grid.ShowCellToolTips = false;
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
            Text = "关闭控制面板不会断开桥接，也不会关闭已启动的助手窗口",
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

}
