using System.Net;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Host.Tests;

[TestClass]
public sealed class ApprovalAuditLogTests
{
    [TestMethod]
    public async Task RotatesBeforeAppendingPastMaximumFileSize()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"approval-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var audit = new ApprovalAuditLog(new(
                directory,
                IPAddress.Loopback,
                0,
                BridgeOwnershipMode.Active,
                "audit-test"));
            var largeReason = new string('x', 5 * 1024 * 1024);

            await audit.AppendChangesAsync(
                new ApprovalStoreDocument(),
                Approvals("approval-large", largeReason));
            await audit.AppendChangesAsync(
                new ApprovalStoreDocument(),
                Approvals("approval-next", "low risk"));

            var path = Path.Combine(directory, "approval-events.log");
            Assert.IsTrue(File.Exists(path));
            Assert.IsTrue(File.Exists(path + ".1"));
            StringAssert.Contains(
                await File.ReadAllTextAsync(path),
                "approval-next");
            Assert.IsTrue(new FileInfo(path + ".1").Length > 5 * 1024 * 1024);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ApprovalStoreDocument Approvals(string requestId, string reason) => new()
    {
        Requests = new Dictionary<string, ApprovalStoreRecord>(StringComparer.Ordinal)
        {
            [requestId] = new()
            {
                RequestId = requestId,
                SessionId = "session-1",
                ToolName = "shell_command",
                Status = ApprovalStatuses.Pending,
                ExtensionData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["riskLevel"] = JsonSerializer.SerializeToElement("low"),
                    ["riskReason"] = JsonSerializer.SerializeToElement(reason),
                },
            },
        },
    };
}
