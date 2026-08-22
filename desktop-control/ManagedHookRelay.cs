using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiCliFeishuControl;

internal static class ManagedHookRelay
{
    private const string DefaultBridgeUrl = "http://127.0.0.1:8765";
    private static readonly TimeSpan PresenceTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PresenceJoinGrace = TimeSpan.FromMilliseconds(125);
    internal const string ControlTokenHeader = "X-AI-CLI-Feishu-Control-Token";
    internal const string TerminalSecretHeader = "X-AI-CLI-Feishu-Terminal-Secret";
    private const uint SnapshotProcesses = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static int Run(string[] args)
    {
        HookProfile? profile = null;
        Task<JsonObject>? execution = null;
        try
        {
            profile = HookProfile.Parse(args);
            var processBudget = profile.ProcessBudget;
            using var cancellation = processBudget is { } budget
                ? new CancellationTokenSource(budget)
                : new CancellationTokenSource();
            execution = RunAsync(profile, args, cancellation.Token);
            var result = processBudget is { } hardDeadline
                ? execution.WaitAsync(hardDeadline).GetAwaiter().GetResult()
                : execution.GetAwaiter().GetResult();
            if (profile.WritesOutput)
            {
                WriteOutput(result);
            }
        }
        catch (Exception error) when (
            profile?.QuietFailure == true &&
            error is OperationCanceledException or TimeoutException or
                HttpRequestException or IOException)
        {
            ObserveFault(execution);
            if (profile.WritesOutput)
            {
                WriteOutput(new JsonObject());
            }
        }
        catch (Exception error)
        {
            ObserveFault(execution);
            Console.Error.WriteLine($"[ai-cli-feishu] C# Hook relay skipped: {error.Message}");
            if (profile?.WritesOutput is not false)
            {
                WriteOutput(new JsonObject());
            }
        }
        return 0;
    }

    private static async Task<JsonObject> RunAsync(
        HookProfile profile,
        string[] args,
        CancellationToken cancellationToken)
    {
        var root = BridgeRoot(args);
        var payload = await ReadInputAsync(cancellationToken);
        if (profile.Runtime == "claudecode")
        {
            payload = NormalizeClaudeCodePayload(payload, DateTimeOffset.UtcNow);
        }
        AddManagedTerminalMetadata(payload);
        AddClientProcessMetadata(payload);

        var action = profile.Resolve(payload);
        if (action.Skip)
        {
            return new JsonObject();
        }
        if (action.CompactActivity)
        {
            payload = CompactActivityPayload(payload);
        }

        var bypassed = IsBypassed(root);
        HookAuthentication authentication;
        try
        {
            authentication = ResolveAuthentication(payload, root);
        }
        catch when (bypassed)
        {
            return new JsonObject();
        }

        var baseUrl = BridgeUrl(args);
        using var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        if (bypassed)
        {
            await PostLocalPresenceAsync(
                client,
                authentication,
                payload,
                cancellationToken);
            return new JsonObject();
        }

        return await DispatchAsync(
            client,
            authentication,
            payload,
            action,
            cancellationToken);
    }

    private static async Task<JsonObject> ReadInputAsync(
        CancellationToken cancellationToken)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        var text = (await Console.In.ReadToEndAsync(cancellationToken)).Trim();
        if (text.Length == 0)
        {
            return new JsonObject();
        }
        return JsonNode.Parse(text) as JsonObject ??
            throw new InvalidDataException("Hook 输入必须是 JSON 对象。");
    }

    internal static JsonObject NormalizeClaudeCodePayload(
        JsonObject input,
        DateTimeOffset now)
    {
        var item = (JsonObject)input.DeepClone();
        item["runtime"] = "claudecode";
        if (String(item, "model") is null)
        {
            item["model"] = "claude-code";
        }

        var eventName = String(item, "hook_event_name") ?? string.Empty;
        if (eventName is "PermissionRequest" or "PreToolUse" or "PostToolUse" or
            "PostToolUseFailure" or "Stop" &&
            String(item, "turn_id") is null)
        {
            var eventId = String(item, "tool_use_id") ??
                $"{eventName.ToLowerInvariant()}-{now.ToUnixTimeMilliseconds()}";
            item["turn_id"] =
                $"claudecode-{String(item, "session_id") ?? "unknown"}-{eventId}";
        }

        switch (eventName)
        {
            case "SessionStart":
                if (String(item, "source") is not ("startup" or "resume" or "clear" or "compact"))
                {
                    item["source"] = "startup";
                }
                break;
            case "SessionEnd":
                if (String(item, "reason") is null)
                {
                    item["reason"] = "other";
                }
                break;
            case "Stop":
                if (!item.TryGetPropertyValue("last_assistant_message", out var message) ||
                    message is not null && !TryString(message, out _))
                {
                    item["last_assistant_message"] = null;
                }
                break;
            case "UserPromptSubmit":
                if (String(item, "prompt") is null &&
                    String(item, "user_prompt") is { } prompt)
                {
                    item["prompt"] = prompt;
                }
                break;
            case "PreToolUse" when String(item, "tool_name") == "AskUserQuestion":
                NormalizeClaudeCodeQuestions(item);
                break;
        }
        return item;
    }

    private static void NormalizeClaudeCodeQuestions(JsonObject item)
    {
        if (item["tool_input"] is not JsonObject originalInput ||
            originalInput["questions"] is not JsonArray originalQuestions ||
            originalQuestions.Count == 0)
        {
            return;
        }

        var questions = new JsonArray();
        var questionTextById = new JsonObject();
        for (var index = 0; index < originalQuestions.Count; index++)
        {
            if (originalQuestions[index] is not JsonObject originalQuestion ||
                String(originalQuestion, "question") is not { } questionText)
            {
                continue;
            }
            var id = $"claude_question_{index + 1}";
            questionTextById[id] = questionText;
            var options = new JsonArray();
            if (originalQuestion["options"] is JsonArray originalOptions)
            {
                foreach (var node in originalOptions)
                {
                    if (node is not JsonObject originalOption ||
                        String(originalOption, "label") is not { } label)
                    {
                        continue;
                    }
                    var option = new JsonObject
                    {
                        ["label"] = label,
                        ["description"] = String(originalOption, "description") ?? string.Empty,
                    };
                    if (String(originalOption, "preview") is { } preview)
                    {
                        option["preview"] = preview;
                    }
                    options.Add(option);
                }
            }
            questions.Add(new JsonObject
            {
                ["header"] = String(originalQuestion, "header") ?? $"问题 {index + 1}",
                ["id"] = id,
                ["question"] = questionText,
                ["options"] = options,
                ["multiple"] = Boolean(originalQuestion, "multiSelect"),
                ["custom"] = true,
            });
        }
        if (questions.Count == 0)
        {
            return;
        }

        item["claude_code_tool_name"] = "AskUserQuestion";
        item["tool_name"] = "request_user_input";
        item["tool_input"] = new JsonObject
        {
            ["questions"] = questions,
            ["claudeCodeOriginalInput"] = originalInput.DeepClone(),
            ["claudeCodeQuestionTextById"] = questionTextById,
        };
    }

    internal static JsonObject CompactActivityPayload(JsonObject value)
    {
        var compact = new JsonObject();
        foreach (var name in new[]
                 {
                     "hook_event_name",
                     "session_id",
                     "turn_id",
                     "cwd",
                     "model",
                     "prompt",
                     "tool_name",
                     "tool_use_id",
                     "runtime",
                     "transcript_path",
                     "managed_terminal_id",
                     "managed_terminal_elevated",
                     "client_process_id",
                     "client_process_started_at",
                 })
        {
            if (value.TryGetPropertyValue(name, out var node))
            {
                compact[name] = node?.DeepClone();
            }
        }
        if (value.TryGetPropertyValue("tool_input", out var toolInput))
        {
            compact["tool_preview"] = CompactPreview(toolInput);
        }
        foreach (var name in new[]
                 {
                     "tool_response",
                     "tool_result",
                     "tool_output",
                     "error",
                     "summary",
                 })
        {
            if (!value.TryGetPropertyValue(name, out var response))
            {
                continue;
            }
            compact["tool_response_preview"] = CompactPreview(response);
            break;
        }
        return compact;
    }

    private static string CompactPreview(JsonNode? value)
    {
        var text = value?.ToJsonString() ?? "null";
        return text.Length <= 1_200
            ? text
            : $"{text[..1_180]}…（已截断）";
    }

    private static void AddManagedTerminalMetadata(JsonObject input)
    {
        var terminalId = Environment.GetEnvironmentVariable(
            "AI_CLI_FEISHU_MANAGED_TERMINAL_ID")?.Trim();
        if (string.IsNullOrWhiteSpace(terminalId))
        {
            return;
        }
        input["managed_terminal_id"] = terminalId;
        input["managed_terminal_elevated"] =
            Environment.GetEnvironmentVariable(
                "AI_CLI_FEISHU_MANAGED_TERMINAL_ELEVATED") == "1";
    }

    private static void AddClientProcessMetadata(JsonObject input)
    {
        if (String(input, "managed_terminal_id") is not null ||
            Integer(input, "client_process_id") is > 0)
        {
            return;
        }
        var client = CaptureAssistantAncestor();
        if (client is null)
        {
            return;
        }
        input["client_process_id"] = client.ProcessId;
        if (client.StartedAt is not null)
        {
            input["client_process_started_at"] = client.StartedAt;
        }
    }

    private static ClientProcess? CaptureAssistantAncestor()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
        {
            return null;
        }
        try
        {
            var entries = new Dictionary<int, ProcessEntry>();
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
            };
            if (!Process32First(snapshot, ref entry))
            {
                return null;
            }
            do
            {
                entries[(int)entry.ProcessId] = new(
                    (int)entry.ProcessId,
                    (int)entry.ParentProcessId,
                    Path.GetFileNameWithoutExtension(entry.ExecutableFile));
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));

            var match = FindAssistantAncestor(Environment.ProcessId, entries.Values);
            if (match is null)
            {
                return null;
            }
            string? startedAt = null;
            try
            {
                using var process = Process.GetProcessById(match.ProcessId);
                startedAt = process.StartTime.ToUniversalTime().ToString("O");
            }
            catch (Exception error) when (
                error is ArgumentException or InvalidOperationException or
                System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
            return new(match.ProcessId, startedAt);
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    internal static ProcessEntry? FindAssistantAncestor(
        int startProcessId,
        IEnumerable<ProcessEntry> processes)
    {
        var byId = processes.ToDictionary(item => item.ProcessId);
        var visited = new HashSet<int>();
        var currentId = startProcessId;
        for (var depth = 0; depth < 16 && currentId > 0; depth++)
        {
            if (!visited.Add(currentId) || !byId.TryGetValue(currentId, out var current))
            {
                break;
            }
            if (current.Name is not null &&
                (current.Name.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
                 current.Name.Equals("claude", StringComparison.OrdinalIgnoreCase)))
            {
                return current;
            }
            currentId = current.ParentProcessId;
        }
        return null;
    }

    private static bool IsBypassed(string bridgeRoot)
    {
        if (Environment.GetEnvironmentVariable("AI_CLI_FEISHU_HOOK_BYPASS") == "1")
        {
            return true;
        }
        var threadId = Environment.GetEnvironmentVariable("CODEX_THREAD_ID")?.Trim();
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return false;
        }
        try
        {
            var marker = JsonNode.Parse(File.ReadAllText(
                Path.Combine(bridgeRoot, "data", "codex-hook-bypass.json")));
            var values = marker switch
            {
                JsonArray array => array,
                JsonObject item when item["threadIds"] is JsonArray array => array,
                _ => null,
            };
            return values?.Any(value =>
                value is JsonValue item &&
                item.TryGetValue<string>(out var candidate) &&
                candidate == threadId) == true;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    internal static HookAuthentication ResolveAuthentication(
        JsonObject payload,
        string bridgeRoot)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (String(payload, "managed_terminal_id") is not null)
        {
            var terminalSecret = Environment.GetEnvironmentVariable(
                "AI_CLI_FEISHU_MANAGED_TERMINAL_SECRET")?.Trim();
            if (!IsHexToken(terminalSecret))
            {
                throw new InvalidDataException(
                    "Managed terminal secret is missing or invalid.");
            }
            return new(TerminalSecretHeader, terminalSecret!);
        }
        return new(ControlTokenHeader, ReadControlToken(bridgeRoot));
    }

    private static string ReadControlToken(string bridgeRoot)
        => BridgeControlTokenReader.Read(bridgeRoot);

    private static bool IsHexToken(string? value) =>
        value?.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static async Task PostLocalPresenceAsync(
        HttpClient client,
        HookAuthentication authentication,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        var sessionId = String(payload, "session_id");
        var processId = Integer(payload, "client_process_id");
        if (sessionId is null || processId is null or <= 0)
        {
            return;
        }
        try
        {
            var body = new JsonObject
            {
                ["session_id"] = sessionId,
                ["cwd"] = payload["cwd"]?.DeepClone(),
                ["runtime"] = payload["runtime"]?.DeepClone(),
                ["client_process_id"] = processId,
                ["client_process_started_at"] =
                    payload["client_process_started_at"]?.DeepClone(),
            };
            _ = await PostAsync(
                client,
                "hooks/local-presence",
                authentication,
                body,
                PresenceTimeout,
                cancellationToken);
        }
        catch
        {
            // Local presence is best effort and must not block the actual Hook.
        }
    }

    internal static async Task<JsonObject> DispatchAsync(
        HttpClient client,
        HookAuthentication authentication,
        JsonObject payload,
        HookAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(action);

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        if (action.ExecutionBudget is { } budget)
        {
            execution.CancelAfter(budget);
        }
        using var presenceCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            execution.Token);
        presenceCancellation.CancelAfter(PresenceTimeout);
        var presence = PostLocalPresenceAsync(
            client,
            authentication,
            payload,
            presenceCancellation.Token);
        try
        {
            var response = await PostAsync(
                client,
                action.Path,
                authentication,
                payload,
                action.Timeout,
                execution.Token);
            if (action.Path == "hooks/session-start")
            {
                await PostLocalPresenceAsync(
                    client,
                    authentication,
                    payload,
                    execution.Token);
            }
            return response;
        }
        catch (Exception error) when (
            action.BestEffort &&
            error is OperationCanceledException or HttpRequestException or
                IOException or JsonException)
        {
            return new JsonObject();
        }
        finally
        {
            await CompletePresenceAsync(
                presence,
                presenceCancellation,
                execution.Token);
        }
    }

    private static async Task CompletePresenceAsync(
        Task presence,
        CancellationTokenSource cancellation,
        CancellationToken executionToken)
    {
        try
        {
            if (presence.IsCompleted)
            {
                await presence;
            }
            else if (!executionToken.IsCancellationRequested)
            {
                await presence.WaitAsync(PresenceJoinGrace, executionToken);
            }
        }
        catch
        {
            // Local presence never decides whether the actual Hook succeeded.
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    private static async Task<JsonObject> PostAsync(
        HttpClient client,
        string path,
        HookAuthentication authentication,
        JsonObject payload,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add(authentication.HeaderName, authentication.Value);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        using var response = await client.SendAsync(
            request,
            timeoutCancellation.Token);
        var text = await response.Content.ReadAsStringAsync(timeoutCancellation.Token);
        if (!response.IsSuccessStatusCode)
        {
            var detail = text.Length <= 300 ? text : text[..300];
            throw new HttpRequestException(
                $"Bridge hook /{path} returned HTTP {(int)response.StatusCode}" +
                (detail.Length == 0 ? "." : $": {detail}"));
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }
        return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }

    private static void ObserveFault(Task? task)
    {
        if (task is null || task.IsCompletedSuccessfully || task.IsCanceled)
        {
            return;
        }
        if (task.IsFaulted)
        {
            _ = task.Exception;
            return;
        }
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously |
                TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static string BridgeRoot(string[] args)
    {
        var configured = Argument(args, "--bridge-root") ??
            BridgeLocatorValue(args, "bridgeRoot") ??
            Environment.GetEnvironmentVariable("AI_CLI_FEISHU_BRIDGE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 10; depth++, current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "data")) &&
                (File.Exists(Path.Combine(current.FullName, "AiCliFeishuBridgeHost.exe")) ||
                 Directory.Exists(Path.Combine(current.FullName, "bridge-dotnet"))))
            {
                return current.FullName;
            }
        }
        throw new DirectoryNotFoundException("找不到 AI CLI 飞书助手目录。");
    }

    private static string BridgeUrl(string[] args) =>
        Argument(args, "--bridge-url") ??
        BridgeLocatorValue(args, "bridgeUrl") ??
        Environment.GetEnvironmentVariable("AI_CLI_FEISHU_BRIDGE_URL") ??
        DefaultBridgeUrl;

    private static string? BridgeLocatorValue(string[] args, string propertyName)
    {
        var path = Argument(args, "--bridge-config") ??
            Environment.GetEnvironmentVariable("AI_CLI_FEISHU_HOOK_CONFIG");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var locator = ReadHookRelayLocator(path);
        return propertyName switch
        {
            "bridgeRoot" => locator.BridgeRoot,
            "bridgeUrl" => locator.BridgeUrl,
            _ => null,
        };
    }

    internal static HookRelayLocator ReadHookRelayLocator(string path)
    {
        var document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ??
            throw new InvalidDataException("Hook locator 必须是 JSON 对象。");
        var schemaVersion = Integer(document, "schemaVersion");
        var bridgeRoot = String(document, "bridgeRoot");
        var bridgeUrl = String(document, "bridgeUrl");
        if (schemaVersion != 1 || bridgeRoot is null || bridgeUrl is null)
        {
            throw new InvalidDataException("Hook locator 缺少有效的 schemaVersion、bridgeRoot 或 bridgeUrl。");
        }
        if (!Uri.TryCreate(bridgeUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsLoopback)
        {
            throw new InvalidDataException("Hook locator 的 bridgeUrl 必须是本机 HTTP 地址。");
        }
        return new(schemaVersion.Value, bridgeRoot, bridgeUrl);
    }

    internal static string? ClaudeHookKind(string? eventName) =>
        eventName?.ToLowerInvariant() switch
        {
            "sessionstart" => "session-start",
            "sessionend" => "session-end",
            "permissionrequest" => "permission",
            "pretooluse" => "pre-tool-use",
            "posttooluse" => "post-tool-use",
            "posttoolusefailure" => "activity",
            "precompact" or "postcompact" or "userpromptsubmit" => "activity",
            "stop" => "stop",
            _ => null,
        };

    private static string? Argument(string[] args, string name)
    {
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return null;
    }

    private static string? String(JsonObject item, string name) =>
        item[name] is JsonValue value && value.TryGetValue<string>(out var text) &&
        !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static bool TryString(JsonNode value, out string text)
    {
        text = string.Empty;
        if (value is not JsonValue item ||
            !item.TryGetValue<string>(out var candidate) ||
            candidate is null)
        {
            return false;
        }
        text = candidate;
        return true;
    }

    private static int? Integer(JsonObject item, string name) =>
        item[name] is JsonValue value && value.TryGetValue<int>(out var number)
            ? number
            : null;

    private static bool Boolean(JsonObject item, string name) =>
        item[name] is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    private static void WriteOutput(JsonObject value)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.Out.Write(value.ToJsonString());
    }

    internal sealed record ProcessEntry(
        int ProcessId,
        int ParentProcessId,
        string Name);

    internal sealed record HookRelayLocator(
        int SchemaVersion,
        string BridgeRoot,
        string BridgeUrl);

    internal sealed record HookAuthentication(string HeaderName, string Value);

    private sealed record ClientProcess(int ProcessId, string? StartedAt);

    internal sealed record HookAction(
        string Path,
        TimeSpan Timeout,
        bool CompactActivity = false,
        bool Skip = false,
        bool BestEffort = false,
        TimeSpan? ExecutionBudget = null);

    private sealed record HookProfile(
        string Runtime,
        string? Kind,
        bool WritesOutput)
    {
        public TimeSpan? ProcessBudget => Runtime == "codex" ? Kind switch
        {
            "activity" => TimeSpan.FromSeconds(3),
            "session-end" => TimeSpan.FromMilliseconds(2_400),
            _ => null,
        } : null;

        public bool QuietFailure => Runtime == "codex" &&
            Kind is "activity" or "session-end";

        public static HookProfile Parse(string[] args)
        {
            var marker = Array.FindIndex(args, item =>
                string.Equals(item, "--bridge-hook", StringComparison.OrdinalIgnoreCase));
            if (marker < 0 || marker + 1 >= args.Length)
            {
                throw new InvalidOperationException("--bridge-hook 缺少运行时或事件类型。");
            }
            var runtime = args[marker + 1].ToLowerInvariant();
            var kind = marker + 2 < args.Length &&
                !args[marker + 2].StartsWith("--", StringComparison.Ordinal)
                    ? args[marker + 2].ToLowerInvariant()
                    : null;
            if (runtime is not ("codex" or "claudecode"))
            {
                throw new InvalidOperationException("Hook 运行时只接受 codex 或 claudecode。");
            }
            return new(runtime, kind, runtime != "codex" || kind != "session-end");
        }

        public HookAction Resolve(JsonObject payload)
        {
            var kind = Kind ?? HookKindFromEvent(payload) ??
                throw new InvalidOperationException(
                    "Hook 命令未提供事件类型，且输入中没有可识别的 hook_event_name。");
            if (Runtime == "codex")
            {
                return kind switch
                {
                    "session-start" => new("hooks/session-start", TimeSpan.FromSeconds(5)),
                    "session-end" => new(
                        "hooks/session-end",
                        TimeSpan.FromMilliseconds(1_500),
                        BestEffort: true,
                        ExecutionBudget: TimeSpan.FromSeconds(2)),
                    "permission" => new(
                        "hooks/permission",
                        EnvironmentTimeout("AI_CLI_FEISHU_PERMISSION_HTTP_TIMEOUT_MS", 1_230_000)),
                    "input" => new(
                        "hooks/request-user-input",
                        EnvironmentTimeout("AI_CLI_FEISHU_INPUT_HTTP_TIMEOUT_MS", 1_230_000)),
                    "activity" => new(
                        "hooks/activity",
                        TimeSpan.FromMilliseconds(1_750),
                        CompactActivity: true,
                        BestEffort: true,
                        ExecutionBudget: TimeSpan.FromMilliseconds(2_250)),
                    "stop" => new("hooks/stop", TimeSpan.FromSeconds(10)),
                    _ => throw new InvalidOperationException($"未知 Codex Hook 类型 {kind}。"),
                };
            }

            return kind switch
            {
                "session-start" => new("hooks/session-start", TimeSpan.FromSeconds(5)),
                "session-end" => new(
                    "hooks/session-end",
                    TimeSpan.FromMilliseconds(1_750),
                    BestEffort: true,
                    ExecutionBudget: TimeSpan.FromMilliseconds(2_250)),
                "permission" when String(payload, "tool_name") == "AskUserQuestion" =>
                    new(string.Empty, TimeSpan.Zero, Skip: true),
                "permission" => new("hooks/permission", TimeSpan.FromMilliseconds(1_500_000)),
                "pre-tool-use" when String(payload, "tool_name") == "request_user_input" =>
                    new("hooks/request-user-input", TimeSpan.FromMilliseconds(1_500_000)),
                "pre-tool-use" => new(
                    "hooks/activity",
                    TimeSpan.FromMilliseconds(1_750),
                    CompactActivity: true,
                    BestEffort: true,
                    ExecutionBudget: TimeSpan.FromMilliseconds(2_250)),
                "post-tool-use" or "activity" or "user-prompt-submit" =>
                    new(
                        "hooks/activity",
                        TimeSpan.FromMilliseconds(1_750),
                        CompactActivity: true,
                        BestEffort: true,
                        ExecutionBudget: TimeSpan.FromMilliseconds(2_250)),
                "stop" => new("hooks/stop", TimeSpan.FromSeconds(20)),
                _ => throw new InvalidOperationException($"未知 Claude Code Hook 类型 {kind}。"),
            };
        }

        private string? HookKindFromEvent(JsonObject payload) =>
            Runtime == "claudecode"
                ? ClaudeHookKind(String(payload, "hook_event_name"))
                : null;

        private static TimeSpan EnvironmentTimeout(string name, int fallbackMilliseconds)
        {
            var configured = Environment.GetEnvironmentVariable(name);
            return int.TryParse(configured, out var milliseconds) && milliseconds > 0
                ? TimeSpan.FromMilliseconds(milliseconds)
                : TimeSpan.FromMilliseconds(fallbackMilliseconds);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
