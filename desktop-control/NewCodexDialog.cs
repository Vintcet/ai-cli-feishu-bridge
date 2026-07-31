namespace CodexFeishuControl;

internal sealed class NewCodexDialog : Form
{
    private readonly TextBox directoryBox = new();
    private readonly TextBox argumentsBox = new();
    private readonly ComboBox historyCombo = new();
    private readonly CheckBox administratorBox = new();
    private readonly Button startButton = new();

    private sealed record SessionItem(CodexSession? Session, string Text);

    public NewCodexDialog(string initialDirectory, IReadOnlyList<CodexSession> historySessions)
    {
        Text = "新建同步 Codex";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(610, 335);
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9F);

        var title = new Label
        {
            Text = "启动 Windows Terminal / Codex 同步窗口",
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
            Location = new Point(24, 62),
        };
        directoryBox.Location = new Point(24, 84);
        directoryBox.Size = new Size(470, 28);
        directoryBox.Text = Directory.Exists(initialDirectory) ? initialDirectory : "";
        directoryBox.TextChanged += (_, _) => UpdateStartButton();

        var browseButton = new Button
        {
            Text = "选择…",
            Location = new Point(504, 82),
            Size = new Size(82, 31),
            FlatStyle = FlatStyle.System,
        };
        browseButton.Click += (_, _) => BrowseDirectory();

        var argumentsLabel = new Label
        {
            Text = "Codex 启动参数（可选）",
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Location = new Point(24, 120),
        };
        argumentsBox.Location = new Point(24, 142);
        argumentsBox.Size = new Size(562, 28);
        argumentsBox.MaxLength = 4_000;
        argumentsBox.PlaceholderText = "例如：resume 019faef0-d0bb-7703-af82-17ee9b45397b";

        var historyLabel = new Label
        {
            Text = "打开已往会话（可选）",
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Location = new Point(24, 178),
        };
        historyCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        historyCombo.DisplayMember = nameof(SessionItem.Text);
        historyCombo.Location = new Point(24, 200);
        historyCombo.Size = new Size(562, 30);
        PopulateHistory(historySessions);

        administratorBox.Text = "以 Windows 管理员身份启动（会弹出 UAC 确认）";
        administratorBox.AutoSize = true;
        administratorBox.Location = new Point(24, 242);
        administratorBox.ForeColor = Color.FromArgb(185, 28, 28);

        var hint = new Label
        {
            Text = "选择历史会话后会自动填入 resume 参数与对应目录；不选择则新建会话。",
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 116, 139),
            Location = new Point(43, 267),
        };

        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(405, 300),
            Size = new Size(86, 32),
        };
        startButton.Text = "启动 Codex";
        startButton.DialogResult = DialogResult.OK;
        startButton.Location = new Point(500, 300);
        startButton.Size = new Size(86, 32);
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
            historyLabel,
            historyCombo,
            administratorBox,
            hint,
            cancelButton,
            startButton,
        ]);
        UpdateStartButton();
    }

    public string SelectedDirectory => Path.GetFullPath(directoryBox.Text.Trim());

    public bool RunAsAdministrator => administratorBox.Checked;

    public string CodexArguments => argumentsBox.Text.Trim();

    private void PopulateHistory(IReadOnlyList<CodexSession> historySessions)
    {
        historyCombo.Items.Add(new SessionItem(null, "（不打开历史会话，新建会话）"));
        foreach (var session in historySessions
                     .Where(item => !string.Equals(item.Source, "opencode", StringComparison.Ordinal))
                     .OrderByDescending(HistorySortKey))
        {
            historyCombo.Items.Add(new SessionItem(session, FormatHistorySession(session)));
        }
        historyCombo.SelectedIndex = 0;
        historyCombo.SelectedIndexChanged += (_, _) =>
        {
            if (historyCombo.SelectedItem is not SessionItem { Session: not null } item)
            {
                return;
            }
            argumentsBox.Text = $"resume {item.Session.SessionId}";
            if (Directory.Exists(item.Session.Cwd))
            {
                directoryBox.Text = item.Session.Cwd;
            }
        };
    }

    private static string HistorySortKey(CodexSession session) =>
        string.IsNullOrWhiteSpace(session.EndedAt) ? session.LastSeenAt : session.EndedAt;

    private static string FormatHistorySession(CodexSession session)
    {
        var time = string.IsNullOrWhiteSpace(session.EndedAt) ? session.LastSeenAt : session.EndedAt;
        return $"{session.ProjectName} · #{session.ShortId} · {FormatTime(time)}";
    }

    private static string FormatTime(string value)
    {
        if (DateTimeOffset.TryParse(value, out var parsed))
        {
            return parsed.ToLocalTime().ToString("MM-dd HH:mm");
        }
        return value;
    }

    private void BrowseDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 Codex 要处理的项目目录",
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
