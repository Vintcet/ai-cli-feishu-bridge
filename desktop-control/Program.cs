using System.Runtime.InteropServices;
using System.Text;

namespace AiCliFeishuControl;

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
        if (args.Contains("--bridge-start", StringComparer.OrdinalIgnoreCase))
        {
            return RunBridgeCommand(start: true);
        }
        if (args.Contains("--bridge-stop", StringComparer.OrdinalIgnoreCase))
        {
            return RunBridgeCommand(start: false);
        }
        if (args.Contains("--bridge-service", StringComparer.OrdinalIgnoreCase))
        {
            return RunBridgeService();
        }

        var instanceScope = CurrentDesktopScope();
        var mutexName = $"AiCliFeishuControl.SingleInstance.{instanceScope}";
        var activateEventName = $"AiCliFeishuControl.Activate.{instanceScope}";
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

    private static int RunBridgeCommand(bool start)
    {
        try
        {
            using var bridgeClient = new BridgeClient();
            AppLog.Initialize(Path.Combine(bridgeClient.BridgeRoot, "data"));
            if (start)
            {
                RequireSafeStartupRecovery(bridgeClient);
                bridgeClient.StartAsync().GetAwaiter().GetResult();
                for (var attempt = 0; attempt < 25; attempt += 1)
                {
                    Thread.Sleep(400);
                    if (bridgeClient.GetStatusAsync().GetAwaiter().GetResult()?.Ok == true)
                    {
                        return 0;
                    }
                }
                throw new InvalidOperationException("桥接服务没有在预期时间内启动。");
            }

            bridgeClient.StopAsync().GetAwaiter().GetResult();
            for (var attempt = 0; attempt < 25; attempt += 1)
            {
                if (bridgeClient.GetStatusAsync().GetAwaiter().GetResult() is null)
                {
                    return 0;
                }
                Thread.Sleep(200);
            }
            throw new InvalidOperationException("桥接服务没有在预期时间内停止。");
        }
        catch (Exception error)
        {
            AppLog.Error(start ? "命令行启动桥接失败" : "命令行停止桥接失败", error);
            return 1;
        }
    }

    private static int RunBridgeService()
    {
        try
        {
            using var bridgeClient = new BridgeClient();
            AppLog.Initialize(Path.Combine(bridgeClient.BridgeRoot, "data"));
            RequireSafeStartupRecovery(bridgeClient);
            return bridgeClient.RunBridgeService();
        }
        catch (Exception error)
        {
            AppLog.Error("后台桥接宿主失败", error);
            return 1;
        }
    }

    private static void RequireSafeStartupRecovery(BridgeClient bridgeClient)
    {
        var recovery = bridgeClient.RecoverProductionHostOnStartupAsync()
            .AsTask()
            .GetAwaiter()
            .GetResult();
        if (!recovery.CanContinue)
        {
            throw new InvalidOperationException(recovery.UserMessage);
        }
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
