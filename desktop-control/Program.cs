namespace CodexFeishuControl;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--managed-terminal", StringComparer.OrdinalIgnoreCase))
        {
            return ManagedTerminalHost.Run(args);
        }

        using var mutex = new Mutex(true, "CodexFeishuControl.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Codex 飞书助手已经打开。",
                "Codex 飞书助手",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
