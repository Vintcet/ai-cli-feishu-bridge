using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodexFeishuControl;

internal static class RuntimeArgumentParser
{
    public static IReadOnlyList<string> ReadRepeatedArguments(
        IReadOnlyList<string> arguments,
        string name)
    {
        var result = new List<string>();
        for (var index = 0; index < arguments.Count - 1; index += 1)
        {
            if (!string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            result.Add(arguments[index + 1]);
            index += 1;
        }
        return result;
    }

    public static IReadOnlyList<string> Parse(
        RuntimeProfile runtime,
        string? rawArguments)
    {
        if (string.IsNullOrWhiteSpace(rawArguments))
        {
            return [];
        }
        if (rawArguments.Length > 4_000 || rawArguments.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new InvalidOperationException($"{runtime.DisplayName} 启动参数无效或过长。");
        }

        var argumentVector = CommandLineToArgvW(
            $"{runtime.CommandName}.exe {rawArguments}",
            out var argumentCount);
        if (argumentVector == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"无法解析 {runtime.DisplayName} 启动参数。");
        }

        try
        {
            var result = new List<string>(Math.Max(0, argumentCount - 1));
            for (var index = 1; index < argumentCount; index++)
            {
                var valuePointer = Marshal.ReadIntPtr(argumentVector, index * IntPtr.Size);
                result.Add(Marshal.PtrToStringUni(valuePointer) ?? "");
            }

            if (result.Count > 0 && runtime.MatchesCommand(result[0]))
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
