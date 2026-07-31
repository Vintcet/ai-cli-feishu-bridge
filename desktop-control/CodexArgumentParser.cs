using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodexFeishuControl;

internal static class CodexArgumentParser
{
    public static IReadOnlyList<string> Parse(string? rawArguments) =>
        ParseArguments(
            "codex",
            "Codex 启动参数无效或过长。",
            "无法解析 Codex 启动参数。",
            rawArguments,
            IsCodexCommand);

    public static IReadOnlyList<string> ParseOpenCode(string? rawArguments) =>
        ParseArguments(
            "opencode",
            "opencode 启动参数无效或过长。",
            "无法解析 opencode 启动参数。",
            rawArguments,
            IsOpenCodeCommand);

    private static IReadOnlyList<string> ParseArguments(
        string toolCommand,
        string invalidMessage,
        string parseFailureMessage,
        string? rawArguments,
        Func<string, bool> isToolCommand)
    {
        if (string.IsNullOrWhiteSpace(rawArguments))
        {
            return [];
        }
        if (rawArguments.Length > 4_000 || rawArguments.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new InvalidOperationException(invalidMessage);
        }

        var argumentVector = CommandLineToArgvW($"{toolCommand}.exe {rawArguments}", out var argumentCount);
        if (argumentVector == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), parseFailureMessage);
        }

        try
        {
            var result = new List<string>(Math.Max(0, argumentCount - 1));
            for (var index = 1; index < argumentCount; index++)
            {
                var valuePointer = Marshal.ReadIntPtr(argumentVector, index * IntPtr.Size);
                result.Add(Marshal.PtrToStringUni(valuePointer) ?? "");
            }

            if (result.Count > 0 && isToolCommand(result[0]))
            {
                result.RemoveAt(0);
            }
            return result;
        }
        finally
        {
            LocalFree(argumentVector);
        }
    }

    private static bool IsCodexCommand(string value)
    {
        var fileName = Path.GetFileName(value);
        return fileName.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("codex.cmd", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("codex.ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOpenCodeCommand(string value)
    {
        var fileName = Path.GetFileName(value);
        return fileName.Equals("opencode", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("opencode.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("opencode.cmd", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
