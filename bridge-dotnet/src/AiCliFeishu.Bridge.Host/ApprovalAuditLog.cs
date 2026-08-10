using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;

namespace AiCliFeishu.Bridge.Host;

internal interface IApprovalAuditLog
{
    Task AppendChangesAsync(
        ApprovalStoreDocument before,
        ApprovalStoreDocument after,
        CancellationToken cancellationToken = default);
}

internal sealed class ApprovalAuditLog(BridgeHostOptions options) : IApprovalAuditLog
{
    private const long MaximumBytes = 5 * 1024 * 1024;
    private const int MaximumBackups = 5;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly string path = Path.Combine(options.DataDirectory, "approval-events.log");

    public async Task AppendChangesAsync(
        ApprovalStoreDocument before,
        ApprovalStoreDocument after,
        CancellationToken cancellationToken = default)
    {
        var events = Changes(before, after).ToArray();
        if (events.Length == 0)
        {
            return;
        }
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            foreach (var value in events)
            {
                var line = JsonSerializer.Serialize(value) + "\n";
                var bytes = Encoding.UTF8.GetBytes(line);
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaximumBytes)
                {
                    Rotate();
                }
                await using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static IEnumerable<object> Changes(
        ApprovalStoreDocument before,
        ApprovalStoreDocument after)
    {
        var at = DateTimeOffset.UtcNow.ToString("O");
        foreach (var (requestId, current) in after.Requests)
        {
            var existed = before.Requests.TryGetValue(requestId, out var previous);
            if (!existed)
            {
                yield return new
                {
                    at,
                    eventType = "approval.requested",
                    requestId,
                    current.SessionId,
                    current.ToolName,
                    current.Status,
                    riskLevel = Extension(current, "riskLevel"),
                    riskReason = Extension(current, "riskReason"),
                };
            }
            var prior = previous ?? new ApprovalStoreRecord();
            foreach (var messageId in current.MessageIds.Except(
                prior.MessageIds,
                StringComparer.Ordinal))
            {
                yield return new
                {
                    at,
                    eventType = "approval.notified",
                    requestId,
                    current.SessionId,
                    messageId,
                };
            }
            if (existed &&
                (!string.Equals(prior.Status, current.Status, StringComparison.Ordinal) ||
                !string.Equals(prior.Resolution, current.Resolution, StringComparison.Ordinal))
            )
            {
                yield return new
                {
                    at,
                    eventType = "approval.decided",
                    requestId,
                    current.SessionId,
                    current.Status,
                    current.Resolution,
                    current.ResolvedAt,
                    source = Extension(current, "desktopApprovalRequested") == "true"
                        ? "desktop"
                        : "runtime_or_feishu",
                };
            }
        }
    }

    private static string? Extension(ApprovalStoreRecord value, string name) =>
        value.ExtensionData is not null &&
        value.ExtensionData.TryGetValue(name, out var property)
            ? property.ToString()
            : null;

    private void Rotate()
    {
        for (var index = MaximumBackups; index >= 1; index--)
        {
            var source = index == 1 ? path : $"{path}.{index - 1}";
            var destination = $"{path}.{index}";
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
            if (File.Exists(source))
            {
                File.Move(source, destination);
            }
        }
    }
}
