namespace AiCliFeishuControl;

internal sealed class NewRuntimeDialog : Form
{
    private readonly RuntimeProfile runtime;
    private readonly TextBox directoryBox = new();
    private readonly TextBox argumentsBox = new();
    private readonly CheckBox administratorBox = new();
    private readonly Button startButton = new();
    private readonly Label launchHintLabel = new();
    private readonly IReadOnlyList<AssistantSession> knownSessions;

    public NewRuntimeDialog(
        RuntimeProfile runtime,
        string initialDirectory,
        IReadOnlyList<AssistantSession>? knownSessions = null)
    {
        this.runtime = runtime;
        this.knownSessions = knownSessions ?? [];
        Text = $"新建同步 {runtime.DisplayName}";
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
            Text = $"启动 Windows Terminal / {runtime.DisplayName} 同步窗口",
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
            Text = $"{runtime.DisplayName} 启动参数（可选）",
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Location = new Point(24, 127),
        };
        argumentsBox.Location = new Point(24, 150);
        argumentsBox.Size = new Size(562, 28);
        argumentsBox.MaxLength = 4_000;
        argumentsBox.PlaceholderText = runtime.ArgumentsPlaceholder;
        argumentsBox.TextChanged += (_, _) => TrySelectResumeDirectory();

        administratorBox.Text = "以 Windows 管理员身份启动（会弹出 UAC 确认）";
        administratorBox.AutoSize = true;
        administratorBox.Location = new Point(24, 190);
        administratorBox.ForeColor = Color.FromArgb(185, 28, 28);

        launchHintLabel.Text = runtime.LaunchHint;
        launchHintLabel.AutoSize = false;
        launchHintLabel.AutoEllipsis = true;
        launchHintLabel.Size = new Size(543, 38);
        launchHintLabel.ForeColor = Color.FromArgb(100, 116, 139);
        launchHintLabel.Location = new Point(43, 215);

        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(356, 260),
            Size = new Size(86, 32),
        };
        startButton.Text = $"启动 {runtime.DisplayName}";
        startButton.DialogResult = DialogResult.OK;
        startButton.Location = new Point(452, 260);
        startButton.Size = new Size(134, 32);
        startButton.Enabled = false;
        startButton.Click += (_, _) =>
        {
            if (Directory.Exists(directoryBox.Text.Trim())) return;
            DialogResult = DialogResult.None;
            MessageBox.Show(
                this,
                "请选择一个存在的项目目录。",
                "目录无效",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
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
            launchHintLabel,
            cancelButton,
            startButton,
        ]);
        UpdateStartButton();
    }

    public string SelectedDirectory => Path.GetFullPath(directoryBox.Text.Trim());

    public bool RunAsAdministrator => administratorBox.Checked;

    public string Arguments => argumentsBox.Text.Trim();

    private void BrowseDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = $"选择 {runtime.DisplayName} 要处理的项目目录",
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

    private void TrySelectResumeDirectory()
    {
        string? sessionId;
        try
        {
            sessionId = RuntimeArgumentParser.ExtractResumeSessionId(
                runtime,
                argumentsBox.Text);
        }
        catch
        {
            launchHintLabel.Text = runtime.LaunchHint;
            return;
        }
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            launchHintLabel.Text = runtime.LaunchHint;
            return;
        }
        var session = knownSessions.FirstOrDefault(item =>
            item.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase) &&
            RuntimeCatalog.FromId(item.Runtime).Id.Equals(
                runtime.Id,
                StringComparison.OrdinalIgnoreCase));
        if (session is null || !Directory.Exists(session.Cwd))
        {
            launchHintLabel.Text = $"检测到恢复参数，但本机历史中没有可用的 {runtime.DisplayName} 工作目录。";
            return;
        }
        directoryBox.Text = session.Cwd;
        launchHintLabel.Text = $"已从会话 #{session.ShortId} 自动识别工作目录：{session.Cwd}";
    }

    private void UpdateStartButton() =>
        startButton.Enabled = Directory.Exists(directoryBox.Text.Trim());
}
