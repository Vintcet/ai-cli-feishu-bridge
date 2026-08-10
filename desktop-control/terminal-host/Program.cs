namespace AiCliFeishuControl;

internal static class Program
{
    private static int Main(string[] args) =>
        args.Contains("--bridge-hook", StringComparer.OrdinalIgnoreCase)
            ? ManagedHookRelay.Run(args)
            : ManagedTerminalHost.Run(args);
}
