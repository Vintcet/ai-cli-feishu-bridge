using System.Text;
using System.Text.RegularExpressions;

namespace CodexFeishuControl;

internal sealed class SessionAliasDialog : Form
{
    private static readonly Regex ValidAlias = new(
        @"^[\p{L}\p{N}_-]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly TextBox aliasBox = new();

    public SessionAliasDialog(CodexSession session)
    {
        Text = "设置会话别名";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 250);
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9F);

        var title = new Label
        {
            Text = "给这个助手会话起一个容易识别的名字",
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Location = new Point(24, 20),
        };
        var sessionLabel = new Label
        {
            Text = $"会话：{session.ProjectName}  #{session.ShortId}",
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(100, 116, 139),
            Location = new Point(25, 55),
            Size = new Size(470, 24),
        };
        var aliasLabel = new Label
        {
            Text = "别名（输入时不用加 @）",
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Location = new Point(24, 88),
        };

        aliasBox.Location = new Point(24, 113);
        aliasBox.Size = new Size(472, 28);
        aliasBox.Text = session.Alias;
        aliasBox.MaxLength = 40;

        var hint = new Label
        {
            Text = "1–20 个字符，可用中文、字母、数字、下划线和短横线；活跃会话间不能重名。",
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 116, 139),
            Location = new Point(24, 150),
        };
        var clearButton = new Button
        {
            Text = "清除别名",
            Location = new Point(24, 196),
            Size = new Size(96, 34),
        };
        clearButton.Click += (_, _) =>
        {
            Alias = null;
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(314, 196),
            Size = new Size(86, 34),
        };
        var saveButton = new Button
        {
            Text = "保存",
            Location = new Point(410, 196),
            Size = new Size(86, 34),
        };
        saveButton.Click += (_, _) => SaveAlias();

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Controls.AddRange([
            title,
            sessionLabel,
            aliasLabel,
            aliasBox,
            hint,
            clearButton,
            cancelButton,
            saveButton,
        ]);
        Shown += (_, _) =>
        {
            aliasBox.Focus();
            aliasBox.SelectAll();
        };
    }

    public string? Alias { get; private set; }

    private void SaveAlias()
    {
        var alias = aliasBox.Text.Trim().Normalize(NormalizationForm.FormC);
        if (string.IsNullOrWhiteSpace(alias))
        {
            MessageBox.Show(this, "请输入别名，或点击“清除别名”。", "别名为空",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (alias.EnumerateRunes().Count() > 20)
        {
            MessageBox.Show(this, "别名最多 20 个字符。", "别名过长",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!ValidAlias.IsMatch(alias))
        {
            MessageBox.Show(this, "别名只能包含中文、字母、数字、下划线或短横线，不能包含空格。", "别名格式不正确",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Alias = alias;
        DialogResult = DialogResult.OK;
        Close();
    }
}
