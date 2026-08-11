using System.Text.Json;
using System.Text.Json.Nodes;
using AiCliFeishu.Bridge.Adapters.ManagedTerminal;
using AiCliFeishu.Bridge.Protocol;

namespace AiCliFeishu.Bridge.Host;

internal enum BridgeManagedIngressKind
{
    TerminalRegister,
    TerminalUnregister,
    SessionStart,
    SessionEnd,
    Permission,
    RequestUserInput,
    Activity,
    Stop,
}

internal sealed class ActiveManagedHookIngress(
    BridgeHostOptions options,
    IBridgeManagedTerminalRegistrationDirectory terminals,
    IBridgeManagedRuntimeLaunchCoordinator runtimeLaunches,
    ManagedRuntimeHookBridge hookBridge) : IBridgeManagedHookIngress, IDisposable
{
    private static readonly JsonElement OkResponse =
        JsonSerializer.SerializeToElement(new { ok = true });
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);

    public async Task<JsonElement> HandleAsync(
        BridgeManagedIngressKind kind,
        JsonElement payload,
        string traceId,
        CancellationToken cancellationToken = default)
    {
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();
        if (payload.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidDataException("托管 Hook 请求必须是 JSON 对象。");
        }
        if (string.IsNullOrWhiteSpace(traceId))
        {
            throw new ArgumentException("Hook trace ID 不能为空。", nameof(traceId));
        }

        return kind switch
        {
            BridgeManagedIngressKind.TerminalRegister =>
                await RegisterAsync(payload, cancellationToken),
            BridgeManagedIngressKind.TerminalUnregister =>
                await UnregisterAsync(payload, traceId, cancellationToken),
            BridgeManagedIngressKind.SessionStart =>
                await StartSessionAsync(payload, traceId, cancellationToken),
            BridgeManagedIngressKind.SessionEnd =>
                await EndSessionAsync(payload, traceId, cancellationToken),
            _ => await HandleSessionHookAsync(kind, payload, traceId, cancellationToken),
        };
    }

    public void Dispose() => lifecycleGate.Dispose();

    private async Task<JsonElement> RegisterAsync(
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var registration = new BridgeManagedTerminalRegistration(
            RequiredString(payload, "terminalId", 64),
            RequiredString(payload, "terminalSecret", 64),
            RequiredString(payload, "cwd", 32_768),
            RequiredRuntime(payload),
            RequiredBoolean(payload, "elevated"),
            RequiredBoolean(payload, "ready"));
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            terminals.Register(registration);
            if (registration.Ready &&
                terminals.FindClaimByTerminal(registration.TerminalId) is
                { SessionExternalId: var sessionExternalId })
            {
                await runtimeLaunches.DrainAsync(
                    sessionExternalId,
                    cancellationToken);
            }
            return OkResponse.Clone();
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task<JsonElement> UnregisterAsync(
        JsonElement payload,
        string traceId,
        CancellationToken cancellationToken)
    {
        var terminalId = RequiredString(payload, "terminalId", 64);
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var claimed = terminals.FindClaimByTerminal(terminalId);
            if (claimed is not null)
            {
                var hook = JsonSerializer.SerializeToElement(new
                {
                    hook_event_name = "SessionEnd",
                    session_id = claimed.SessionExternalId,
                    cwd = claimed.Cwd,
                    reason = "managed_terminal_unregistered",
                    runtime = claimed.Runtime,
                    managed_terminal_id = claimed.TerminalId,
                    managed_terminal_elevated = claimed.Elevated,
                });
                await hookBridge.HandleAsync(hook, traceId, cancellationToken);
                hookBridge.ReleaseSession(
                    claimed.Runtime,
                    claimed.SessionExternalId);
            }
            terminals.Unregister(terminalId);
            return OkResponse.Clone();
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task<JsonElement> StartSessionAsync(
        JsonElement payload,
        string traceId,
        CancellationToken cancellationToken)
    {
        ValidateHook(BridgeManagedIngressKind.SessionStart, payload);
        var runtime = OptionalRuntime(payload);
        var sessionId = RequiredString(payload, "session_id", 256);
        var cwd = RequiredString(payload, "cwd", 32_768);
        var terminalId = OptionalString(payload, "managed_terminal_id");

        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            BridgeManagedTerminalClaim? claim = null;
            if (terminalId is not null)
            {
                claim = terminals.ClaimById(
                    terminalId,
                    cwd,
                    runtime,
                    sessionId,
                    OptionalBoolean(payload, "managed_terminal_elevated"));
                if (claim is null)
                {
                    throw new InvalidOperationException(
                        "托管终端尚未登记或已经离线，无法认领会话。");
                }
            }
            else if (terminals.FindClaimBySession(sessionId) is not null)
            {
                throw new InvalidOperationException(
                    "已绑定托管终端的 SessionStart 缺少终端身份。");
            }

            var canonical = claim is null
                ? WithRuntime(payload, runtime)
                : WithManagedIdentity(
                    payload,
                    runtime,
                    claim.TerminalId,
                    claim.Elevated);
            JsonElement response;
            try
            {
                response = await hookBridge.HandleAsync(
                    canonical,
                    traceId,
                    cancellationToken);
            }
            catch
            {
                if (claim is { ExistingClaim: false })
                {
                    terminals.Release(sessionId);
                }
                throw;
            }
            // The event is durable before queued launch prompts are delivered. A
            // drain failure keeps the binding so a retried SessionStart can drain.
            await runtimeLaunches.DrainAsync(sessionId, cancellationToken);
            return response;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task<JsonElement> EndSessionAsync(
        JsonElement payload,
        string traceId,
        CancellationToken cancellationToken)
    {
        ValidateHook(BridgeManagedIngressKind.SessionEnd, payload);
        var sessionId = RequiredString(payload, "session_id", 256);
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var canonical = BindExistingIdentity(payload);
            var response = await hookBridge.HandleAsync(
                canonical,
                traceId,
                cancellationToken);
            hookBridge.ReleaseSession(
                OptionalRuntime(canonical),
                sessionId);
            terminals.Release(sessionId);
            return response;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task<JsonElement> HandleSessionHookAsync(
        BridgeManagedIngressKind kind,
        JsonElement payload,
        string traceId,
        CancellationToken cancellationToken)
    {
        ValidateHook(kind, payload);
        Task<JsonElement> handling;
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            handling = hookBridge.HandleAsync(
                BindExistingIdentity(payload),
                traceId,
                cancellationToken);
        }
        finally
        {
            lifecycleGate.Release();
        }
        return await handling;
    }

    private JsonElement BindExistingIdentity(JsonElement payload)
    {
        var runtime = OptionalRuntime(payload);
        var sessionId = RequiredString(payload, "session_id", 256);
        var suppliedTerminalId = OptionalString(payload, "managed_terminal_id");
        var suppliedElevated = OptionalBoolean(payload, "managed_terminal_elevated");
        var claimed = terminals.FindClaimBySession(sessionId);
        if (claimed is null)
        {
            if (suppliedTerminalId is not null || suppliedElevated is not null)
            {
                throw new InvalidOperationException(
                    "Hook 声明了未被目录认领的托管终端身份。");
            }
            return WithRuntime(payload, runtime);
        }

        if (!string.Equals(
                suppliedTerminalId,
                claimed.TerminalId,
                StringComparison.Ordinal) ||
            suppliedElevated != claimed.Elevated ||
            !string.Equals(runtime, claimed.Runtime, StringComparison.Ordinal) ||
            !CwdEquals(
                RequiredString(payload, "cwd", 32_768),
                claimed.Cwd))
        {
            throw new InvalidOperationException(
                "Hook 与托管终端的会话、目录、运行时或权限身份不一致。");
        }
        return WithManagedIdentity(
            payload,
            claimed.Runtime,
            claimed.TerminalId,
            claimed.Elevated);
    }

    private static void ValidateHook(
        BridgeManagedIngressKind kind,
        JsonElement payload)
    {
        var expectedEvent = kind switch
        {
            BridgeManagedIngressKind.SessionStart => "SessionStart",
            BridgeManagedIngressKind.SessionEnd => "SessionEnd",
            BridgeManagedIngressKind.Permission => "PermissionRequest",
            BridgeManagedIngressKind.RequestUserInput => "PreToolUse",
            BridgeManagedIngressKind.Stop => "Stop",
            BridgeManagedIngressKind.Activity => null,
            _ => throw new InvalidDataException("请求路径不是 Hook 入口。"),
        };
        var eventName = RequiredString(payload, "hook_event_name", 64);
        if (expectedEvent is not null &&
            !string.Equals(eventName, expectedEvent, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Hook 类型与请求路径不匹配。");
        }
        _ = RequiredString(payload, "session_id", 256);
        _ = RequiredString(payload, "cwd", 32_768);
        _ = OptionalRuntime(payload);
        ValidateManagedMetadata(payload);

        switch (kind)
        {
            case BridgeManagedIngressKind.SessionStart:
                _ = RequiredString(payload, "model", 512);
                var source = RequiredString(payload, "source", 32);
                if (source is not ("startup" or "resume" or "clear" or "compact"))
                {
                    throw new InvalidDataException("SessionStart 来源无效。");
                }
                break;
            case BridgeManagedIngressKind.SessionEnd:
                _ = RequiredString(payload, "reason", 512);
                break;
            case BridgeManagedIngressKind.Permission:
                _ = RequiredString(payload, "turn_id", 256);
                _ = RequiredString(payload, "model", 512);
                _ = RequiredString(payload, "tool_name", 512);
                if (OptionalString(payload, "tool_use_id") is null &&
                    OptionalString(payload, "turn_id") is null)
                {
                    throw new InvalidDataException("审批 Hook 缺少请求 ID。");
                }
                break;
            case BridgeManagedIngressKind.RequestUserInput:
                if (OptionalString(payload, "tool_use_id") is null &&
                    OptionalString(payload, "turn_id") is null)
                {
                    throw new InvalidDataException("补充问题 Hook 缺少请求 ID。");
                }
                if (OptionalString(payload, "tool_name") != "request_user_input" ||
                    !payload.TryGetProperty("tool_input", out var input) ||
                    input.ValueKind is not JsonValueKind.Object ||
                    !input.TryGetProperty("questions", out var questions) ||
                    questions.ValueKind is not JsonValueKind.Array ||
                    questions.GetArrayLength() == 0)
                {
                    throw new InvalidDataException("补充问题 Hook 内容无效。");
                }
                break;
            case BridgeManagedIngressKind.Activity:
                if (eventName is not (
                    "PreToolUse" or "PostToolUse" or "PostToolUseFailure" or
                    "PreCompact" or "PostCompact" or "UserPromptSubmit") ||
                    (eventName == "PreToolUse" &&
                     OptionalString(payload, "tool_name") == "request_user_input"))
                {
                    throw new InvalidDataException("Activity Hook 类型无效。");
                }
                break;
            case BridgeManagedIngressKind.Stop:
                _ = RequiredString(payload, "turn_id", 256);
                _ = RequiredString(payload, "model", 512);
                if (!payload.TryGetProperty("last_assistant_message", out var message) ||
                    message.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
                {
                    throw new InvalidDataException("Stop Hook 内容无效。");
                }
                break;
        }
    }

    private static void ValidateManagedMetadata(JsonElement payload)
    {
        var terminalId = OptionalString(payload, "managed_terminal_id");
        var elevated = OptionalBoolean(payload, "managed_terminal_elevated");
        if (terminalId is null && elevated is not null)
        {
            throw new InvalidDataException("托管终端权限标记缺少终端 ID。");
        }
    }

    private static JsonElement WithRuntime(JsonElement payload, string runtime)
    {
        var root = JsonNode.Parse(payload.GetRawText())!.AsObject();
        root["runtime"] = runtime;
        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonElement WithManagedIdentity(
        JsonElement payload,
        string runtime,
        string terminalId,
        bool elevated)
    {
        var root = JsonNode.Parse(payload.GetRawText())!.AsObject();
        root["runtime"] = runtime;
        root["managed_terminal_id"] = terminalId;
        root["managed_terminal_elevated"] = elevated;
        return JsonSerializer.SerializeToElement(root);
    }

    private static string RequiredRuntime(JsonElement payload) =>
        OptionalString(payload, "runtime") switch
        {
            RuntimeNames.Codex => RuntimeNames.Codex,
            RuntimeNames.ClaudeCode => RuntimeNames.ClaudeCode,
            _ => throw new InvalidDataException("托管终端运行时无效。"),
        };

    private static string OptionalRuntime(JsonElement payload) =>
        OptionalString(payload, "runtime") switch
        {
            null or RuntimeNames.Codex => RuntimeNames.Codex,
            RuntimeNames.ClaudeCode => RuntimeNames.ClaudeCode,
            _ => throw new InvalidDataException("Hook 运行时无效。"),
        };

    private static string RequiredString(
        JsonElement payload,
        string name,
        int maximumLength)
    {
        var value = OptionalString(payload, name);
        if (value is null ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw new InvalidDataException($"请求字段 {name} 无效。");
        }
        return value;
    }

    private static string? OptionalString(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value))
        {
            return null;
        }
        if (value.ValueKind is not JsonValueKind.String)
        {
            throw new InvalidDataException($"请求字段 {name} 类型无效。");
        }
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool RequiredBoolean(JsonElement payload, string name) =>
        OptionalBoolean(payload, name) ??
        throw new InvalidDataException($"请求字段 {name} 无效。");

    private static bool? OptionalBoolean(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value))
        {
            return null;
        }
        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidDataException($"请求字段 {name} 类型无效。");
        }
        return value.GetBoolean();
    }

    private static bool CwdEquals(string left, string right)
    {
        try
        {
            if (!Path.IsPathFullyQualified(left.Trim()) ||
                !Path.IsPathFullyQualified(right.Trim()))
            {
                throw new InvalidDataException("Hook 工作目录必须是绝对路径。");
            }
            var normalizedLeft = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(left.Trim()));
            var normalizedRight = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(right.Trim()));
            return string.Equals(
                normalizedLeft,
                normalizedRight,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception error) when (
            error is ArgumentException or IOException or NotSupportedException)
        {
            throw new InvalidDataException("Hook 工作目录无效。", error);
        }
    }

    private void EnsureActive()
    {
        if (options.OwnershipMode is not BridgeOwnershipMode.Active)
        {
            throw new InvalidOperationException(
                "托管 Hook 生产入口只能用于 Active Host。");
        }
    }
}
