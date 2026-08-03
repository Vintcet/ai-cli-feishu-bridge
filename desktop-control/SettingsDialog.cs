namespace CodexFeishuControl;

internal sealed class SettingsDialog : Form
{
    private readonly CheckBox notifyActivityBox = new();
    private readonly CheckBox notifyUserPromptsBox = new();
    private readonly CheckBox autoRetryBox = new();
    private readonly CheckBox autoApproveBox = new();
    private readonly CheckBox notifyAutoApprovalsBox = new();
    private readonly GroupBox retryOptionsGroup = new();
    private readonly NumericUpDown retryMaxAttemptsInput = new();
    private readonly NumericUpDown retryIntervalInput = new();
    private readonly NumericUpDown retryJitterInput = new();
    private readonly TextBox workspaceRootBox = new();
    private readonly bool initiallyAutoApprove;

    public SettingsDialog(BridgeSettings settings)
    {
        initiallyAutoApprove = settings.AutoApprove;
        Text = "助手设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(620, 714);
        Font = new Font("Microsoft YaHei UI", 9F);

        var title = new Label
        {
            Text = "飞书通知与自动处理",
            Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 20),
        };
        var hint = new Label
        {
            Text = "完成、补充信息和错误始终通知；以下开关控制额外行为。",
            ForeColor = Color.FromArgb(100, 116, 139),
            AutoSize = true,
            Location = new Point(25, 52),
        };

        var workspaceLabel = new Label
        {
            Text = "默认工作区",
            AutoSize = true,
            Location = new Point(26, 84),
        };
        workspaceRootBox.Text = settings.WorkspaceRoot;
        workspaceRootBox.Location = new Point(26, 106);
        workspaceRootBox.Size = new Size(468, 28);
        workspaceRootBox.MaxLength = 1_024;
        var workspaceBrowseButton = new Button
        {
            Text = "浏览…",
            Location = new Point(504, 104),
            Size = new Size(90, 32),
        };
        workspaceBrowseButton.Click += (_, _) => BrowseWorkspaceRoot();
        var workspaceHint = new Label
        {
            Text = "飞书发送“新建 codex 项目名”时，会在这里查找或创建对应项目文件夹。",
            ForeColor = Color.FromArgb(100, 116, 139),
            AutoSize = true,
            Location = new Point(27, 139),
        };

        ConfigureCheckBox(
            notifyActivityBox,
            "同步普通过程信息",
            "在飞书持续更新工具执行和处理进度。关闭时只推送关键节点。",
            165,
            settings.NotifyActivity);
        ConfigureCheckBox(
            notifyUserPromptsBox,
            "同步电脑端输入到飞书",
            "把你在 PC 助手窗口提交的消息同步到对应飞书会话；飞书发来的消息不会重复回显。",
            227,
            settings.NotifyUserPrompts);
        ConfigureCheckBox(
            autoRetryBox,
            "临时错误自动重试",
            "遇到 429、503、服务繁忙或超时等错误时，按下方参数重试。",
            289,
            settings.AutoRetryErrors);
        ConfigureRetryOptions(settings);
        ConfigureCheckBox(
            autoApproveBox,
            "审批请求自动允许",
            "高风险：助手请求权限时不再等待人工确认；默认不发送审批提醒。",
            483,
            settings.AutoApprove);
        ConfigureCheckBox(
            notifyAutoApprovalsBox,
            "自动审批后发送处理留痕",
            "只发送已处理信息卡，不发送带按钮的待审批卡。",
            545,
            settings.NotifyAutoApprovals);
        autoRetryBox.CheckedChanged += (_, _) =>
            retryOptionsGroup.Enabled = autoRetryBox.Checked;
        retryOptionsGroup.Enabled = autoRetryBox.Checked;
        autoApproveBox.CheckedChanged += (_, _) =>
            notifyAutoApprovalsBox.Enabled = autoApproveBox.Checked;
        notifyAutoApprovalsBox.Enabled = autoApproveBox.Checked;

        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Size = new Size(90, 36),
            Location = new Point(406, 650),
        };
        var saveButton = new Button
        {
            Text = "保存",
            DialogResult = DialogResult.OK,
            Size = new Size(90, 36),
            Location = new Point(506, 650),
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        saveButton.FlatAppearance.BorderSize = 0;

        Controls.AddRange([
            title, hint, workspaceLabel, workspaceRootBox, workspaceBrowseButton,
            workspaceHint, notifyActivityBox, notifyUserPromptsBox, autoRetryBox,
            retryOptionsGroup, autoApproveBox, notifyAutoApprovalsBox,
            cancelButton, saveButton,
        ]);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        if (DialogResult == DialogResult.OK &&
            !Directory.Exists(workspaceRootBox.Text.Trim()))
        {
            MessageBox.Show(
                this,
                "请选择一个存在的默认工作区目录。",
                "默认工作区不可用",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            eventArgs.Cancel = true;
            DialogResult = DialogResult.None;
            return;
        }
        if (DialogResult == DialogResult.OK &&
            autoApproveBox.Checked &&
            !initiallyAutoApprove)
        {
            var confirmation = MessageBox.Show(
                this,
                "开启后，助手的权限请求将直接允许，不再等待你确认。确定开启吗？",
                "确认开启自动审批",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                eventArgs.Cancel = true;
                DialogResult = DialogResult.None;
                return;
            }
        }
        base.OnFormClosing(eventArgs);
    }

    public BridgeSettings Settings => new()
    {
        WorkspaceRoot = Path.GetFullPath(workspaceRootBox.Text.Trim()),
        NotifyActivity = notifyActivityBox.Checked,
        NotifyUserPrompts = notifyUserPromptsBox.Checked,
        AutoRetryErrors = autoRetryBox.Checked,
        RetryMaxAttempts = (int)retryMaxAttemptsInput.Value,
        RetryIntervalSeconds = (int)retryIntervalInput.Value,
        RetryJitterSeconds = (int)retryJitterInput.Value,
        AutoApprove = autoApproveBox.Checked,
        NotifyAutoApprovals = notifyAutoApprovalsBox.Checked,
    };

    private void ConfigureRetryOptions(BridgeSettings settings)
    {
        retryOptionsGroup.Text = "重试参数";
        retryOptionsGroup.Location = new Point(26, 345);
        retryOptionsGroup.Size = new Size(568, 126);

        ConfigureNumericInput(
            retryMaxAttemptsInput,
            "最大重试次数",
            settings.RetryMaxAttempts,
            1,
            20,
            24);
        ConfigureNumericInput(
            retryIntervalInput,
            "基础间隔（秒）",
            settings.RetryIntervalSeconds,
            1,
            600,
            54);
        ConfigureNumericInput(
            retryJitterInput,
            "随机增加（秒）",
            settings.RetryJitterSeconds,
            0,
            120,
            84);

        var explanation = new Label
        {
            Text = "实际等待 = 基础间隔 + 0～随机增加秒\r\n例如 5 + 0～3 秒，每次会随机等待 5～8 秒。",
            ForeColor = Color.FromArgb(100, 116, 139),
            AutoSize = false,
            Location = new Point(270, 28),
            Size = new Size(238, 65),
        };
        retryOptionsGroup.Controls.Add(explanation);
    }

    private void BrowseWorkspaceRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择飞书新建助手项目时使用的默认工作区",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(workspaceRootBox.Text.Trim())
                ? workspaceRootBox.Text.Trim()
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            workspaceRootBox.Text = dialog.SelectedPath;
        }
    }

    private void ConfigureNumericInput(
        NumericUpDown input,
        string labelText,
        int value,
        int minimum,
        int maximum,
        int top)
    {
        var label = new Label
        {
            Text = labelText,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(16, top),
            Size = new Size(125, 26),
        };
        input.Minimum = minimum;
        input.Maximum = maximum;
        input.Value = Math.Clamp(value, minimum, maximum);
        input.Location = new Point(148, top);
        input.Size = new Size(92, 26);
        input.TextAlign = HorizontalAlignment.Right;
        retryOptionsGroup.Controls.Add(label);
        retryOptionsGroup.Controls.Add(input);
    }

    private void ConfigureCheckBox(
        CheckBox checkBox,
        string title,
        string description,
        int top,
        bool value)
    {
        checkBox.Text = $"{title}\r\n{description}";
        checkBox.Checked = value;
        checkBox.AutoSize = false;
        checkBox.Size = new Size(485, 54);
        checkBox.Location = new Point(26, top);
        checkBox.TextAlign = ContentAlignment.MiddleLeft;
        checkBox.Padding = new Padding(4, 0, 0, 0);
    }
}
