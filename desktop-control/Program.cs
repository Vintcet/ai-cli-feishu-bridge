using System.Runtime.InteropServices;
using System.Text;

namespace CodexFeishuControl;

internal static class Program
{
    private const int UoiName = 2;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--managed-terminal", StringComparer.OrdinalIgnoreCase))
        {
            return ManagedTerminalHost.Run(args);
        }

        var instanceScope = CurrentDesktopScope();
        var mutexName = $"CodexFeishuControl.SingleInstance.{instanceScope}";
        var activateEventName = $"CodexFeishuControl.Activate.{instanceScope}";
        using var mutex = new Mutex(true, mutexName, out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstance(activateEventName);
            return 0;
        }

        using var activateEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            activateEventName);
        ApplicationConfiguration.Initialize();
        using var mainForm = new MainForm(activateEvent);
        mainForm.WindowState = FormWindowState.Normal;
        mainForm.ShowInTaskbar = true;
        mainForm.Show();
        Application.Run(mainForm);
        return 0;
    }

    private static string CurrentDesktopScope()
    {
        var windowStation = UserObjectName(GetProcessWindowStation(), "WinSta0");
        var desktop = UserObjectName(
            GetThreadDesktop(GetCurrentThreadId()),
            "Default");
        var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var rawScope = $"{sessionId}:{windowStation}:{desktop}";
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(rawScope)))[..16];
    }

    private static string UserObjectName(IntPtr handle, string fallback)
    {
        if (handle == IntPtr.Zero)
        {
            return fallback;
        }
        var buffer = new StringBuilder(256);
        return GetUserObjectInformation(
            handle,
            UoiName,
            buffer,
            buffer.Capacity * sizeof(char),
            out _)
            ? buffer.ToString()
            : fallback;
    }

    private static void SignalExistingInstance(string activateEventName)
    {
        for (var attempt = 0; attempt < 20; attempt += 1)
        {
            try
            {
                using var activateEvent = EventWaitHandle.OpenExisting(activateEventName);
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

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(
        IntPtr handle,
        int index,
        StringBuilder information,
        int informationLength,
        out int neededLength);
}
