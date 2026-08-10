using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiCliFeishu.Bridge.Host;

internal sealed record ApprovalRiskAssessment(string Level, string Reason);

internal static partial class ApprovalRiskClassifier
{
    private static readonly HashSet<string> assessableTools = new(
        [
            "bash", "shell", "shell_command", "powershell", "pwsh", "cmd",
            "read", "write", "edit", "multiedit", "apply_patch", "patch",
            "glob", "grep", "search", "list", "ls",
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> shellTools = new(
        ["bash", "shell", "shell_command", "powershell", "pwsh", "cmd"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> explicitPathTools = new(
        ["read", "write", "edit", "multiedit"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> patchTools = new(
        ["apply_patch", "patch"],
        StringComparer.OrdinalIgnoreCase);

    public static ApprovalRiskAssessment Assess(
        string toolName,
        string toolPreview,
        string cwd)
    {
        var normalizedTool = toolName.Trim();
        if (HighRiskTool().IsMatch(normalizedTool))
        {
            return High("工具本身具有删除、外发、终止或系统控制能力");
        }
        if (!assessableTools.Contains(normalizedTool))
        {
            return High("工具不在可自动审批的明确白名单中");
        }
        if (toolPreview.Contains("已截断", StringComparison.Ordinal) ||
            toolPreview.Length > 64 * 1024)
        {
            return High("请求参数不完整或过长，无法确认其安全性");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(toolPreview);
        }
        catch (JsonException)
        {
            return High("请求参数不是完整 JSON，无法确认其安全性");
        }
        using (document)
        {
            var collected = new CollectedInput();
            Collect(document.RootElement, null, collected, 0);
            if (collected.IncompleteReason is not null)
            {
                return High(collected.IncompleteReason);
            }
            var searchable = $"{normalizedTool}\n{string.Join('\n', collected.Text)}";
            if (HighRiskContent().Match(searchable) is { Success: true } dangerous)
            {
                return High(DangerReason(dangerous.Value));
            }
            foreach (var path in collected.Paths)
            {
                if (UnsafeExplicitPath(path, cwd, out var reason))
                {
                    return High(reason);
                }
            }
            if (explicitPathTools.Contains(normalizedTool) && collected.Paths.Count == 0)
            {
                return High("请求没有提供可验证的项目内路径");
            }
            if (patchTools.Contains(normalizedTool))
            {
                var patchPaths = collected.Text
                    .SelectMany(ExtractPatchPaths)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (patchPaths.Length == 0)
                {
                    return High("补丁没有提供可验证的项目内文件路径");
                }
                foreach (var path in patchPaths)
                {
                    if (UnsafeExplicitPath(path, cwd, out var reason))
                    {
                        return High(reason);
                    }
                }
            }
            if (shellTools.Contains(normalizedTool) &&
                (collected.Commands.Count == 0 ||
                 collected.Commands.Any(command => !IsLowRiskCommand(command, cwd))))
            {
                return High("Shell 命令不在可自动审批的明确白名单中");
            }
        }
        return new("low", "未命中高风险命令或路径规则");
    }

    private static void Collect(
        JsonElement value,
        string? key,
        CollectedInput collected,
        int depth)
    {
        if (++collected.Nodes > 10_000)
        {
            collected.IncompleteReason ??= "请求参数过多，无法完整确认其安全性";
            return;
        }
        if (depth > 12)
        {
            collected.IncompleteReason ??= "请求参数嵌套过深，无法完整确认其安全性";
            return;
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString() ?? string.Empty;
                if (collected.Length + text.Length > 64 * 1024)
                {
                    collected.IncompleteReason ??= "请求参数过长，无法完整确认其安全性";
                    return;
                }
                collected.Length += text.Length;
                collected.Text.Add(text);
                if (key is not null && PathKey().IsMatch(NormalizeKey(key)))
                {
                    collected.Paths.Add(text);
                }
                if (key is not null && CommandKey().IsMatch(NormalizeKey(key)))
                {
                    collected.Commands.Add(text);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    Collect(item, key, collected, depth + 1);
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    Collect(property.Value, property.Name, collected, depth + 1);
                }
                break;
        }
    }

    private static bool IsLowRiskCommand(string value, string cwd)
    {
        var command = value.Trim();
        if (command.Length is 0 or > 8_192 ||
            ShellMetacharacters().IsMatch(command) ||
            EnvironmentExpansion().IsMatch(command))
        {
            return false;
        }
        if (!TryTokenizeCommand(command, out var words) ||
            words.Count == 0 ||
            words.Any(word => UnsafeCommandArgument(word, cwd)))
        {
            return false;
        }
        var executable = words[0].ToLowerInvariant();
        var subcommand = words.ElementAtOrDefault(1)?.ToLowerInvariant();
        return executable switch
        {
            // Build and test commands execute project-controlled code (MSBuild targets,
            // package scripts, test initializers, compiler plugins, and similar hooks).
            // They therefore require an explicit approval even when their verb sounds safe.
            "git" => IsLowRiskGitCommand(words, subcommand),
            "rg" => !words.Any(word =>
                word.StartsWith("--pre", StringComparison.OrdinalIgnoreCase)),
            "get-content" or "get-childitem" or "get-child-item" or
                "ls" or "dir" or "type" => true,
            "get-location" or "pwd" => words.Count == 1,
            _ => false,
        };
    }

    private static bool IsLowRiskGitCommand(
        IReadOnlyList<string> words,
        string? subcommand)
    {
        if (subcommand is not (
                "status" or "diff" or "log" or "show" or "rev-parse" or "ls-files"))
        {
            return false;
        }
        return !words.Any(word => word.Equals("--no-index", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("--ext-diff", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("--textconv", StringComparison.OrdinalIgnoreCase) ||
            word.StartsWith("--git-dir", StringComparison.OrdinalIgnoreCase) ||
            word.StartsWith("--work-tree", StringComparison.OrdinalIgnoreCase) ||
            word.StartsWith("--config-env", StringComparison.OrdinalIgnoreCase) ||
            word.StartsWith("--output", StringComparison.OrdinalIgnoreCase) ||
            word.Equals("-c", StringComparison.OrdinalIgnoreCase));
    }

    private static bool UnsafeCommandArgument(string word, string cwd)
    {
        foreach (var candidate in CommandArgumentCandidates(word))
        {
            if (candidate.Length == 0)
            {
                continue;
            }
            if (UriScheme().IsMatch(candidate) ||
                ProviderPath().IsMatch(candidate) && !DrivePath().IsMatch(candidate) ||
                SensitivePath().IsMatch(candidate))
            {
                return true;
            }
            if (IsOutsideWorkspace(candidate, cwd))
            {
                return true;
            }
        }
        return false;
    }

    private static bool UnsafeExplicitPath(
        string path,
        string cwd,
        out string reason)
    {
        if (SensitivePath().IsMatch(path))
        {
            reason = "请求会访问凭据、密钥或敏感配置";
            return true;
        }
        if (IsOutsideWorkspace(path, cwd))
        {
            reason = "请求会访问当前项目目录之外的路径";
            return true;
        }
        reason = string.Empty;
        return false;
    }

    private static IEnumerable<string> ExtractPatchPaths(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var match = PatchFileHeader().Match(line);
            if (match.Success)
            {
                yield return match.Groups[1].Value.Trim();
            }
        }
    }

    private static IEnumerable<string> CommandArgumentCandidates(string word)
    {
        var value = word.Trim().Trim('"', '\'');
        if (value.Length == 0)
        {
            yield break;
        }
        yield return value;

        var equals = value.IndexOf('=');
        if (equals >= 0 && equals + 1 < value.Length)
        {
            yield return value[(equals + 1)..];
        }
        if (value[0] is '-' or '/')
        {
            var colon = value.IndexOf(':', 1);
            if (colon >= 0 && colon + 1 < value.Length)
            {
                yield return value[(colon + 1)..];
            }
        }
    }

    private static bool TryTokenizeCommand(string command, out List<string> words)
    {
        words = [];
        var current = new System.Text.StringBuilder();
        char quote = '\0';
        foreach (var character in command)
        {
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(character);
                }
                continue;
            }
            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }
            if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(character);
        }
        if (quote != '\0')
        {
            words = [];
            return false;
        }
        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }
        return true;
    }

    private static bool IsOutsideWorkspace(string candidate, string cwd)
    {
        var value = candidate.Trim();
        if (value.Length == 0 || value.Contains('\n') || UriScheme().IsMatch(value))
        {
            return false;
        }
        try
        {
            var root = Path.GetFullPath(cwd);
            var resolved = Path.GetFullPath(value, root);
            var relative = Path.GetRelativePath(root, resolved);
            return relative == ".." ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative);
        }
        catch
        {
            return true;
        }
    }

    private static string NormalizeKey(string key) =>
        Regex.Replace(key, @"[^a-zA-Z0-9]+", "_").ToLowerInvariant();

    private static ApprovalRiskAssessment High(string reason) => new("high", reason);

    private static string DangerReason(string value) => value.Contains("git", StringComparison.OrdinalIgnoreCase)
        ? "命令包含高影响 Git 操作"
        : value.Contains("curl", StringComparison.OrdinalIgnoreCase) ||
          value.Contains("wget", StringComparison.OrdinalIgnoreCase)
            ? "命令会访问网络或远程主机"
            : "请求包含删除、外发、提权或系统修改操作";

    private sealed class CollectedInput
    {
        public List<string> Text { get; } = [];
        public List<string> Paths { get; } = [];
        public List<string> Commands { get; } = [];
        public int Length { get; set; }
        public int Nodes { get; set; }
        public string? IncompleteReason { get; set; }
    }

    [GeneratedRegex(@"(?:delete|remove|erase|destroy|format|wipe|kill|terminate|shutdown|reboot|publish|deploy|release|upload|send[_-]?(?:message|mail)|purchase|payment|external[_-]?directory|web[_-]?(?:fetch|search)|network)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HighRiskTool();

    [GeneratedRegex(@"(?:^|[\s;&|(""'`])(?:rm|rmdir|del|erase|remove-item|clear-content|shred|unlink)(?=$|[\s;&|)""'`])|\*\*\*\s+delete\s+file\s*:|\bgit\s+(?:reset\s+--hard|clean\b|checkout\s+--|restore\b|branch\s+-D\b|push\b|rebase\b)|\b(?:curl|wget|invoke-webrequest|invoke-restmethod|ssh|scp|sftp|rsync|ftp|kubectl|helm|terraform|ansible|aws|az|gcloud|docker|podman|sudo|runas|chmod|chown|icacls|takeown|shutdown|reboot|format|mkfs|diskpart|taskkill|stop-process|systemctl|schtasks)\b|\b(?:drop\s+(?:database|table)|truncate\s+table|delete\s+from)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HighRiskContent();

    [GeneratedRegex(@"(?:^|[\\/])(?:\.ssh|\.aws|\.azure|\.kube|\.gnupg|\.bridge-store|credentials?|secrets?|private[_-]?keys?|control[_-]?tokens?|\.env)(?:[\\/\s.]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitivePath();

    [GeneratedRegex(@"(?:^|_)(?:path|file|filename|directory|dir|cwd|target|destination|source|resource|resources)$", RegexOptions.CultureInvariant)]
    private static partial Regex PathKey();

    [GeneratedRegex(@"(?:^|_)(?:command|cmd|script|resource|resources|pattern|patterns)$", RegexOptions.CultureInvariant)]
    private static partial Regex CommandKey();

    [GeneratedRegex(@"[\r\n;&|<>`(){}\[\]]|\$\{|::|%comspec%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShellMetacharacters();

    [GeneratedRegex(@"\$[a-zA-Z_]|%[a-zA-Z_][a-zA-Z0-9_]*%|![a-zA-Z_][a-zA-Z0-9_]*!|(?:^|\s)~(?:[\\/\s]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentExpansion();

    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProviderPath();

    [GeneratedRegex(@"^[a-z]:[\\/]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DrivePath();

    [GeneratedRegex(@"^\*\*\*\s+(?:(?:Add|Update|Delete)\s+File|Move\s+to):\s+(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PatchFileHeader();

    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriScheme();
}
