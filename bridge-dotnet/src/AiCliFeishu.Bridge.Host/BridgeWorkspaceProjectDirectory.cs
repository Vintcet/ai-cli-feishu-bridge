using System.Text;

namespace AiCliFeishu.Bridge.Host;

internal sealed record BridgePreparedProjectDirectory(
    string WorkspaceRoot,
    string Cwd,
    bool Created);

internal sealed class BridgeProjectDirectoryException(
    string message,
    Exception? innerException = null) : IOException(message, innerException);

internal static class BridgeWorkspaceProjectDirectory
{
    private const int MaximumProjectNameLength = 80;
    private static readonly char[] forbiddenNameCharacters =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly HashSet<string> windowsReservedNames =
        new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
            "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static string? NormalizeAndValidateName(
        string? value,
        out string? validationError)
    {
        string normalized;
        try
        {
            normalized = value?.Normalize(NormalizationForm.FormC) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            validationError = "项目名包含无效字符。";
            return null;
        }

        if (normalized.Any(char.IsControl))
        {
            validationError = "不能包含斜杠、盘符或 Windows 文件名保留字符。";
            return null;
        }
        if (normalized.EndsWith(' '))
        {
            validationError = "不能以句点或空格结尾。";
            return null;
        }
        var name = normalized.Trim();
        if (name.Length == 0)
        {
            validationError = "项目名不能为空。";
            return null;
        }
        if (name.EnumerateRunes().Count() > MaximumProjectNameLength)
        {
            validationError = $"项目名最多 {MaximumProjectNameLength} 个字符。";
            return null;
        }
        if (name is "." or "..")
        {
            validationError = "不能使用点目录。";
            return null;
        }
        if (name.IndexOfAny(forbiddenNameCharacters) >= 0)
        {
            validationError = "不能包含斜杠、盘符或 Windows 文件名保留字符。";
            return null;
        }
        if (name[^1] is '.' or ' ')
        {
            validationError = "不能以句点或空格结尾。";
            return null;
        }
        var stem = name.Split('.', 2)[0];
        if (windowsReservedNames.Contains(stem))
        {
            validationError = "不能使用 Windows 保留名称。";
            return null;
        }

        validationError = null;
        return name;
    }

    public static BridgePreparedProjectDirectory Prepare(
        string workspaceRoot,
        string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        string? createdPath = null;
        try
        {
            var requestedRoot = Path.GetFullPath(workspaceRoot.Trim());
            Directory.CreateDirectory(requestedRoot);
            var resolvedRoot = ResolveDirectory(requestedRoot);
            var projectPath = Path.Combine(resolvedRoot, projectName);

            if (!PathExists(projectPath))
            {
                if (CreateDirectoryAtomically(resolvedRoot, projectPath))
                {
                    createdPath = projectPath;
                }
            }

            var attributes = File.GetAttributes(projectPath);
            if (!attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new BridgeProjectDirectoryException(
                    "同名路径不是可用的普通文件夹。");
            }

            var resolvedProject = ResolveDirectory(projectPath);
            if (!IsStrictChild(resolvedRoot, resolvedProject))
            {
                throw new BridgeProjectDirectoryException(
                    "项目目录超出了默认工作区。");
            }
            return new(
                resolvedRoot,
                resolvedProject,
                createdPath is not null);
        }
        catch (BridgeProjectDirectoryException)
        {
            DeleteEmptyDirectory(createdPath);
            throw;
        }
        catch (Exception error) when (error is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            DeleteEmptyDirectory(createdPath);
            throw new BridgeProjectDirectoryException(
                "无法准备默认工作区中的项目目录。",
                error);
        }
    }

    public static void Rollback(BridgePreparedProjectDirectory prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!prepared.Created)
        {
            return;
        }

        try
        {
            var attributes = File.GetAttributes(prepared.Cwd);
            if (!attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }
            var resolved = ResolveDirectory(prepared.Cwd);
            if (!PathEquals(resolved, prepared.Cwd) ||
                !IsStrictChild(prepared.WorkspaceRoot, resolved))
            {
                return;
            }
            Directory.Delete(resolved, recursive: false);
        }
        catch (Exception error) when (error is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            // Rollback is best effort and never replaces the dispatch failure.
        }
    }

    private static bool CreateDirectoryAtomically(string root, string projectPath)
    {
        var stagingPath = Path.Combine(
            root,
            $".ai-cli-feishu-new-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stagingPath);
        try
        {
            Directory.Move(stagingPath, projectPath);
            return true;
        }
        catch (IOException) when (PathExists(projectPath))
        {
            return false;
        }
        finally
        {
            DeleteEmptyDirectory(stagingPath);
        }
    }

    private static string ResolveDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new IOException("路径缺少文件系统根目录。");
        }

        var current = root;
        var relative = fullPath[root.Length..];
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, segment);
            var attributes = File.GetAttributes(candidate);
            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                throw new IOException("路径包含非目录节点。");
            }
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                var target = new DirectoryInfo(candidate).ResolveLinkTarget(
                    returnFinalTarget: true)
                    ?? throw new IOException("无法解析目录重解析点。");
                current = Path.GetFullPath(target.FullName);
            }
            else
            {
                current = candidate;
            }
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static bool IsStrictChild(string root, string target)
    {
        var relative = Path.GetRelativePath(root, target);
        return relative != "." &&
            relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool PathExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void DeleteEmptyDirectory(string? path)
    {
        if (path is null)
        {
            return;
        }
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (Exception error) when (error is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
        }
    }
}
