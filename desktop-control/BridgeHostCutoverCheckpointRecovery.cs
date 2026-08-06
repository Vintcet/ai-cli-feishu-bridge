namespace AiCliFeishuControl;

internal enum BridgeHostCutoverCheckpointRecoveryState
{
    Clean,
    RecoveryRequired,
    Recovered,
    Busy,
    Unavailable,
}

internal sealed record BridgeHostCutoverCheckpointRecoveryResult(
    BridgeHostCutoverCheckpointRecoveryState State);

internal static class BridgeHostCutoverCheckpointRecovery
{
    internal const string OrphanedDirectoryName = "bridge-host-cutover.orphaned";

    internal static BridgeHostCutoverCheckpointRecoveryState InspectDirectory(
        string dataDirectory)
    {
        var fullDataDirectory = NormalizeDataDirectory(dataDirectory);
        if (!TryGetDirectoryState(fullDataDirectory, out var state))
        {
            return BridgeHostCutoverCheckpointRecoveryState.Unavailable;
        }
        return state is DirectoryState.Present or DirectoryState.Missing
            ? BridgeHostCutoverCheckpointRecoveryState.Clean
            : BridgeHostCutoverCheckpointRecoveryState.RecoveryRequired;
    }

    internal static BridgeHostCutoverCheckpointRecoveryState Inspect(
        string dataDirectory)
    {
        var fullDataDirectory = NormalizeDataDirectory(dataDirectory);
        return Scan(fullDataDirectory).State switch
        {
            ScanState.Clean => BridgeHostCutoverCheckpointRecoveryState.Clean,
            ScanState.Unavailable => BridgeHostCutoverCheckpointRecoveryState.Unavailable,
            ScanState.Orphaned or ScanState.Unsafe =>
                BridgeHostCutoverCheckpointRecoveryState.RecoveryRequired,
            _ => BridgeHostCutoverCheckpointRecoveryState.Unavailable,
        };
    }

    internal static ValueTask<BridgeHostCutoverCheckpointRecoveryResult>
        TryQuarantineOrphanedFilesAsync(
            string dataDirectory,
            CancellationToken cancellationToken = default)
    {
        var fullDataDirectory = NormalizeDataDirectory(dataDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetDirectoryState(fullDataDirectory, out var directoryState))
        {
            return ValueTask.FromResult(Result(
                BridgeHostCutoverCheckpointRecoveryState.Unavailable));
        }
        if (directoryState is DirectoryState.Missing)
        {
            return ValueTask.FromResult(Result(
                BridgeHostCutoverCheckpointRecoveryState.Clean));
        }
        if (directoryState is DirectoryState.Invalid)
        {
            return ValueTask.FromResult(Result(
                BridgeHostCutoverCheckpointRecoveryState.RecoveryRequired));
        }

        FileStream? lockStream = null;
        try
        {
            lockStream = new FileStream(
                Path.Combine(
                    fullDataDirectory,
                    BridgeHostCutoverCheckpointWriter.WriterLockFileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.WriteThrough);
        }
        catch (UnauthorizedAccessException)
        {
            return ValueTask.FromResult(Result(
                BridgeHostCutoverCheckpointRecoveryState.Unavailable));
        }
        catch (IOException)
        {
            return ValueTask.FromResult(Result(
                BridgeHostCutoverCheckpointRecoveryState.Busy));
        }

        using (lockStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scan = Scan(fullDataDirectory);
            if (scan.State is ScanState.Clean)
            {
                return ValueTask.FromResult(Result(
                    BridgeHostCutoverCheckpointRecoveryState.Clean));
            }
            if (scan.State is ScanState.Unsafe)
            {
                return ValueTask.FromResult(Result(
                    BridgeHostCutoverCheckpointRecoveryState.RecoveryRequired));
            }
            if (scan.State is ScanState.Unavailable)
            {
                return ValueTask.FromResult(Result(
                    BridgeHostCutoverCheckpointRecoveryState.Unavailable));
            }

            if (!TryPrepareQuarantineDirectory(
                    fullDataDirectory,
                    out var quarantineDirectory))
            {
                return ValueTask.FromResult(Result(
                    BridgeHostCutoverCheckpointRecoveryState.Unavailable));
            }

            foreach (var sourcePath in scan.Paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetPath = Path.Combine(
                    quarantineDirectory,
                    Path.GetFileName(sourcePath));
                if (File.Exists(targetPath) || Directory.Exists(targetPath))
                {
                    return ValueTask.FromResult(Result(
                        BridgeHostCutoverCheckpointRecoveryState.Unavailable));
                }

                try
                {
                    File.Move(sourcePath, targetPath);
                }
                catch (UnauthorizedAccessException)
                {
                    return ValueTask.FromResult(Result(
                        BridgeHostCutoverCheckpointRecoveryState.Unavailable));
                }
                catch (IOException)
                {
                    return ValueTask.FromResult(Result(
                        BridgeHostCutoverCheckpointRecoveryState.Unavailable));
                }
            }

            var remaining = Scan(fullDataDirectory);
            return ValueTask.FromResult(Result(
                remaining.State is ScanState.Clean
                    ? BridgeHostCutoverCheckpointRecoveryState.Recovered
                    : remaining.State is ScanState.Unsafe
                        ? BridgeHostCutoverCheckpointRecoveryState.RecoveryRequired
                        : BridgeHostCutoverCheckpointRecoveryState.Unavailable));
        }
    }

    private static BridgeHostCutoverCheckpointRecoveryResult Result(
        BridgeHostCutoverCheckpointRecoveryState state) =>
        new(state);

    private static string NormalizeDataDirectory(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException(
                "检查点恢复的数据目录不能为空。",
                nameof(dataDirectory));
        }
        return Path.GetFullPath(dataDirectory);
    }

    private static bool TryGetDirectoryState(
        string dataDirectory,
        out DirectoryState state)
    {
        try
        {
            var attributes = File.GetAttributes(dataDirectory);
            state = attributes.HasFlag(FileAttributes.Directory) &&
                !attributes.HasFlag(FileAttributes.ReparsePoint)
                ? DirectoryState.Present
                : DirectoryState.Invalid;
            return true;
        }
        catch (FileNotFoundException)
        {
            state = DirectoryState.Missing;
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            state = DirectoryState.Missing;
            return true;
        }
        catch (IOException)
        {
            state = DirectoryState.Unavailable;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            state = DirectoryState.Unavailable;
            return false;
        }
    }

    private static ScanResult Scan(string dataDirectory)
    {
        if (!TryGetDirectoryState(dataDirectory, out var directoryState))
        {
            return new(ScanState.Unavailable, []);
        }
        if (directoryState is DirectoryState.Missing)
        {
            return new(ScanState.Clean, []);
        }
        if (directoryState is DirectoryState.Invalid)
        {
            return new(ScanState.Unsafe, []);
        }

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(
                dataDirectory,
                $"{BridgeHostCutoverCheckpointStore.CheckpointFileName}.*.tmp",
                SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (IOException)
        {
            return new(ScanState.Unavailable, []);
        }
        catch (UnauthorizedAccessException)
        {
            return new(ScanState.Unavailable, []);
        }

        var paths = new List<string>();
        foreach (var entry in entries)
        {
            var fileName = Path.GetFileName(entry);
            if (!IsCandidateName(fileName))
            {
                return new(ScanState.Unsafe, []);
            }
            try
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.Directory) ||
                    attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return new(ScanState.Unsafe, []);
                }
            }
            catch (IOException)
            {
                return new(ScanState.Unavailable, []);
            }
            catch (UnauthorizedAccessException)
            {
                return new(ScanState.Unavailable, []);
            }
            paths.Add(Path.GetFullPath(entry));
        }

        return paths.Count is 0
            ? new(ScanState.Clean, [])
            : new(ScanState.Orphaned, paths);
    }

    private static bool IsCandidateName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }
        var prefix = BridgeHostCutoverCheckpointStore.CheckpointFileName + ".";
        const string suffix = ".tmp";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = fileName[
            prefix.Length..
            ^suffix.Length];
        var separator = body.IndexOf('.');
        if (separator <= 0 || separator != body.LastIndexOf('.'))
        {
            return false;
        }
        var processId = body[..separator];
        var nonce = body[(separator + 1)..];
        return processId.All(character =>
                character is >= '0' and <= '9') &&
            int.TryParse(
                processId,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedProcessId) &&
            parsedProcessId > 0 &&
            string.Equals(
                processId,
                parsedProcessId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal) &&
            nonce.Length is 32 &&
            nonce.All(IsHexDigit);
    }

    private static bool IsHexDigit(char character) =>
        character is >= '0' and <= '9' or
            >= 'a' and <= 'f';

    private static bool TryPrepareQuarantineDirectory(
        string dataDirectory,
        out string quarantineDirectory)
    {
        quarantineDirectory = Path.Combine(
            dataDirectory,
            OrphanedDirectoryName);
        try
        {
            if (File.Exists(quarantineDirectory))
            {
                return false;
            }
            Directory.CreateDirectory(quarantineDirectory);
            var attributes = File.GetAttributes(quarantineDirectory);
            return attributes.HasFlag(FileAttributes.Directory) &&
                !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private enum DirectoryState
    {
        Present,
        Missing,
        Invalid,
        Unavailable,
    }

    private enum ScanState
    {
        Clean,
        Orphaned,
        Unsafe,
        Unavailable,
    }

    private sealed record ScanResult(ScanState State, IReadOnlyList<string> Paths);
}
