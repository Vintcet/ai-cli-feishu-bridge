using System.Text;
using System.Text.Json;
using AiCliFeishu.Bridge.Adapters.Feishu;
using AiCliFeishu.Bridge.Adapters.Storage;
using AiCliFeishu.Bridge.Core;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal sealed partial class ActiveFeishuIntentHandler
{
    private async Task<FeishuCallbackResult?> RejectUnboundAsync(
        FeishuIntent intent,
        BindingStoreDocument bindings,
        CancellationToken cancellationToken)
    {
        var message = string.IsNullOrWhiteSpace(bindings.OwnerOpenId)
            ? $"飞书连接正常。请先在电脑端查看随机绑定码，然后私聊发送“{BindCommand()} 绑定码”。"
            : "飞书连接正常，但这个助手只允许已设置的管理员账号操作。";
        if (IsCardAction(intent))
        {
            return new("warning", message);
        }
        await SendTextWithFallbackAsync(intent, message, cancellationToken);
        return null;
    }

    private async Task<FeishuCallbackResult?> BindAsync(
        FeishuIntent intent,
        string? pairingCode,
        CancellationToken cancellationToken)
    {
        var result = "invalid_code";
        await storeOwner.UpdateAsync(store =>
        {
            var ownerOpenId = store.Bindings.OwnerOpenId?.Trim();
            if (!string.IsNullOrEmpty(ownerOpenId) &&
                !string.Equals(ownerOpenId, intent.OperatorOpenId, StringComparison.Ordinal))
            {
                result = "owner_mismatch";
                return store;
            }
            if (string.IsNullOrEmpty(ownerOpenId) &&
                (string.IsNullOrWhiteSpace(store.Bindings.PairingCode) ||
                 !string.Equals(
                     store.Bindings.PairingCode.Trim(),
                     pairingCode?.Trim(),
                     StringComparison.OrdinalIgnoreCase)))
            {
                result = "invalid_code";
                return store;
            }

            result = string.IsNullOrEmpty(ownerOpenId) ? "bound" : "rebound";
            var binding = new BindingStoreRecord
            {
                OpenId = intent.OperatorOpenId,
                ChatId = intent.ChatId,
                ChatType = intent.ChatType,
                BoundAt = DateTimeOffset.UtcNow.ToString("O"),
            };
            return store with
            {
                Bindings = new BindingStoreDocument
                {
                    OwnerOpenId = intent.OperatorOpenId,
                    PairingCode = null,
                    Users = new Dictionary<string, BindingStoreRecord>(
                        StringComparer.Ordinal)
                    {
                        [intent.OperatorOpenId] = binding,
                    },
                    ExtensionData = store.Bindings.ExtensionData,
                },
            };
        }, cancellationToken);

        var message = result switch
        {
            "bound" => "绑定成功，你已成为这台电脑上 AI CLI 助手的唯一管理员。",
            "rebound" => "管理员绑定已恢复。现在可以继续接收通知和回复 AI CLI。",
            "owner_mismatch" => "这个助手已经设置了唯一管理员，其他飞书账号不能绑定或控制本机 AI CLI。",
            _ => $"绑定码不正确。请在电脑端 AI CLI 飞书助手中查看本机绑定命令，再发送“{BindCommand()} 绑定码”。",
        };
        return await RespondTextAsync(intent, message, cancellationToken);
    }

    private async Task<FeishuCallbackResult?> UnbindAsync(
        FeishuIntent intent,
        CancellationToken cancellationToken)
    {
        var removed = false;
        await storeOwner.UpdateAsync(store =>
        {
            if (!store.Bindings.Users.ContainsKey(intent.OperatorOpenId))
            {
                return store;
            }
            removed = true;
            var users = new Dictionary<string, BindingStoreRecord>(
                store.Bindings.Users,
                StringComparer.Ordinal);
            users.Remove(intent.OperatorOpenId);
            return store with
            {
                Bindings = new BindingStoreDocument
                {
                    OwnerOpenId = store.Bindings.OwnerOpenId,
                    PairingCode = store.Bindings.PairingCode,
                    Users = users,
                    ExtensionData = store.Bindings.ExtensionData,
                },
            };
        }, cancellationToken);
        return await RespondTextAsync(
            intent,
            removed ? "已解绑。" : "当前账号还没有绑定。",
            cancellationToken);
    }

    private bool TryParseBindingCommand(FeishuIntent intent, out string? pairingCode)
    {
        pairingCode = null;
        if (!string.Equals(intent.ChatType, "p2p", StringComparison.Ordinal) ||
            intent.Text is null)
        {
            return false;
        }
        var text = intent.Text.Trim();
        var command = BindCommand();
        if (string.Equals(text, command, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!text.StartsWith(command, StringComparison.OrdinalIgnoreCase) ||
            text.Length == command.Length ||
            !char.IsWhiteSpace(text[command.Length]))
        {
            return false;
        }
        pairingCode = text[command.Length..].Trim();
        return true;
    }

    private static bool IsUnbindCommand(FeishuIntent intent) =>
        string.Equals(intent.ChatType, "p2p", StringComparison.Ordinal) &&
        string.Equals(intent.Text?.Trim(), "解绑", StringComparison.Ordinal);

    private string BindCommand() =>
        BridgeLocalConfiguration.Read(options, "FEISHU_BIND_COMMAND")?.Trim() is
            { Length: > 0 } command
            ? command
            : "绑定";

}
