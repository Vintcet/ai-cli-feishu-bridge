using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiCliFeishu.Bridge.Host;

internal sealed record ApprovalRiskAssessment(string Level, string Reason);

internal static class ApprovalRiskLevels
{
    // Auto-approved by the strict tier: matched an explicit read-only allowlist.
    public const string Low = "low";

    // Auto-approved by the relaxed tier only: inspectable and not irreversible, but
    // outside the strict allowlist (builds, tests, dependency installs, edits...).
    public const string Medium = "medium";

    // Never auto-approved by any tier: irreversible, escapes the workspace, or could
    // not be inspected at all.
    public const string Critical = "critical";

    // Requests classified before the relaxed tier existed only carry "high", which
    // must keep failing closed.
    public const string LegacyHigh = "high";

    public static bool IsAutoApprovable(string? level, bool relaxed) =>
        string.Equals(level, Low, StringComparison.Ordinal) ||
        relaxed && string.Equals(level, Medium, StringComparison.Ordinal);
}

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
            return Critical("工具本身具有删除、外发、提权或系统控制能力");
        }
        // An unrecognized tool cannot be reasoned about: there is no way to tell which
        // of its arguments are paths or commands, so none of the content rules below
        // apply to it. OpenCode approvals in particular arrive with no tool name and no
        // argument preview at all, which would otherwise be waved through by the relaxed
        // tier as "nothing matched".
        if (!assessableTools.Contains(normalizedTool))
        {
            return Critical("工具不在可评估白名单中，无法确认其能力");
        }

        // Anything that cannot be inspected stays critical in every tier. This is the
        // load-bearing rule for the relaxed tier: it flips the default to allow, so a
        // truncated or unparsable payload would otherwise be the way to smuggle an
        // irreversible command past every content rule below.
        if (toolPreview.Contains("已截断", StringComparison.Ordinal) ||
            toolPreview.Length > 64 * 1024)
        {
            return Critical("请求参数不完整或过长，无法确认其安全性");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(toolPreview);
        }
        catch (JsonException)
        {
            return Critical("请求参数不是完整 JSON，无法确认其安全性");
        }
        using (document)
        {
            var collected = new CollectedInput();
            Collect(document.RootElement, null, collected, 0);
            if (collected.IncompleteReason is not null)
            {
                return Critical(collected.IncompleteReason);
            }
            var searchable = $"{normalizedTool}\n{string.Join('\n', collected.Text)}";
            if (IrreversibleContent().Match(searchable) is { Success: true } irreversible)
            {
                return Critical(DangerReason(irreversible.Value));
            }
            if (RemoteScriptExecution().IsMatch(searchable))
            {
                return Critical("命令会下载并直接执行远程脚本");
            }
            if (OutboundDataTransfer().IsMatch(searchable))
            {
                return Critical("命令会把数据外发到远程主机");
            }
            foreach (var path in collected.Paths)
            {
                if (UnsafeExplicitPath(path, cwd, out var pathReason))
                {
                    return Critical(pathReason);
                }
            }
            if (patchTools.Contains(normalizedTool) &&
                CriticalPatchReason(collected, cwd) is { } patchReason)
            {
                return Critical(patchReason);
            }
            // Shell arguments are only scanned for paths inside the strict allowlist,
            // so the relaxed tier has to re-check every token here; otherwise a
            // redirect such as `echo x > C:\Windows\...` would pass as medium.
            if (shellTools.Contains(normalizedTool) &&
                CriticalCommandPathReason(collected, cwd) is { } commandReason)
            {
                return Critical(commandReason);
            }

            // From here the request is inspectable and reversible. It is only "low"
            // when it matches the original strict allowlist unchanged.
            if (explicitPathTools.Contains(normalizedTool) && collected.Paths.Count == 0)
            {
                return Medium("请求没有提供可验证的项目内路径");
            }
            if (patchTools.Contains(normalizedTool) && PatchPaths(collected).Length == 0)
            {
                return Medium("补丁没有提供可验证的项目内文件路径");
            }
            if (shellTools.Contains(normalizedTool) &&
                (collected.Commands.Count == 0 ||
                 collected.Commands.Any(command => !IsLowRiskCommand(command, cwd))))
            {
                return Medium("Shell 命令不在严格白名单中，但未命中不可逆操作规则");
            }
        }
        return new(ApprovalRiskLevels.Low, "未命中高风险命令或路径规则");
    }

    private static string? CriticalPatchReason(CollectedInput collected, string cwd)
    {
        foreach (var path in PatchPaths(collected))
        {
            if (UnsafeExplicitPath(path, cwd, out var reason))
            {
                return reason;
            }
        }
        return null;
    }

    private static string? CriticalCommandPathReason(CollectedInput collected, string cwd)
    {
        foreach (var command in collected.Commands)
        {
            if (!TryTokenizeCommand(command, out var words))
            {
                return "命令引号不完整，无法确认其安全性";
            }
            foreach (var word in words)
            {
                // A variable reference resolves at run time, so the real target cannot be
                // checked here. `Env:X` is already caught as a provider path, but `$env:X`
                // and `%X%` would otherwise slip through and could name a credential.
                if (EnvironmentExpansion().IsMatch(word))
                {
                    return "命令通过变量引用目标，无法确认其实际路径";
                }
                // A Windows switch such as /pid or /s is not a path, but GetFullPath
                // resolves it against the drive root and it would look like an escape.
                if (IsWindowsCommandSwitch(word))
                {
                    continue;
                }
                if (UnsafeCommandArgument(word, cwd))
                {
                    return SensitivePath().IsMatch(word)
                        ? "请求会访问凭据、密钥或敏感配置"
                        : "请求会访问当前项目目录之外的路径";
                }
            }
        }
        return null;
    }

    private static bool IsWindowsCommandSwitch(string word) =>
        word.Length >= 2 &&
        word[0] == '/' &&
        char.IsAsciiLetter(word[1]) &&
        word.IndexOfAny(['/', '\\'], 1) < 0;

    private static string[] PatchPaths(CollectedInput collected) => collected.Text
        .SelectMany(ExtractPatchPaths)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

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

    private static ApprovalRiskAssessment Critical(string reason) =>
        new(ApprovalRiskLevels.Critical, reason);

    private static ApprovalRiskAssessment Medium(string reason) =>
        new(ApprovalRiskLevels.Medium, reason);

    private static string DangerReason(string value) => value.Contains("git", StringComparison.OrdinalIgnoreCase)
        ? "命令包含不可逆的 Git 操作"
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

    // Tool names that describe an irreversible or outward-facing capability. `kill` and
    // `terminate` are deliberately absent: stopping a dev server is routine and
    // recoverable. `web_fetch`/`web_search` are absent too - they read, and the
    // outbound rules below still catch attempts to push data out through them.
    [GeneratedRegex(@"(?:delete|remove|erase|destroy|format|wipe|shutdown|reboot|publish|deploy|release|upload|send[_-]?(?:message|mail)|purchase|payment|external[_-]?directory)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HighRiskTool();

    // Irreversible operations: destroy data, rewrite published history, change
    // permissions or system state, or act on shared infrastructure. `git rebase` and
    // `commit --amend` are absent (reflog and ORIG_HEAD make them recoverable), and
    // `git push` only counts when it force-writes. Process termination is absent.
    [GeneratedRegex(@"(?:^|[\s;&|(""'`])(?:rm|rmdir|del|erase|remove-item|clear-content|shred|unlink|dd)(?=$|[\s;&|)""'`])|\*\*\*\s+delete\s+file\s*:|\bgit\s+(?:reset\s+--hard|clean\b|checkout\s+--|restore\b|branch\s+-D\b|filter-branch\b|filter-repo\b|reflog\s+expire\b)|\bgit\s+push\b[^\r\n]*(?:--force|--force-with-lease|(?:^|\s)-f(?=$|\s))|\b(?:ssh|scp|sftp|rsync|ftp|kubectl|helm|terraform|ansible|aws|az|gcloud|docker|podman|sudo|runas|chmod|chown|icacls|takeown|shutdown|reboot|format|mkfs|diskpart|systemctl|schtasks)\b|\b(?:drop\s+(?:database|table)|truncate\s+table|delete\s+from)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IrreversibleContent();

    // Downloading a script and piping it straight into an interpreter executes
    // unreviewed remote code, so it stays manual even though each half looks benign.
    [GeneratedRegex(@"\b(?:curl|wget|invoke-webrequest|iwr|invoke-restmethod|irm)\b[^\r\n]*?[|;&]{1,2}\s*(?:bash|sh|zsh|dash|pwsh|powershell|python[0-9.]*|node|ruby|perl|iex|invoke-expression)\b|\b(?:iex|invoke-expression)\b[^\r\n]*\b(?:downloadstring|invoke-webrequest|iwr)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoteScriptExecution();

    // Uploading a body or a file to a remote endpoint can exfiltrate data. Read-only
    // fetches (--version, -I, -O into the workspace) are left to the relaxed tier.
    [GeneratedRegex(@"\b(?:curl|wget|invoke-webrequest|iwr|invoke-restmethod|irm)\b[^\r\n]*?(?:--data(?:-raw|-binary|-urlencode)?\b|(?:^|\s)-d\b|--upload-file\b|(?:^|\s)-T\b|(?:^|\s)-F\b|--form\b|--mail-\w+\b|-X\s*(?:POST|PUT|PATCH|DELETE)\b|-Method\s*(?:Post|Put|Patch|Delete)\b|-InFile\b|-Body\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OutboundDataTransfer();

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
