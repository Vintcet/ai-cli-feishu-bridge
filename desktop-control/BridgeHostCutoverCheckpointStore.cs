using System.Text;
using System.Text.Json;

namespace AiCliFeishuControl;

internal sealed class BridgeHostCutoverCheckpointStore
{
    public const string CheckpointFileName = "bridge-host-cutover.checkpoint.json";

    private readonly string dataDirectory;

    public BridgeHostCutoverCheckpointStore(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException(
                "切换检查点数据目录不能为空。",
                nameof(dataDirectory));
        }
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        CheckpointPath = Path.Combine(this.dataDirectory, CheckpointFileName);
    }

    internal string CheckpointPath { get; }

    public async ValueTask<BridgeHostCutoverCheckpointReadResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(CheckpointPath);
        }
        catch (Exception error) when (
            error is FileNotFoundException or DirectoryNotFoundException)
        {
            return ClassifyMissingCheckpoint();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(BridgeHostCutoverCheckpointReadState.Unavailable);
        }
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            return new(BridgeHostCutoverCheckpointReadState.Invalid);
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(
                CheckpointPath,
                Encoding.UTF8,
                cancellationToken);
        }
        catch (Exception error) when (
            error is FileNotFoundException or DirectoryNotFoundException)
        {
            return ClassifyMissingCheckpoint();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(BridgeHostCutoverCheckpointReadState.Unavailable);
        }

        try
        {
            return new(
                BridgeHostCutoverCheckpointReadState.Present,
                BridgeHostCutoverCheckpointJson.Deserialize(json));
        }
        catch (Exception error) when (
            error is JsonException or InvalidDataException or ArgumentException or
                InvalidOperationException or NotSupportedException)
        {
            return new(BridgeHostCutoverCheckpointReadState.Invalid);
        }
    }

    public async ValueTask WriteAsync(
        BridgeHostCutoverCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var json = BridgeHostCutoverCheckpointJson.Serialize(checkpoint);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(dataDirectory);
        var temporaryPath =
            $"{CheckpointPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(json);
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4_096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, CheckpointPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void TryDelete(string path)
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

    private BridgeHostCutoverCheckpointReadResult ClassifyMissingCheckpoint()
    {
        try
        {
            var attributes = File.GetAttributes(dataDirectory);
            return attributes.HasFlag(FileAttributes.Directory)
                ? new(BridgeHostCutoverCheckpointReadState.Missing)
                : new(BridgeHostCutoverCheckpointReadState.Invalid);
        }
        catch (Exception error) when (
            error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(BridgeHostCutoverCheckpointReadState.Missing);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(BridgeHostCutoverCheckpointReadState.Unavailable);
        }
    }
}
