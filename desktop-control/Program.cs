namespace CodexFeishuControl;

internal static class Program
{
    private const string SingleInstanceMutexName = "CodexFeishuControl.SingleInstance";
    private const string ActivateEventName = "CodexFeishuControl.Activate";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--managed-terminal", StringComparer.OrdinalIgnoreCase))
        {
            return ManagedTerminalHost.Run(args);
        }

        using var mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            return 0;
        }

        using var activateEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ActivateEventName);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(activateEvent));
        return 0;
    }

    private static void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 20; attempt += 1)
        {
            try
            {
                using var activateEvent = EventWaitHandle.OpenExisting(ActivateEventName);
                activateEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
