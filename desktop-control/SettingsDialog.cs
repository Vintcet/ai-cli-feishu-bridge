namespace CodexFeishuControl;

internal sealed class SettingsDialog : Form
{
    private readonly CheckBox notifyActivityBox = new();
    private readonly CheckBox autoRetryBox = new();
    private readonly CheckBox autoApproveBox = new();
    private readonly GroupBox retryOptionsGroup = new();
    private readonly NumericUpDown retryMaxAttemptsInput = new();
    private readonly NumericUpDown retryIntervalInput = new();
    private readonly NumericUpDown retryJitterInput = new();
    private readonly bool initiallyAutoApprove;

    public SettingsDialog(BridgeSettings settings)
    {
        initiallyAutoApprove = settings.AutoApprove;
        Text = "通知与自动处理设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(580, 480);
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
            Text = "完成、补充信息、审批和错误始终通知；以下开关控制额外行为。",
            ForeColor = Color.FromArgb(100, 116, 139),
            AutoSize = true,
            Location = new Point(25, 52),
        };

        ConfigureCheckBox(
            notifyActivityBox,
            "同步普通过程信息",
            "在飞书持续更新工具执行和处理进度。关闭时只推送关键节点。",
            86,
            settings.NotifyActivity);
        ConfigureCheckBox(
            autoRetryBox,
            "临时错误自动重试",
            "遇到 429、503、服务繁忙或超时等错误时，按下方参数重试。",
            148,
            settings.AutoRetryErrors);
        ConfigureRetryOptions(settings);
        ConfigureCheckBox(
            autoApproveBox,
            "审批请求自动允许",
            "高风险：Codex 请求权限时不再等待人工确认，并在飞书留下记录。",
            342,
            settings.AutoApprove);
        autoRetryBox.CheckedChanged += (_, _) =>
            retryOptionsGroup.Enabled = autoRetryBox.Checked;
        retryOptionsGroup.Enabled = autoRetryBox.Checked;

        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Size = new Size(90, 36),
            Location = new Point(366, 425),
        };
        var saveButton = new Button
        {
            Text = "保存",
            DialogResult = DialogResult.OK,
            Size = new Size(90, 36),
            Location = new Point(466, 425),
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        saveButton.FlatAppearance.BorderSize = 0;

        Controls.AddRange([
            title, hint, notifyActivityBox, autoRetryBox, retryOptionsGroup, autoApproveBox,
            cancelButton, saveButton,
        ]);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        if (DialogResult == DialogResult.OK &&
            autoApproveBox.Checked &&
            !initiallyAutoApprove)
        {
            var confirmation = MessageBox.Show(
                this,
                "开启后，Codex 的权限请求将直接允许，不再等待你确认。确定开启吗？",
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
        NotifyActivity = notifyActivityBox.Checked,
        AutoRetryErrors = autoRetryBox.Checked,
        RetryMaxAttempts = (int)retryMaxAttemptsInput.Value,
        RetryIntervalSeconds = (int)retryIntervalInput.Value,
        RetryJitterSeconds = (int)retryJitterInput.Value,
        AutoApprove = autoApproveBox.Checked,
    };

    private void ConfigureRetryOptions(BridgeSettings settings)
    {
        retryOptionsGroup.Text = "重试参数";
        retryOptionsGroup.Location = new Point(26, 204);
        retryOptionsGroup.Size = new Size(528, 126);

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
