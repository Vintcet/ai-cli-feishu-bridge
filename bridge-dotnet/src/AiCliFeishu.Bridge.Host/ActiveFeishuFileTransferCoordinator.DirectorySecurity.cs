using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class LocalAttachmentStore
{
    private static void EnsureSafeDirectoryRoot(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var missing = new Stack<string>();
        var current = fullPath;
        while (!Directory.Exists(current))
        {
            missing.Push(current);
            var parent = Directory.GetParent(current);
            if (parent is null || PathComparer().Equals(parent.FullName, current))
            {
                throw new DirectoryNotFoundException(
                    $"附件暂存区的父目录不存在：{current}");
            }
            current = parent.FullName;
        }

        EnsureNotReparseDirectory(current);
        while (missing.Count > 0)
        {
            current = missing.Pop();
            Directory.CreateDirectory(current);
            EnsureNotReparseDirectory(current);
        }
    }

    private static void EnsureNotReparseDirectory(string directory)
    {
        var info = new DirectoryInfo(directory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("附件暂存区不能位于重解析目录中。");
        }
    }

    private static DateTimeOffset SafeCutoff(
        DateTimeOffset now,
        TimeSpan ttl) =>
        ttl >= now - DateTimeOffset.MinValue
            ? DateTimeOffset.MinValue
            : now - ttl;

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static string FormatBytes(long value)
    {
        if (value >= 1024L * 1024 * 1024)
        {
            return $"{value / (1024d * 1024 * 1024):0.0} GiB";
        }
        if (value >= 1024L * 1024)
        {
            return $"{value / (1024d * 1024):0.0} MiB";
        }
        if (value >= 1024)
        {
            return $"{value / 1024d:0.0} KiB";
        }
        return $"{value} B";
    }

    private sealed class DirectoryUsage
    {
        public int FileCount { get; set; }

        public long TotalBytes { get; set; }
    }
}
