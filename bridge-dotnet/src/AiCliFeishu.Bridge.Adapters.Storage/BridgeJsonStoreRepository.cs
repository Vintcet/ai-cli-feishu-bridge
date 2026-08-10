using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiCliFeishu.Bridge.Adapters.Storage;

public enum BridgeStoreAccess
{
    ReadOnly,
    ReadWriteCopy,
    ReadWriteActiveOwner,
}

public sealed class BridgeStoreCorruptionException(
    string logicalFile,
    string? quarantinedPath,
    Exception innerException)
    : IOException(
        quarantinedPath is null
            ? $"Bridge Store {logicalFile} 已损坏，已拒绝加载。"
            : $"Bridge Store {logicalFile} 已损坏并隔离到 {quarantinedPath}，已拒绝加载。",
        innerException)
{
    public string LogicalFile { get; } = logicalFile;

    public string? QuarantinedPath { get; } = quarantinedPath;
}

internal readonly record struct BridgeStoreWriteCheckpoint(
    string Stage,
    int Index,
    BridgeStoreFile? File = null);

public sealed class BridgeJsonStoreRepository
{
    private const int CommitSchemaVersion = 1;
    private const int RetainedCommitManifests = 64;
    private const int CommitCleanupThreshold = 80;
    private const string MetadataDirectoryName = ".bridge-store";
    private const string CommitManifestFileName = ".bridge-store.commit";
    private const string ObjectsDirectoryName = "objects";
    private const string CommitsDirectoryName = "commits";

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
    };

    private readonly string dataDirectory;
    private readonly string metadataDirectory;
    private readonly string objectsDirectory;
    private readonly string commitsDirectory;
    private readonly string commitManifestPath;
    private readonly Action<BridgeStoreWriteCheckpoint>? checkpoint;

    public BridgeJsonStoreRepository(
        string dataDirectory,
        BridgeStoreAccess access = BridgeStoreAccess.ReadOnly)
        : this(dataDirectory, access, checkpoint: null)
    {
    }

    internal BridgeJsonStoreRepository(
        string dataDirectory,
        BridgeStoreAccess access,
        Action<BridgeStoreWriteCheckpoint>? checkpoint)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("Store 目录不能为空。", nameof(dataDirectory));
        }
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        metadataDirectory = Path.Combine(this.dataDirectory, MetadataDirectoryName);
        objectsDirectory = Path.Combine(metadataDirectory, ObjectsDirectoryName);
        commitsDirectory = Path.Combine(metadataDirectory, CommitsDirectoryName);
        commitManifestPath = Path.Combine(this.dataDirectory, CommitManifestFileName);
        Access = access;
        this.checkpoint = checkpoint;
    }

    public BridgeStoreAccess Access { get; }

    public bool HasCommittedGeneration => File.Exists(commitManifestPath);

    internal string CommitManifestPath => commitManifestPath;

    public async Task<BridgeStoreSnapshot> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (Access is not BridgeStoreAccess.ReadOnly)
        {
            BridgeStoreFileSecurity.HardenDirectory(dataDirectory);
        }

        if (File.Exists(commitManifestPath))
        {
            return await LoadCommittedAsync(cancellationToken);
        }
        if (HasCommitHistory())
        {
            if (Access is BridgeStoreAccess.ReadOnly)
            {
                throw new BridgeStoreCorruptionException(
                    CommitManifestFileName,
                    null,
                    new FileNotFoundException(
                        "Bridge Store canonical commit manifest 缺失，但存在提交历史。",
                        commitManifestPath));
            }
            var recovered = await RecoverLatestManifestAsync(cancellationToken);
            return await LoadCommittedAsync(recovered, cancellationToken);
        }
        return await LoadLegacyAsync(cancellationToken);
    }

    public async ValueTask<string?> ReadControlTokenAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(commitManifestPath))
        {
            if (HasCommitHistory())
            {
                throw new BridgeStoreCorruptionException(
                    CommitManifestFileName,
                    null,
                    new FileNotFoundException(
                        "Bridge Store canonical commit manifest 缺失，但存在提交历史。",
                        commitManifestPath));
            }
            return (await ReadLegacyAsync(
                BridgeStoreFile.ControlToken,
                new ControlTokenStoreDocument(),
                cancellationToken)).Token;
        }

        var manifest = await ReadManifestAsync(commitManifestPath, cancellationToken);
        var bytes = await ReadCommittedFileBytesAsync(
            manifest,
            BridgeStoreFile.ControlToken,
            cancellationToken);
        return DeserializeCommitted<ControlTokenStoreDocument>(
            manifest,
            bytes,
            BridgeStoreFile.ControlToken).Token;
    }

    public async Task WriteAsync(
        BridgeStoreSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureWritable();
        BridgeStoreValidator.ValidateSnapshot(snapshot);
        BridgeStoreFileSecurity.HardenDirectory(dataDirectory);
        BridgeStoreFileSecurity.HardenDirectory(metadataDirectory);
        BridgeStoreFileSecurity.HardenDirectory(objectsDirectory);
        BridgeStoreFileSecurity.HardenDirectory(commitsDirectory);

        var serialized = Serialize(snapshot);
        BridgeStoreCommitManifest? current = null;
        if (File.Exists(commitManifestPath))
        {
            current = await ReadManifestAsync(commitManifestPath, cancellationToken);
        }
        if (current is not null && ManifestMatches(current, serialized))
        {
            await SynchronizeMirrorsAsync(serialized, cancellationToken);
            return;
        }

        var changedFiles = BridgeStoreFile.All
            .Where(file => current is null ||
                !current.Files.TryGetValue(file.FileName, out var entry) ||
                !string.Equals(
                    entry.Sha256,
                    serialized[file].Sha256,
                    StringComparison.Ordinal) ||
                entry.Length != serialized[file].Bytes.LongLength)
            .ToArray();

        var generation = Guid.NewGuid().ToString("N");
        var files = new Dictionary<string, BridgeStoreCommitFile>(StringComparer.Ordinal);
        var index = 0;
        foreach (var file in BridgeStoreFile.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = serialized[file];
            var objectName = $"{content.Sha256}.blob";
            var objectPath = Path.Combine(objectsDirectory, objectName);
            if (!File.Exists(objectPath))
            {
                await WriteAtomicFileAsync(objectPath, content.Bytes, cancellationToken);
            }
            else
            {
                await ValidateExistingObjectAsync(
                    objectPath,
                    content,
                    file,
                    cancellationToken);
            }
            files[file.FileName] = new BridgeStoreCommitFile(
                objectName,
                content.Sha256,
                content.Bytes.LongLength);
            checkpoint?.Invoke(new("file-prepared", ++index, file));
        }

        var manifest = new BridgeStoreCommitManifest(
            CommitSchemaVersion,
            generation,
            DateTimeOffset.UtcNow,
            files);
        var manifestBytes = SerializeManifest(manifest);
        var historicalManifest = Path.Combine(
            commitsDirectory,
            $"{generation}.commit");
        await WriteAtomicFileAsync(
            historicalManifest,
            manifestBytes,
            cancellationToken);
        checkpoint?.Invoke(new("before-manifest-commit", 0));
        await WriteAtomicFileAsync(
            commitManifestPath,
            manifestBytes,
            cancellationToken);
        checkpoint?.Invoke(new("manifest-committed", 0));

        await SynchronizeMirrorsAsync(
            serialized,
            cancellationToken,
            changedFiles);
        PruneCommitHistoryBestEffort(manifest);
    }

    private async Task<BridgeStoreSnapshot> LoadCommittedAsync(
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(commitManifestPath, cancellationToken);
        return await LoadCommittedAsync(manifest, cancellationToken);
    }

    private async Task<BridgeStoreSnapshot> LoadCommittedAsync(
        BridgeStoreCommitManifest manifest,
        CancellationToken cancellationToken)
    {
        var (snapshot, content) = await ReadCommittedSnapshotAsync(
            manifest,
            cancellationToken);
        ValidateLoadedSnapshot(snapshot, manifest);
        if (Access is not BridgeStoreAccess.ReadOnly)
        {
            await SynchronizeMirrorsAsync(
                content.ToDictionary(
                    item => item.Key,
                    item => SerializedStoreFile.FromBytes(item.Value)),
                cancellationToken);
        }
        return snapshot;
    }

    private async Task<BridgeStoreCommitManifest> RecoverLatestManifestAsync(
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var path in Directory.EnumerateFiles(commitsDirectory, "*.commit")
                     .Select(path => new FileInfo(path))
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                     .Select(file => file.FullName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifest = await ReadManifestAsync(path, cancellationToken);
                await ValidateCommittedGenerationAsync(manifest, cancellationToken);
                await WriteAtomicFileAsync(
                    commitManifestPath,
                    SerializeManifest(manifest),
                    cancellationToken);
                return manifest;
            }
            catch (Exception error) when (
                error is IOException or
                UnauthorizedAccessException or
                JsonException or
                InvalidDataException)
            {
                lastError = error;
            }
        }
        throw new BridgeStoreCorruptionException(
            CommitManifestFileName,
            null,
            new InvalidDataException(
                "Bridge Store 提交历史均无法恢复。",
                lastError));
    }

    private async Task ValidateCommittedGenerationAsync(
        BridgeStoreCommitManifest manifest,
        CancellationToken cancellationToken)
    {
        var (snapshot, _) = await ReadCommittedSnapshotAsync(manifest, cancellationToken);
        BridgeStoreValidator.ValidateSnapshot(snapshot);
    }

    private async Task<BridgeStoreSnapshot> LoadLegacyAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = new BridgeStoreSnapshot(
            await ReadLegacyAsync(
                BridgeStoreFile.Bindings,
                new BindingStoreDocument(),
                cancellationToken),
            await ReadLegacyAsync(
                BridgeStoreFile.Sessions,
                new SessionStoreDocument(),
                cancellationToken),
            await ReadLegacyAsync(
                BridgeStoreFile.Routes,
                new RouteStoreDocument(),
                cancellationToken),
            await ReadLegacyAsync(
                BridgeStoreFile.Approvals,
                new ApprovalStoreDocument(),
                cancellationToken),
            await ReadLegacyAsync(
                BridgeStoreFile.Settings,
                new SettingsStoreDocument(),
                cancellationToken),
            await ReadLegacyAsync(
                BridgeStoreFile.ControlToken,
                new ControlTokenStoreDocument(),
                cancellationToken));
        ValidateLoadedSnapshot(snapshot, manifest: null);
        return snapshot;
    }

    private async Task<(
        BridgeStoreSnapshot Snapshot,
        Dictionary<BridgeStoreFile, byte[]> Content)> ReadCommittedSnapshotAsync(
        BridgeStoreCommitManifest manifest,
        CancellationToken cancellationToken)
    {
        var content = new Dictionary<BridgeStoreFile, byte[]>();
        foreach (var file in BridgeStoreFile.All)
        {
            content[file] = await ReadCommittedFileBytesAsync(
                manifest,
                file,
                cancellationToken);
        }

        return (new BridgeStoreSnapshot(
            DeserializeCommitted<BindingStoreDocument>(
                manifest,
                content[BridgeStoreFile.Bindings],
                BridgeStoreFile.Bindings),
            DeserializeCommitted<SessionStoreDocument>(
                manifest,
                content[BridgeStoreFile.Sessions],
                BridgeStoreFile.Sessions),
            DeserializeCommitted<RouteStoreDocument>(
                manifest,
                content[BridgeStoreFile.Routes],
                BridgeStoreFile.Routes),
            DeserializeCommitted<ApprovalStoreDocument>(
                manifest,
                content[BridgeStoreFile.Approvals],
                BridgeStoreFile.Approvals),
            DeserializeCommitted<SettingsStoreDocument>(
                manifest,
                content[BridgeStoreFile.Settings],
                BridgeStoreFile.Settings),
            DeserializeCommitted<ControlTokenStoreDocument>(
                manifest,
                content[BridgeStoreFile.ControlToken],
                BridgeStoreFile.ControlToken)), content);
    }

    private void ValidateLoadedSnapshot(
        BridgeStoreSnapshot snapshot,
        BridgeStoreCommitManifest? manifest)
    {
        try
        {
            BridgeStoreValidator.ValidateSnapshot(snapshot);
        }
        catch (BridgeStoreValidationException error)
        {
            if (Access is BridgeStoreAccess.ReadOnly)
            {
                throw;
            }
            var file = BridgeStoreFile.All.FirstOrDefault(candidate => string.Equals(
                candidate.FileName,
                error.FileName,
                StringComparison.Ordinal)) ?? throw new InvalidDataException(
                $"未知 Bridge Store 文件 {error.FileName}。",
                error);
            var path = manifest is null
                ? Resolve(file)
                : Path.Combine(
                    objectsDirectory,
                    manifest.Files[file.FileName].ObjectName);
            throw File.Exists(path)
                ? Quarantine(path, file.FileName, error)
                : new BridgeStoreCorruptionException(file.FileName, null, error);
        }
    }

    private async Task<T> ReadLegacyAsync<T>(
        BridgeStoreFile file,
        T fallback,
        CancellationToken cancellationToken)
        where T : class
    {
        var path = Resolve(file);
        if (!File.Exists(path))
        {
            return fallback;
        }
        try
        {
            var json = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
            return BridgeStoreJson.Deserialize<T>(json, file);
        }
        catch (Exception error) when (IsCorruption(error))
        {
            if (Access is BridgeStoreAccess.ReadOnly)
            {
                throw;
            }
            throw Quarantine(path, file.FileName, error);
        }
    }

    private async Task<BridgeStoreCommitManifest> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
            var manifest = JsonSerializer.Deserialize<BridgeStoreCommitManifest>(
                json,
                ManifestJson) ?? throw new InvalidDataException(
                    "Bridge Store commit manifest 不能为空。 ");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (Exception error) when (IsCorruption(error))
        {
            if (Access is BridgeStoreAccess.ReadOnly)
            {
                throw;
            }
            throw Quarantine(path, CommitManifestFileName, error);
        }
    }

    private async Task<byte[]> ReadCommittedFileBytesAsync(
        BridgeStoreCommitManifest manifest,
        BridgeStoreFile file,
        CancellationToken cancellationToken)
    {
        var entry = manifest.Files[file.FileName];
        var path = Path.Combine(objectsDirectory, entry.ObjectName);
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (bytes.LongLength != entry.Length ||
                !string.Equals(Hash(bytes), entry.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Bridge Store 对象 {entry.ObjectName} 校验和不匹配。 ");
            }
            return bytes;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (Access is BridgeStoreAccess.ReadOnly)
            {
                throw;
            }
            if (!File.Exists(path))
            {
                throw new BridgeStoreCorruptionException(file.FileName, null, error);
            }
            throw Quarantine(path, file.FileName, error);
        }
    }

    private T DeserializeCommitted<T>(
        BridgeStoreCommitManifest manifest,
        byte[] bytes,
        BridgeStoreFile file)
        where T : class
    {
        try
        {
            return BridgeStoreJson.Deserialize<T>(Encoding.UTF8.GetString(bytes), file);
        }
        catch (Exception error) when (IsCorruption(error))
        {
            if (Access is BridgeStoreAccess.ReadOnly)
            {
                throw;
            }
            var path = Path.Combine(
                objectsDirectory,
                manifest.Files[file.FileName].ObjectName);
            throw File.Exists(path)
                ? Quarantine(path, file.FileName, error)
                : new BridgeStoreCorruptionException(file.FileName, null, error);
        }
    }

    private async Task ValidateExistingObjectAsync(
        string path,
        SerializedStoreFile expected,
        BridgeStoreFile file,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.AsSpan().SequenceEqual(expected.Bytes))
        {
            return;
        }

        throw Quarantine(
            path,
            file.FileName,
            new InvalidDataException("内容寻址 Store 对象与其哈希文件名不匹配。"));
    }

    private async Task SynchronizeMirrorsAsync(
        IReadOnlyDictionary<BridgeStoreFile, SerializedStoreFile> serialized,
        CancellationToken cancellationToken,
        IReadOnlyCollection<BridgeStoreFile>? selectedFiles = null)
    {
        var index = 0;
        foreach (var file in selectedFiles ?? BridgeStoreFile.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Resolve(file);
            var content = serialized[file];
            var currentMatches = false;
            if (File.Exists(destination))
            {
                try
                {
                    var existing = await File.ReadAllBytesAsync(destination, cancellationToken);
                    currentMatches = existing.AsSpan().SequenceEqual(content.Bytes);
                }
                catch (IOException)
                {
                    currentMatches = false;
                }
            }
            if (!currentMatches)
            {
                await WriteAtomicFileAsync(destination, content.Bytes, cancellationToken);
            }
            else if (file == BridgeStoreFile.ControlToken)
            {
                BridgeStoreFileSecurity.HardenFile(destination);
            }
            checkpoint?.Invoke(new("mirror-synchronized", ++index, file));
        }
    }

    private static Dictionary<BridgeStoreFile, SerializedStoreFile> Serialize(
        BridgeStoreSnapshot snapshot) => new()
        {
            [BridgeStoreFile.Bindings] = SerializedStoreFile.FromJson(
                BridgeStoreJson.Serialize(snapshot.Bindings)),
            [BridgeStoreFile.Sessions] = SerializedStoreFile.FromJson(
                BridgeStoreJson.Serialize(snapshot.Sessions)),
            [BridgeStoreFile.Routes] = SerializedStoreFile.FromJson(
                BridgeStoreJson.Serialize(snapshot.Routes)),
            [BridgeStoreFile.Approvals] = SerializedStoreFile.FromJson(
                BridgeStoreJson.Serialize(snapshot.Approvals)),
            [BridgeStoreFile.Settings] = SerializedStoreFile.FromJson(
                BridgeStoreJson.Serialize(snapshot.Settings)),
            [BridgeStoreFile.ControlToken] = SerializedStoreFile.FromJson(
                BridgeStoreJson.Serialize(snapshot.ControlToken)),
        };

    private static bool ManifestMatches(
        BridgeStoreCommitManifest manifest,
        IReadOnlyDictionary<BridgeStoreFile, SerializedStoreFile> serialized) =>
        BridgeStoreFile.All.All(file =>
            manifest.Files.TryGetValue(file.FileName, out var entry) &&
            string.Equals(
                entry.Sha256,
                serialized[file].Sha256,
                StringComparison.Ordinal) &&
            entry.Length == serialized[file].Bytes.LongLength);

    private static byte[] SerializeManifest(BridgeStoreCommitManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, ManifestJson) + "\n";
        return new UTF8Encoding(false).GetBytes(json);
    }

    private async Task WriteAtomicFileAsync(
        string destination,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var temporary = $"{destination}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            BridgeStoreFileSecurity.HardenFile(temporary);
            File.Move(temporary, destination, overwrite: true);
            BridgeStoreFileSecurity.HardenFile(destination);
            using var committed = new FileStream(
                destination,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                4_096,
                FileOptions.WriteThrough);
            committed.Flush(flushToDisk: true);
        }
        catch
        {
            TryDeleteFile(temporary);
            throw;
        }
    }

    private void PruneCommitHistoryBestEffort(BridgeStoreCommitManifest current)
    {
        try
        {
            var manifests = Directory.EnumerateFiles(commitsDirectory, "*.commit")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                .ToArray();
            if (manifests.Length <= CommitCleanupThreshold)
            {
                return;
            }

            var retained = manifests.Take(RetainedCommitManifests).ToArray();
            var objects = new HashSet<string>(
                current.Files.Values.Select(file => file.ObjectName),
                StringComparer.Ordinal);
            foreach (var file in retained)
            {
                try
                {
                    var manifest = JsonSerializer.Deserialize<BridgeStoreCommitManifest>(
                        File.ReadAllText(file.FullName, Encoding.UTF8),
                        ManifestJson);
                    if (manifest is not null)
                    {
                        foreach (var entry in manifest.Files.Values)
                        {
                            objects.Add(entry.ObjectName);
                        }
                    }
                }
                catch (Exception error) when (IsCorruption(error))
                {
                }
            }
            foreach (var obsolete in manifests.Skip(RetainedCommitManifests))
            {
                TryDeleteFile(obsolete.FullName);
            }
            foreach (var path in Directory.EnumerateFiles(objectsDirectory, "*.blob"))
            {
                if (!objects.Contains(Path.GetFileName(path)))
                {
                    TryDeleteFile(path);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateManifest(BridgeStoreCommitManifest manifest)
    {
        if (manifest.SchemaVersion != CommitSchemaVersion ||
            manifest.CommittedAt == default ||
            manifest.Generation.Length != 32 ||
            !manifest.Generation.All(Uri.IsHexDigit) ||
            manifest.Files.Count != BridgeStoreFile.All.Count)
        {
            throw new InvalidDataException("Bridge Store commit manifest 结构无效。 ");
        }
        foreach (var file in BridgeStoreFile.All)
        {
            if (!manifest.Files.TryGetValue(file.FileName, out var entry) ||
                entry.Length < 0 ||
                entry.Sha256.Length != 64 ||
                !entry.Sha256.All(Uri.IsHexDigit) ||
                !string.Equals(
                    entry.ObjectName,
                    $"{entry.Sha256}.blob",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Bridge Store commit manifest 缺少有效的 {file.FileName}。 ");
            }
        }
    }

    private bool HasCommitHistory() =>
        Directory.Exists(commitsDirectory) &&
        Directory.EnumerateFiles(commitsDirectory, "*.commit").Any();

    private BridgeStoreCorruptionException Quarantine(
        string path,
        string logicalFile,
        Exception error)
    {
        string? quarantine = null;
        try
        {
            if (File.Exists(path))
            {
                quarantine = $"{path}.corrupt-" +
                    $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
                File.Move(path, quarantine);
                BridgeStoreFileSecurity.HardenFile(quarantine);
            }
        }
        catch (Exception quarantineError) when (
            quarantineError is IOException or UnauthorizedAccessException)
        {
            return new BridgeStoreCorruptionException(
                logicalFile,
                null,
                new AggregateException(error, quarantineError));
        }
        return new BridgeStoreCorruptionException(logicalFile, quarantine, error);
    }

    private string Resolve(BridgeStoreFile file)
    {
        if (!BridgeStoreFile.All.Contains(file))
        {
            throw new ArgumentException("只允许访问已登记的 Bridge Store 文件。", nameof(file));
        }
        return Path.Combine(dataDirectory, file.FileName);
    }

    private void EnsureWritable()
    {
        if (Access is not (BridgeStoreAccess.ReadWriteCopy or
            BridgeStoreAccess.ReadWriteActiveOwner))
        {
            throw new InvalidOperationException(
                "只读 Bridge Store Repository 不允许写入。 ");
        }
    }

    private static bool IsCorruption(Exception error) =>
        error is JsonException or BridgeStoreValidationException or InvalidDataException;

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record SerializedStoreFile(byte[] Bytes, string Sha256)
    {
        public static SerializedStoreFile FromJson(string json) =>
            FromBytes(new UTF8Encoding(false).GetBytes(json));

        public static SerializedStoreFile FromBytes(byte[] bytes) =>
            new(bytes, Hash(bytes));
    }

    private sealed record BridgeStoreCommitManifest(
        int SchemaVersion,
        string Generation,
        DateTimeOffset CommittedAt,
        Dictionary<string, BridgeStoreCommitFile> Files);

    private sealed record BridgeStoreCommitFile(
        string ObjectName,
        string Sha256,
        long Length);
}
