namespace CodexFeishuControl;

internal sealed class SettingsDialog : Form
{
    private readonly CheckBox notifyActivityBox = new();
    private readonly CheckBox autoRetryBox = new();
    private readonly CheckBox autoApproveBox = new();
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
        ClientSize = new Size(540, 330);
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
            "遇到 429、503、服务繁忙或超时等错误时，最多重试 3 次。",
            148,
            settings.AutoRetryErrors);
        ConfigureCheckBox(
            autoApproveBox,
            "审批请求自动允许",
            "高风险：Codex 请求权限时不再等待人工确认，并在飞书留下记录。",
            210,
            settings.AutoApprove);

        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Size = new Size(90, 36),
            Location = new Point(326, 275),
        };
        var saveButton = new Button
        {
            Text = "保存",
            DialogResult = DialogResult.OK,
            Size = new Size(90, 36),
            Location = new Point(426, 275),
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        saveButton.FlatAppearance.BorderSize = 0;

        Controls.AddRange([
            title, hint, notifyActivityBox, autoRetryBox, autoApproveBox,
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
        AutoApprove = autoApproveBox.Checked,
    };

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
