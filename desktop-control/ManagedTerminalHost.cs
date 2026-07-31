using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CodexFeishuControl;

internal static class ManagedTerminalHost
{
    private const int StdInputHandle = -10;
    private const short KeyEvent = 0x0001;
    private const ushort VirtualKeyReturn = 0x0D;
    private const ushort VirtualScanReturn = 0x1C;
    private const ushort VirtualKeyTab = 0x09;
    private const ushort VirtualScanTab = 0x0F;
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public static int Run(string[] args)
    {
        var terminalId = ReadArgument(args, "--id");
        var cwd = ReadArgument(args, "--cwd");
        var bridgeUrl = ReadArgument(args, "--bridge-url") ?? "http://127.0.0.1:8765";
        var rawCodexArguments = ReadArgument(args, "--codex-args");
        if (string.IsNullOrWhiteSpace(terminalId) ||
            !terminalId.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') ||
            terminalId.Length is < 8 or > 64)
        {
            return 2;
        }
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
        {
            return 3;
        }

        if (!EnsureConsole())
        {
            return 4;
        }

        SetConsoleCP(65001);
        SetConsoleOutputCP(65001);
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);

        var elevated = IsElevated();
        var projectName = new DirectoryInfo(cwd).Name;
        SetConsoleTitle($"Codex · {projectName}{(elevated ? " · 管理员" : "")}");

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var codexArguments = CodexArgumentParser.Parse(rawCodexArguments);
            RegisterTerminalAsync(
                    terminalId,
                    cwd,
                    bridgeUrl,
                    elevated,
                    ready: false,
                    cancellationToken: CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            using var job = CreateKillOnCloseJob();
            using var powershell = StartPowerShell(
                cwd,
                terminalId,
                bridgeUrl,
                elevated,
                codexArguments);
            if (job is not null)
            {
                AssignProcessToJobObject(job, powershell.Handle);
            }
            var heartbeatTask = RunRegistrationHeartbeatAsync(
                terminalId,
                cwd,
                bridgeUrl,
                elevated,
                powershell,
                cancellation.Token);
            var pipeTask = RunPipeServerAsync(terminalId, powershell, cancellation.Token);
            powershell.WaitForExit();
            cancellation.Cancel();
            try { pipeTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { heartbeatTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
            return powershell.ExitCode;
        }
        catch (Exception error)
        {
            Console.WriteLine();
            Console.WriteLine($"Codex 启动失败：{error.Message}");
            Console.WriteLine("按任意键关闭窗口。");
            try { Console.ReadKey(intercept: true); } catch { }
            return 5;
        }
        finally
        {
            try
            {
                UnregisterTerminalAsync(terminalId, bridgeUrl, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                // Heartbeat expiry remains the fallback if the bridge is unavailable.
            }
            FreeConsole();
        }
    }

    private static Process StartPowerShell(
        string cwd,
        string terminalId,
        string bridgeUrl,
        bool elevated,
        IReadOnlyList<string> codexArguments)
    {
        var executable = File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe")
            ? @"C:\Program Files\PowerShell\7\pwsh.exe"
            : "powershell.exe";
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$codexArgsJson = $env:CODEX_FEISHU_CODEX_ARGS_JSON; " +
            "Remove-Item Env:CODEX_FEISHU_CODEX_ARGS_JSON -ErrorAction SilentlyContinue; " +
            "$codexArgs = @($codexArgsJson | ConvertFrom-Json); " +
            "& codex @codexArgs; exit $LASTEXITCODE");
        startInfo.Environment["CODEX_FEISHU_MANAGED_TERMINAL_ID"] = terminalId;
        startInfo.Environment["CODEX_FEISHU_MANAGED_TERMINAL_ELEVATED"] = elevated ? "1" : "0";
        startInfo.Environment["CODEX_FEISHU_BRIDGE_URL"] = bridgeUrl;
        startInfo.Environment["CODEX_FEISHU_CODEX_ARGS_JSON"] = JsonSerializer.Serialize(
            BuildCodexArguments(terminalId, bridgeUrl, elevated, codexArguments));

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 PowerShell。");
        }
        return process;
    }

    private static async Task RegisterTerminalAsync(
        string terminalId,
        string cwd,
        string bridgeUrl,
        bool elevated,
        bool ready,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var response = await client.PostAsJsonAsync(
            $"{bridgeUrl.TrimEnd('/')}/managed-terminals/register",
            new { terminalId, cwd, elevated, ready },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task UnregisterTerminalAsync(
        string terminalId,
        string bridgeUrl,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var response = await client.PostAsJsonAsync(
            $"{bridgeUrl.TrimEnd('/')}/managed-terminals/unregister",
            new { terminalId },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task RunRegistrationHeartbeatAsync(
        string terminalId,
        string cwd,
        string bridgeUrl,
        bool elevated,
        Process powershell,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                await RegisterTerminalAsync(
                    terminalId,
                    cwd,
                    bridgeUrl,
                    elevated,
                    ready: !powershell.HasExited,
                    cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // The bridge may be restarting. A later heartbeat will register again.
            }
        }
    }

    private static IReadOnlyList<string> BuildCodexArguments(
        string terminalId,
        string bridgeUrl,
        bool elevated,
        IReadOnlyList<string> codexArguments)
    {
        static string ConfigSet(string name, string value)
        {
            var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return $"shell_environment_policy.set.{name}=\"{escaped}\"";
        }

        var result = new List<string>
        {
            "-c",
            ConfigSet("CODEX_FEISHU_MANAGED_TERMINAL_ID", terminalId),
            "-c",
            ConfigSet("CODEX_FEISHU_MANAGED_TERMINAL_ELEVATED", elevated ? "1" : "0"),
            "-c",
            ConfigSet("CODEX_FEISHU_BRIDGE_URL", bridgeUrl),
        };
        result.AddRange(codexArguments);
        return result;
    }

    private static async Task RunPipeServerAsync(
        string terminalId,
        Process powershell,
        CancellationToken cancellationToken)
    {
        var pipeName = $"CodexFeishu.{terminalId}";
        while (!cancellationToken.IsCancellationRequested && !powershell.HasExited)
        {
            try
            {
                await using var pipe = CreatePipeServer(pipeName);
                await pipe.WaitForConnectionAsync(cancellationToken);
                object response;
                try
                {
                    using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                    var line = await reader.ReadLineAsync(cancellationToken);
                    var input = TerminalInputParser.Parse(line);
                    if (powershell.HasExited)
                    {
                        throw new InvalidOperationException("Codex 窗口已经关闭。");
                    }
                    InjectPrompt(input);
                    response = new { ok = true, error = (string?)null };
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    response = new { ok = false, error = error.Message };
                }
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
                {
                    AutoFlush = true,
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A failed client connection is isolated; the next connection can still retry.
            }
        }
    }

    private static NamedPipeServerStream CreatePipeServer(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException("无法识别当前 Windows 用户。");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(
            user,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            16_384,
            16_384,
            security);
    }

    private static void InjectPrompt(TerminalInputRequest input)
    {
        var inputHandle = GetStdHandle(StdInputHandle);
        if (inputHandle == IntPtr.Zero || inputHandle == new IntPtr(-1))
        {
            throw new InvalidOperationException("无法访问 Codex 窗口输入缓冲区。");
        }

        var records = new List<InputRecord>(input.Prompt.Length * 2);
        foreach (var character in input.Prompt)
        {
            records.Add(CreateKeyRecord(character, keyDown: true));
            records.Add(CreateKeyRecord(character, keyDown: false));
        }
        WriteInputRecords(inputHandle, records);

        // Codex deliberately treats a rapid burst of characters as pasted text. Give its
        // paste detector time to flush before sending the submit key, otherwise Enter can
        // become a newline inside the prompt. Enter steers the current turn; Tab queues the
        // prompt for the next turn.
        Thread.Sleep(600);
        var submitCharacter = input.SubmitMode == TerminalSubmitMode.Queue ? '\t' : '\r';
        var submitVirtualKey = input.SubmitMode == TerminalSubmitMode.Queue
            ? VirtualKeyTab
            : VirtualKeyReturn;
        var submitScanCode = input.SubmitMode == TerminalSubmitMode.Queue
            ? VirtualScanTab
            : VirtualScanReturn;
        WriteInputRecords(inputHandle,
        [
            CreateKeyRecord(submitCharacter, keyDown: true, submitVirtualKey, submitScanCode),
            CreateKeyRecord(submitCharacter, keyDown: false, submitVirtualKey, submitScanCode),
        ]);
    }

    private static void WriteInputRecords(IntPtr inputHandle, IReadOnlyCollection<InputRecord> records)
    {
        var buffer = records.ToArray();
        if (!WriteConsoleInput(inputHandle, buffer, (uint)buffer.Length, out var written) ||
            written != buffer.Length)
        {
            throw new InvalidOperationException("向 Codex 窗口写入回复失败。");
        }
    }

    private static InputRecord CreateKeyRecord(
        char character,
        bool keyDown,
        ushort virtualKeyCode = 0,
        ushort virtualScanCode = 0) =>
        new()
        {
            EventType = KeyEvent,
            KeyEvent = new KeyEventRecord
            {
                KeyDown = keyDown,
                RepeatCount = 1,
                VirtualKeyCode = virtualKeyCode,
                VirtualScanCode = virtualScanCode,
                UnicodeChar = character,
                ControlKeyState = 0,
            },
        };

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool EnsureConsole()
    {
        if (GetConsoleWindow() != IntPtr.Zero)
        {
            return true;
        }
        return AttachConsole(AttachParentProcess) || AllocConsole();
    }

    private static SafeFileHandle? CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            job.Dispose();
            return null;
        }
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = 0x00002000,
            },
        };
        if (!SetInformationJobObject(
                job,
                9,
                ref information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            job.Dispose();
            return null;
        }
        return job;
    }

    private static string? ReadArgument(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return null;
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    private struct InputRecord
    {
        [FieldOffset(0)]
        public short EventType;

        [FieldOffset(4)]
        public KeyEventRecord KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct KeyEventRecord
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public char UnicodeChar;
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleTitle(string title);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCP(uint codePageId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleOutputCP(uint codePageId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", EntryPoint = "WriteConsoleInputW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteConsoleInput(
        IntPtr consoleInput,
        [In] InputRecord[] buffer,
        uint length,
        out uint numberOfEventsWritten);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(
        IntPtr jobAttributes,
        string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        IntPtr process);
}
