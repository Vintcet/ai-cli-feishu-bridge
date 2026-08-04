using System.Diagnostics;
using System.ComponentModel;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace AiCliFeishuControl;

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
        var bridgeRoot = ReadArgument(args, "--bridge-root");
        var runtimeId = ReadArgument(args, "--runtime") ?? RuntimeCatalog.Codex.Id;
        var toolCommand = ReadArgument(args, "--tool-command");
        var rawToolArguments = ReadArgument(args, "--tool-args") ??
            ReadArgument(args, "--codex-args");
        var forwardedToolArguments = RuntimeArgumentParser.ReadRepeatedArguments(
            args,
            "--tool-arg");
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
        if (string.IsNullOrWhiteSpace(bridgeRoot) || !Directory.Exists(bridgeRoot))
        {
            return 3;
        }
        if (!RuntimeCatalog.TryGet(runtimeId, out var runtime))
        {
            return 2;
        }
        toolCommand = string.IsNullOrWhiteSpace(toolCommand)
            ? runtime.CommandName
            : toolCommand.Trim();
        if (toolCommand.Length > 32_000 || toolCommand.IndexOfAny(['\r', '\n']) >= 0)
        {
            return 2;
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
        SetConsoleTitle($"{runtime.DisplayName} · {projectName}{(elevated ? " · 管理员" : "")}");

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var controlToken = "";
        try
        {
            controlToken = ReadControlToken(bridgeRoot);
            var toolArguments = forwardedToolArguments.Count > 0
                ? ValidateForwardedArguments(runtime, forwardedToolArguments)
                : RuntimeArgumentParser.Parse(runtime, rawToolArguments);
            if (!runtime.UsesManagedTerminal)
            {
                return RunHostedRuntime(
                    cwd,
                    terminalId,
                    bridgeUrl,
                    controlToken,
                    elevated,
                    runtime,
                    toolCommand,
                    toolArguments);
            }
            RegisterTerminalAsync(
                    terminalId,
                    cwd,
                    bridgeUrl,
                    controlToken,
                    elevated,
                    runtime.Id,
                    ready: false,
                    cancellationToken: CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            using var job = CreateKillOnCloseJob();
            using var powershell = StartPowerShell(
                cwd,
                terminalId,
                bridgeUrl,
                controlToken,
                elevated,
                runtime,
                toolCommand,
                toolArguments);
            if (job is not null)
            {
                AssignProcessToJobObject(job, powershell.Handle);
            }
            var heartbeatTask = RunRegistrationHeartbeatAsync(
                terminalId,
                cwd,
                bridgeUrl,
                controlToken,
                elevated,
                runtime,
                powershell,
                cancellation.Token);
            var pipeTask = RunPipeServerAsync(
                terminalId,
                elevated,
                runtime,
                powershell,
                cancellation.Token);
            powershell.WaitForExit();
            cancellation.Cancel();
            try { pipeTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { heartbeatTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
            return powershell.ExitCode;
        }
        catch (Exception error)
        {
            Console.WriteLine();
            Console.WriteLine($"{runtime.DisplayName} 启动失败：{error.Message}");
            Console.WriteLine("按任意键关闭窗口。");
            try { Console.ReadKey(intercept: true); } catch { }
            return 5;
        }
        finally
        {
            if (runtime.UsesManagedTerminal)
            {
                try
                {
                    UnregisterTerminalAsync(
                            terminalId,
                            bridgeUrl,
                            controlToken,
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
                catch
                {
                    // Heartbeat expiry remains the fallback if the bridge is unavailable.
                }
            }
            FreeConsole();
        }
    }

    private static int RunHostedRuntime(
        string cwd,
        string terminalId,
        string bridgeUrl,
        string controlToken,
        bool elevated,
        RuntimeProfile runtime,
        string toolCommand,
        IReadOnlyList<string> toolArguments)
    {
        using var job = CreateKillOnCloseJob();
        using var powershell = StartPowerShell(
            cwd,
            terminalId,
            bridgeUrl,
            controlToken,
            elevated,
            runtime,
            toolCommand,
            toolArguments);
        if (job is not null)
        {
            AssignProcessToJobObject(job, powershell.Handle);
        }
        powershell.WaitForExit();
        if (powershell.ExitCode != 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{runtime.DisplayName} 已退出（代码 {powershell.ExitCode}）。");
            Console.WriteLine("请查看上方错误信息；按任意键关闭窗口。");
            try { Console.ReadKey(intercept: true); } catch { }
        }
        return powershell.ExitCode;
    }

    private static IReadOnlyList<string> ValidateForwardedArguments(
        RuntimeProfile runtime,
        IReadOnlyList<string> arguments)
    {
        if (arguments.Count > 256 ||
            arguments.Sum(argument => argument.Length) > 32_000 ||
            arguments.Any(argument => argument.IndexOfAny(['\r', '\n']) >= 0))
        {
            throw new InvalidOperationException($"{runtime.DisplayName} 启动参数无效或过长。");
        }
        return arguments;
    }

    private static Process StartPowerShell(
        string cwd,
        string terminalId,
        string bridgeUrl,
        string controlToken,
        bool elevated,
        RuntimeProfile runtime,
        string toolCommand,
        IReadOnlyList<string> toolArguments)
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
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$toolCommand = $env:AI_CLI_FEISHU_TOOL_COMMAND; " +
            "$toolArgsJson = $env:AI_CLI_FEISHU_TOOL_ARGS_JSON; " +
            "Remove-Item Env:AI_CLI_FEISHU_TOOL_COMMAND -ErrorAction SilentlyContinue; " +
            "Remove-Item Env:AI_CLI_FEISHU_TOOL_ARGS_JSON -ErrorAction SilentlyContinue; " +
            "$toolArgs = @($toolArgsJson | ConvertFrom-Json); " +
            "$exitCode = 1; " +
            "try { " +
            "  & $toolCommand @toolArgs; " +
            "  if ($null -ne $LASTEXITCODE) { $exitCode = $LASTEXITCODE } " +
            "  elseif ($?) { $exitCode = 0 } " +
            "} catch { Write-Error $_; $exitCode = 1 }; " +
            "exit $exitCode");
        if (runtime.UsesManagedTerminal)
        {
            startInfo.Environment["AI_CLI_FEISHU_MANAGED_TERMINAL_ID"] = terminalId;
            startInfo.Environment["AI_CLI_FEISHU_MANAGED_TERMINAL_ELEVATED"] = elevated ? "1" : "0";
        }
        else
        {
            startInfo.Environment.Remove("AI_CLI_FEISHU_MANAGED_TERMINAL_ID");
            startInfo.Environment.Remove("AI_CLI_FEISHU_MANAGED_TERMINAL_ELEVATED");
        }
        startInfo.Environment["AI_CLI_FEISHU_BRIDGE_URL"] = bridgeUrl;
        startInfo.Environment["AI_CLI_FEISHU_CONTROL_TOKEN"] = controlToken;
        startInfo.Environment["AI_CLI_FEISHU_RUNTIME"] = runtime.Id;
        startInfo.Environment["AI_CLI_FEISHU_TOOL_COMMAND"] = toolCommand;
        var toolArgumentsJson = JsonSerializer.Serialize(
            runtime.InjectBridgeArguments
                ? BuildCodexArguments(terminalId, bridgeUrl, elevated, toolArguments)
                : toolArguments);
        startInfo.Environment["AI_CLI_FEISHU_TOOL_ARGS_JSON"] = toolArgumentsJson;

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
        string controlToken,
        bool elevated,
        string runtime,
        bool ready,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{bridgeUrl.TrimEnd('/')}/managed-terminals/register")
        {
            Content = JsonContent.Create(new { terminalId, cwd, elevated, runtime, ready }),
        };
        request.Headers.Add("X-AI-CLI-Feishu-Control-Token", controlToken);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task UnregisterTerminalAsync(
        string terminalId,
        string bridgeUrl,
        string controlToken,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{bridgeUrl.TrimEnd('/')}/managed-terminals/unregister")
        {
            Content = JsonContent.Create(new { terminalId }),
        };
        request.Headers.Add("X-AI-CLI-Feishu-Control-Token", controlToken);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task RunRegistrationHeartbeatAsync(
        string terminalId,
        string cwd,
        string bridgeUrl,
        string controlToken,
        bool elevated,
        RuntimeProfile runtime,
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
                    controlToken,
                    elevated,
                    runtime.Id,
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
            ConfigSet("AI_CLI_FEISHU_MANAGED_TERMINAL_ID", terminalId),
            "-c",
            ConfigSet("AI_CLI_FEISHU_MANAGED_TERMINAL_ELEVATED", elevated ? "1" : "0"),
            "-c",
            ConfigSet("AI_CLI_FEISHU_BRIDGE_URL", bridgeUrl),
        };
        result.AddRange(codexArguments);
        return result;
    }

    private static async Task RunPipeServerAsync(
        string terminalId,
        bool elevated,
        RuntimeProfile runtime,
        Process powershell,
        CancellationToken cancellationToken)
    {
        var pipeName = $"AiCliFeishu.{terminalId}";
        while (!cancellationToken.IsCancellationRequested && !powershell.HasExited)
        {
            try
            {
                await using var pipe = CreatePipeServer(pipeName, elevated);
                await pipe.WaitForConnectionAsync(cancellationToken);
                object response;
                try
                {
                    using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                    var line = await reader.ReadLineAsync(cancellationToken);
                    var input = TerminalInputParser.Parse(line);
                    if (powershell.HasExited)
                    {
                        throw new InvalidOperationException("同步窗口已经关闭。");
                    }
                    InjectPrompt(input, runtime);
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

    private static NamedPipeServerStream CreatePipeServer(string pipeName, bool elevated)
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
        var pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            16_384,
            16_384,
            security);
        if (elevated)
        {
            SetMediumIntegrityLabel(pipe);
        }
        return pipe;
    }

    private static void SetMediumIntegrityLabel(NamedPipeServerStream pipe)
    {
        const string mediumIntegritySddl = "S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                mediumIntegritySddl,
                1,
                out var securityDescriptor,
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "无法创建同步管道的完整性标签。");
        }
        try
        {
            if (!GetSecurityDescriptorSacl(
                    securityDescriptor,
                    out var saclPresent,
                    out var sacl,
                    out _) ||
                !saclPresent ||
                sacl == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法读取同步管道的完整性标签。");
            }
            const uint labelSecurityInformation = 0x00000010;
            const int kernelObject = 6;
            var result = SetSecurityInfo(
                pipe.SafePipeHandle.DangerousGetHandle(),
                kernelObject,
                labelSecurityInformation,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                sacl);
            if (result != 0)
            {
                throw new Win32Exception(
                    (int)result,
                    "无法允许桌面助手连接管理员 Codex 窗口。");
            }
        }
        finally
        {
            LocalFree(securityDescriptor);
        }
    }

    private static void InjectPrompt(TerminalInputRequest input, RuntimeProfile runtime)
    {
        var inputHandle = GetStdHandle(StdInputHandle);
        if (inputHandle == IntPtr.Zero || inputHandle == new IntPtr(-1))
        {
            throw new InvalidOperationException("无法访问同步窗口输入缓冲区。");
        }

        var records = new List<InputRecord>(input.Prompt.Length * 2);
        foreach (var character in input.Prompt)
        {
            records.Add(CreateKeyRecord(character, keyDown: true));
            records.Add(CreateKeyRecord(character, keyDown: false));
        }
        WriteInputRecords(inputHandle, records);

        // Both TUIs treat a rapid burst as pasted text. Wait for the paste detector before
        // submitting. Codex uses Tab for an explicit next-turn queue; Claude Code queues
        // typed input with Enter while a turn is running.
        Thread.Sleep(600);
        var useTab = runtime.QueueWithTab && input.SubmitMode == TerminalSubmitMode.Queue;
        var submitCharacter = useTab ? '\t' : '\r';
        var submitVirtualKey = useTab
            ? VirtualKeyTab
            : VirtualKeyReturn;
        var submitScanCode = useTab
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
            throw new InvalidOperationException("向同步窗口写入回复失败。");
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

    private static string ReadControlToken(string bridgeRoot)
    {
        var tokenPath = Path.Combine(bridgeRoot, "data", "control-token.json");
        var value = JsonSerializer.Deserialize<ControlTokenFile>(File.ReadAllText(tokenPath));
        if (string.IsNullOrWhiteSpace(value?.Token) ||
            value.Token.Length != 64 ||
            value.Token.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("本机控制令牌缺失或格式无效。");
        }
        return value.Token;
    }

    private sealed class ControlTokenFile
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = "";
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

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSecurityDescriptorRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorSacl(
        IntPtr securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool saclPresent,
        out IntPtr sacl,
        [MarshalAs(UnmanagedType.Bool)] out bool saclDefaulted);

    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(
        IntPtr handle,
        int objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
