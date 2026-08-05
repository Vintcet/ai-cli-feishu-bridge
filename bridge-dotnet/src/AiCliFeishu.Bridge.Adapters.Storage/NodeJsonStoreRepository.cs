using System.Text;

namespace AiCliFeishu.Bridge.Adapters.Storage;

public enum NodeStoreAccess
{
    ReadOnly,
    ReadWriteCopy,
}

public sealed class NodeJsonStoreRepository
{
    private readonly string dataDirectory;

    public NodeJsonStoreRepository(string dataDirectory, NodeStoreAccess access = NodeStoreAccess.ReadOnly)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("Store 目录不能为空。", nameof(dataDirectory));
        }
        this.dataDirectory = Path.GetFullPath(dataDirectory);
        Access = access;
    }

    public NodeStoreAccess Access { get; }

    public async Task<NodeStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        return new NodeStoreSnapshot(
            await ReadAsync(NodeStoreFile.Bindings, new BindingStoreDocument(), cancellationToken),
            await ReadAsync(NodeStoreFile.Sessions, new SessionStoreDocument(), cancellationToken),
            await ReadAsync(NodeStoreFile.Routes, new RouteStoreDocument(), cancellationToken),
            await ReadAsync(NodeStoreFile.Approvals, new ApprovalStoreDocument(), cancellationToken),
            await ReadAsync(NodeStoreFile.Settings, new SettingsStoreDocument(), cancellationToken),
            await ReadAsync(NodeStoreFile.ControlToken, new ControlTokenStoreDocument(), cancellationToken));
    }

    public async Task WriteAsync(
        NodeStoreSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureWritable();
        Directory.CreateDirectory(dataDirectory);
        await WriteFileAsync(NodeStoreFile.Bindings, snapshot.Bindings, cancellationToken);
        await WriteFileAsync(NodeStoreFile.Sessions, snapshot.Sessions, cancellationToken);
        await WriteFileAsync(NodeStoreFile.Routes, snapshot.Routes, cancellationToken);
        await WriteFileAsync(NodeStoreFile.Approvals, snapshot.Approvals, cancellationToken);
        await WriteFileAsync(NodeStoreFile.Settings, snapshot.Settings, cancellationToken);
        await WriteFileAsync(NodeStoreFile.ControlToken, snapshot.ControlToken, cancellationToken);
    }

    private async Task<T> ReadAsync<T>(
        NodeStoreFile file,
        T fallback,
        CancellationToken cancellationToken)
        where T : class
    {
        var path = Resolve(file);
        if (!File.Exists(path))
        {
            return fallback;
        }
        var json = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        return NodeStoreJson.Deserialize<T>(json, file);
    }

    private async Task WriteFileAsync<T>(
        NodeStoreFile file,
        T value,
        CancellationToken cancellationToken)
        where T : class
    {
        var destination = Resolve(file);
        var temporary = $"{destination}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var bytes = new UTF8Encoding(false).GetBytes(NodeStoreJson.Serialize(value));
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
            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    private string Resolve(NodeStoreFile file)
    {
        if (!NodeStoreFile.All.Contains(file))
        {
            throw new ArgumentException("只允许访问已登记的 Node Store 文件。", nameof(file));
        }
        return Path.Combine(dataDirectory, file.FileName);
    }

    private void EnsureWritable()
    {
        if (Access != NodeStoreAccess.ReadWriteCopy)
        {
            throw new InvalidOperationException(
                "M2 C# Store 默认为只读；只能对明确创建的迁移副本启用写入。 ");
        }
    }
}
