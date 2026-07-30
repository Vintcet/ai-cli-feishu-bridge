using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodexFeishuControl;

internal static class CodexArgumentParser
{
    public static IReadOnlyList<string> Parse(string? rawArguments)
    {
        if (string.IsNullOrWhiteSpace(rawArguments))
        {
            return [];
        }
        if (rawArguments.Length > 4_000 || rawArguments.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new InvalidOperationException("Codex 启动参数无效或过长。");
        }

        var argumentVector = CommandLineToArgvW($"codex.exe {rawArguments}", out var argumentCount);
        if (argumentVector == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法解析 Codex 启动参数。");
        }

        try
        {
            var result = new List<string>(Math.Max(0, argumentCount - 1));
            for (var index = 1; index < argumentCount; index++)
            {
                var valuePointer = Marshal.ReadIntPtr(argumentVector, index * IntPtr.Size);
                result.Add(Marshal.PtrToStringUni(valuePointer) ?? "");
            }

            if (result.Count > 0 && IsCodexCommand(result[0]))
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
