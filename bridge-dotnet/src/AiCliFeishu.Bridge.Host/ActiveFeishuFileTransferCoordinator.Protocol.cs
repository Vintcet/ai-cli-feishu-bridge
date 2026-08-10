using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

internal static class BridgeFileTransferProtocol
{
    private const int MaximumFiles = 3;
    private static readonly Regex FileDirective = new(
        @"^\s*BRIDGE_SEND_FILE:\s*(.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ExcessBlankLines = new(
        @"\n{3,}",
        RegexOptions.CultureInvariant);
    private static readonly IReadOnlySet<string> AllowedExtensions =
        new HashSet<string>(
        [
            ".bmp", ".csv", ".doc", ".docx", ".gif", ".ico", ".jpeg",
            ".jpg", ".json", ".log", ".md", ".mp4", ".pdf", ".png",
            ".ppt", ".pptx", ".tif", ".tiff", ".txt", ".webp", ".xls",
            ".xlsx", ".zip",
        ],
        StringComparer.Ordinal);

    public static string AppendAttachmentsToPrompt(
        string prompt,
        IReadOnlyList<BridgeSavedAttachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count == 0)
        {
            return prompt;
        }
        var files = string.Join(
            '；',
            attachments.Select((attachment, index) =>
                $"{index + 1}. {attachment.AbsolutePath}"));
        return $"飞书附件已保存到本机：{files}。" +
            $"请使用适合的文件读取工具处理这些文件。用户要求：{prompt}";
    }

    public static string AddFileReturnInstruction(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return prompt +
            "\n\n用户明确要求把最终文件发回飞书。请把生成文件保存到当前项目目录内，" +
            "并在最终回复中为每个文件单独输出一行：BRIDGE_SEND_FILE: 绝对路径。" +
            "最多 3 个文件；不要声明项目目录外的文件。";
    }

    public static BridgeFileDirectiveResult ExtractDirectives(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var paths = new List<string>(MaximumFiles);
        var kept = new List<string>();
        var normalized = message.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        foreach (var line in normalized.Split('\n'))
        {
            var match = FileDirective.Match(line);
            if (!match.Success)
            {
                kept.Add(line);
                continue;
            }
            var candidate = StripWrappingQuotes(match.Groups[1].Value.Trim());
            if (candidate.Length != 0 &&
                paths.Count < MaximumFiles &&
                !paths.Contains(candidate, StringComparer.Ordinal))
            {
                paths.Add(candidate);
            }
        }
        var display = ExcessBlankLines.Replace(string.Join('\n', kept), "\n\n").Trim();
        return new(display, paths);
    }

    public static Task<ValidatedBridgeFile> ValidateFileAsync(
        string candidate,
        string cwd,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }
        if (!Path.IsPathFullyQualified(candidate))
        {
            throw new InvalidDataException("回传路径不是绝对路径。");
        }

        var resolvedFile = ResolveExistingPath(candidate);
        var resolvedRoot = ResolveExistingPath(cwd);
        if (!Directory.Exists(resolvedRoot))
        {
            throw new InvalidDataException("当前项目目录不可用。");
        }
        if (!IsInside(resolvedRoot, resolvedFile))
        {
            throw new InvalidDataException("回传文件不在当前项目目录内。");
        }
        var extension = Path.GetExtension(resolvedFile).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
        {
            throw new InvalidDataException(
                $"不允许回传 {(extension.Length == 0 ? "无扩展名" : extension)} 文件。");
        }
        var info = new FileInfo(resolvedFile);
        if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException("回传路径不是普通文件。");
        }
        if (info.Length <= 0 || info.Length > maxBytes)
        {
            throw new InvalidDataException(
                $"回传文件为空或超过 {FormatMegabytes(maxBytes)}。");
        }
        return Task.FromResult(new ValidatedBridgeFile(resolvedFile, info.Length));
    }

    private static string ResolveExistingPath(string path) =>
        ResolveExistingPath(
            Path.GetFullPath(path),
            new HashSet<string>(PathComparer()),
            0);

    private static string ResolveExistingPath(
        string fullPath,
        HashSet<string> resolving,
        int depth)
    {
        if (depth > 32 || !resolving.Add(fullPath))
        {
            throw new IOException("回传路径包含无法解析的链接循环。");
        }
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new InvalidDataException("回传路径不是绝对路径。");
        }
        var current = root;
        var relative = fullPath[root.Length..];
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var next = Path.Combine(current, segment);
            FileSystemInfo info;
            if (Directory.Exists(next))
            {
                info = new DirectoryInfo(next);
            }
            else if (File.Exists(next))
            {
                info = new FileInfo(next);
            }
            else
            {
                throw new FileNotFoundException("回传路径不存在。", next);
            }
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            current = target is null
                ? Path.GetFullPath(next)
                : ResolveExistingPath(
                    Path.GetFullPath(target.FullName),
                    resolving,
                    depth + 1);
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static bool IsInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative.Length != 0 &&
            !Path.IsPathRooted(relative) &&
            !string.Equals(relative, "..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string StripWrappingQuotes(string value)
    {
        if (value.Length >= 2 &&
            (value[0] == '"' && value[^1] == '"' ||
             value[0] == '\'' && value[^1] == '\''))
        {
            return value[1..^1].Trim();
        }
        return value;
    }

    private static string FormatMegabytes(long bytes)
    {
        var mebibytes = bytes / (1024d * 1024d);
        return $"{Math.Floor(mebibytes + 0.5):0} MiB";
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal sealed record ValidatedBridgeFile(string Path, long Size);
}
