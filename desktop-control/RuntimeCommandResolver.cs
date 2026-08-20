namespace AiCliFeishuControl;

internal sealed record RuntimeCommandEnvironment(
    string SearchPath,
    string PathExtensions,
    string UserProfile,
    string ApplicationData,
    string LocalApplicationData)
{
    public static RuntimeCommandEnvironment FromProcess() => new(
        Environment.GetEnvironmentVariable("PATH") ?? "",
        Environment.GetEnvironmentVariable("PATHEXT") ?? "",
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
}

internal static class RuntimeCommandResolver
{
    // The managed terminal invokes the resolved command through PowerShell's call
    // operator, which runs .exe, .cmd, .bat and .ps1 alike, so every shim shape a
    // CLI installer may produce is a valid candidate. .exe stays first so a native
    // build keeps winning over an npm shim of the same name.
    private static readonly string[] PreferredExtensions = [".exe", ".cmd", ".bat", ".ps1"];

    // Each runtime gets a CODEX_COMMAND-style escape hatch so an install the probe
    // cannot find, or a version-pinned binary, can be pointed at explicitly.
    public static string OverrideVariableName(RuntimeProfile runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return $"{runtime.CommandName.ToUpperInvariant()}_COMMAND";
    }

    public static IReadOnlyList<string> Extensions(string? pathExtensions)
    {
        var ordered = new List<string>(PreferredExtensions);
        foreach (var entry in (pathExtensions ?? "").Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = entry.Trim('"').Trim().ToLowerInvariant();
            if (normalized.Length == 0)
            {
                continue;
            }
            if (!normalized.StartsWith('.'))
            {
                normalized = $".{normalized}";
            }
            if (!ordered.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(normalized);
            }
        }
        return ordered;
    }

    public static IReadOnlyList<string> Candidates(
        RuntimeProfile runtime,
        RuntimeCommandEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(environment);

        var directories = new List<string>();
        foreach (var entry in environment.SearchPath.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddDirectory(directories, entry.Trim('"').Trim());
        }
        if (!string.IsNullOrWhiteSpace(environment.UserProfile))
        {
            AddDirectory(
                directories,
                Path.Combine(environment.UserProfile, ".local", "bin"));
        }
        if (!string.IsNullOrWhiteSpace(environment.ApplicationData))
        {
            AddDirectory(directories, Path.Combine(environment.ApplicationData, "npm"));
        }
        if (!string.IsNullOrWhiteSpace(runtime.LocalProgramDirectory) &&
            !string.IsNullOrWhiteSpace(environment.LocalApplicationData))
        {
            AddDirectory(
                directories,
                Path.Combine(
                    environment.LocalApplicationData,
                    "Programs",
                    runtime.LocalProgramDirectory));
        }

        var extensions = Extensions(environment.PathExtensions);
        var candidates = new List<string>(directories.Count * extensions.Count);
        foreach (var directory in directories)
        {
            foreach (var extension in extensions)
            {
                candidates.Add(
                    Path.Combine(directory, $"{runtime.CommandName}{extension}"));
            }
        }
        return candidates;
    }

    public static string? Resolve(
        RuntimeProfile runtime,
        RuntimeCommandEnvironment environment,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        foreach (var candidate in Candidates(runtime, environment))
        {
            if (fileExists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static void AddDirectory(List<string> directories, string directory)
    {
        if (directory.Length == 0 ||
            directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            directories.Contains(directory, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }
        directories.Add(directory);
    }
}
