namespace CodexFeishuControl;

internal sealed class NewClaudeCodeDialog : Form
{
    private readonly TextBox directoryBox = new();
    private readonly TextBox argumentsBox = new();
    private readonly CheckBox administratorBox = new();
    private readonly Button startButton = new();

    public NewClaudeCodeDialog(string initialDirectory)
    {
        Text = "新建同步 Claude Code";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(610, 307);
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9F);

        var title = new Label
        {
            Text = "启动 Windows Terminal / Claude Code 同步窗口",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Location = new Point(24, 20),
        };
        var directoryLabel = new Label
        {
            Text = "项目目录",
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Location = new Point(24, 65),
        };
        directoryBox.Location = new Point(24, 88);
        directoryBox.Size = new Size(470, 28);
        directoryBox.Text = Directory.Exists(initialDirectory) ? initialDirectory : "";
        directoryBox.TextChanged += (_, _) => UpdateStartButton();

        var browseButton = new Button
        {
            Text = "选择…",
            Location = new Point(504, 86),
            Size = new Size(82, 31),
            FlatStyle = FlatStyle.System,
        };
        browseButton.Click += (_, _) => BrowseDirectory();

        var argumentsLabel = new Label
        {
            Text = "Claude Code 启动参数（可选）",
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Location = new Point(24, 127),
        };
        argumentsBox.Location = new Point(24, 150);
        argumentsBox.Size = new Size(562, 28);
        argumentsBox.MaxLength = 4_000;
        argumentsBox.PlaceholderText = "例如：--resume 019faef0-d0bb-7703-af82-17ee9b45397b";

        administratorBox.Text = "以 Windows 管理员身份启动（会弹出 UAC 确认）";
        administratorBox.AutoSize = true;
        administratorBox.Location = new Point(24, 190);
        administratorBox.ForeColor = Color.FromArgb(185, 28, 28);

        var hint = new Label
        {
            Text = "Claude Code CLI（claude）需已安装并可用；桥接服务会自动登记会话。",
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 116, 139),
            Location = new Point(43, 215),
        };

        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(356, 260),
            Size = new Size(86, 32),
        };
        startButton.Text = "启动 Claude Code";
        startButton.DialogResult = DialogResult.OK;
        startButton.Location = new Point(452, 260);
        startButton.Size = new Size(134, 32);
        startButton.Enabled = false;
        startButton.Click += (_, _) =>
        {
            if (Directory.Exists(directoryBox.Text.Trim())) return;
            DialogResult = DialogResult.None;
            MessageBox.Show(this, "请选择一个存在的项目目录。", "目录无效",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        AcceptButton = startButton;
        CancelButton = cancelButton;
        Controls.AddRange([
            title,
            directoryLabel,
            directoryBox,
            browseButton,
            argumentsLabel,
            argumentsBox,
            administratorBox,
            hint,
            cancelButton,
            startButton,
        ]);
        UpdateStartButton();
    }

    public string SelectedDirectory => Path.GetFullPath(directoryBox.Text.Trim());

    public bool RunAsAdministrator => administratorBox.Checked;

    public string ClaudeCodeArguments => argumentsBox.Text.Trim();

    private void BrowseDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 Claude Code 要处理的项目目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(directoryBox.Text.Trim())
                ? directoryBox.Text.Trim()
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            directoryBox.Text = dialog.SelectedPath;
        }
    }

    private void UpdateStartButton() =>
        startButton.Enabled = Directory.Exists(directoryBox.Text.Trim());
}
