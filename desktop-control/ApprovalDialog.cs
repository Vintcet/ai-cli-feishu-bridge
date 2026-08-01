using System.Media;

namespace CodexFeishuControl;

internal sealed class ApprovalDialog : Form
{
    private static readonly Color Primary = Color.FromArgb(37, 99, 235);
    private static readonly Color Danger = Color.FromArgb(220, 38, 38);
    private static readonly Color Muted = Color.FromArgb(100, 116, 139);
    private static readonly Color Border = Color.FromArgb(203, 213, 225);

    private readonly BridgeApproval approval;
    private readonly Func<string, Task<ApprovalResolveResult>> resolver;
    private readonly Label statusLabel = new();
    private readonly Button allowButton = new();
    private readonly Button denyButton = new();
    private bool resolving;
    private bool resolved;

    public ApprovalDialog(
        BridgeApproval approval,
        Func<string, Task<ApprovalResolveResult>> resolver)
    {
        this.approval = approval;
        this.resolver = resolver;

        Text = $"助手审批 · {approval.ProjectName}";
        Icon = SystemIcons.Shield;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(620, 460);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        BackColor = Color.FromArgb(248, 250, 252);
        Font = new Font("Microsoft YaHei UI", 9F);

        Controls.Add(BuildLayout());
        Shown += (_, _) =>
        {
            SystemSounds.Exclamation.Play();
            Activate();
        };
    }

    public string RequestId => approval.RequestId;

    public event EventHandler? Dismissed;

    public void MarkResolved()
    {
        if (resolved || IsDisposed)
        {
            return;
        }
        resolved = true;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs eventArgs)
    {
        base.OnFormClosed(eventArgs);
        if (!resolved)
        {
            Dismissed?.Invoke(this, EventArgs.Empty);
        }
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 5,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            AutoSize = true,
            Text = "助手需要你的确认",
            ForeColor = Color.FromArgb(15, 23, 42),
            Font = new Font("Microsoft YaHei UI", 17F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 14),
        };

        var summary = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Text =
                $"会话：{approval.SessionLabel}\n" +
                $"工具：{approval.ToolName}\n" +
                $"目录：{approval.Cwd}",
            ForeColor = Color.FromArgb(51, 65, 85),
            Font = new Font("Microsoft YaHei UI", 9.5F),
            Margin = new Padding(0, 0, 0, 14),
        };

        var preview = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(30, 41, 59),
            BorderStyle = BorderStyle.FixedSingle,
            Text = string.IsNullOrWhiteSpace(approval.ToolPreview)
                ? "（没有可展示的参数）"
                : approval.ToolPreview,
            Font = new Font("Cascadia Mono", 9F),
            Margin = new Padding(0, 0, 0, 14),
        };

        statusLabel.AutoSize = true;
        statusLabel.Text = "电脑端或飞书端任意一处处理后，另一端会自动同步。";
        statusLabel.ForeColor = Muted;
        statusLabel.Margin = new Padding(0, 0, 0, 14);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        ConfigureButton(allowButton, "批准一次", Primary, Color.White, Primary);
        ConfigureButton(denyButton, "拒绝", Color.White, Danger, Danger);
        allowButton.Click += async (_, _) => await SubmitAsync("allow");
        denyButton.Click += async (_, _) => await SubmitAsync("deny");

        buttons.Controls.Add(allowButton);
        buttons.Controls.Add(denyButton);

        root.Controls.Add(title, 0, 0);
        root.Controls.Add(summary, 0, 1);
        root.Controls.Add(preview, 0, 2);
        root.Controls.Add(statusLabel, 0, 3);
        root.Controls.Add(buttons, 0, 4);
        return root;
    }

    private async Task SubmitAsync(string resolution)
    {
        if (resolving || resolved)
        {
            return;
        }

        resolving = true;
        SetButtonsEnabled(false);
        statusLabel.ForeColor = Muted;
        statusLabel.Text = "正在同步审批结果…";
        try
        {
            var result = await resolver(resolution);
            resolved = true;
            statusLabel.ForeColor = Color.FromArgb(22, 163, 74);
            statusLabel.Text = string.IsNullOrWhiteSpace(result.Message)
                ? "审批已处理。"
                : result.Message;
            Close();
        }
        catch (Exception error)
        {
            resolving = false;
            SetButtonsEnabled(true);
            statusLabel.ForeColor = Danger;
            statusLabel.Text = $"处理失败：{error.Message}";
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        allowButton.Enabled = enabled;
        denyButton.Enabled = enabled;
    }

    private static void ConfigureButton(
        Button button,
        string text,
        Color background,
        Color foreground,
        Color border)
    {
        button.Text = text;
        button.Size = new Size(110, 40);
        button.Margin = new Padding(10, 0, 0, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = border;
        button.BackColor = background;
        button.ForeColor = foreground;
        button.Cursor = Cursors.Hand;
        button.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
    }
}
