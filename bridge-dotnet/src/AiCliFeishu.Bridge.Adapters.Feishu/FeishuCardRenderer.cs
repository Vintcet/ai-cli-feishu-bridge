using System.Text.Json.Nodes;
using System.Globalization;
using System.Text.RegularExpressions;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Adapters.Feishu;

public sealed partial class FeishuCardRenderer : IFeishuCardRenderer
{
    private const int ErrorChunkLength = 2_800;

    public FeishuCardView CommandMenu() => Card(
        "blue",
        "AI CLI 飞书助手命令",
        [
            Markdown("请选择要执行的桥接命令。这些命令属于飞书助手，不依赖某个活跃 CLI 会话。"),
            ActionRow(
                Button("primary", "新建", CommandValue(FeishuCardActions.CommandNew)),
                Button("default", "会话管理", CommandValue(FeishuCardActions.CommandSessions)),
                Button("default", "状态", CommandValue(FeishuCardActions.CommandStatus))),
            ActionRow(
                Button("default", "工作区", CommandValue(FeishuCardActions.CommandWorkspace)),
                Button("default", "会话别名", CommandValue(FeishuCardActions.CommandAliases)),
                Button("default", "帮助", CommandValue(FeishuCardActions.CommandHelp))),
            Note("也可以直接发送 /新建、/会话、/状态、/工作区、/别名或 /帮助。"),
        ]);

    public FeishuCardView RuntimeSelection(
        string? workspaceRoot,
        FeishuRuntimeNewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateContext(context);
        return Card(
            "blue",
            "新建 AI CLI 会话",
            [
                Markdown(string.IsNullOrWhiteSpace(workspaceRoot)
                    ? "请选择运行环境。\n**默认工作区：** 尚未设置，请先在电脑端设置。"
                    : $"请选择运行环境。\n**默认工作区：** {workspaceRoot}"),
                ActionRow(
                    RuntimeButton("primary", "Codex", "codex", context),
                    RuntimeButton("default", "Claude Code", "claudecode", context),
                    RuntimeButton("default", "OpenCode", "opencode", context)),
                Note("三个运行环境都是 /新建 的二级选项。"),
            ]);
    }

    public FeishuCardView RuntimeProjectForm(
        string runtime,
        string? workspaceRoot,
        FeishuRuntimeNewContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateRuntime(runtime);
        ValidateContext(context);
        var displayName = RuntimeName(runtime);
        var value = RuntimeValue(FeishuCardActions.RuntimeNewSubmit, runtime, context);
        var cancelValue = RuntimeValue(FeishuCardActions.RuntimeNewCancel, runtime, context);
        var form = new JsonObject
        {
            ["tag"] = "form",
            ["name"] = "runtime_new_form",
            ["elements"] = new JsonArray(
                new JsonObject
                {
                    ["tag"] = "input",
                    ["name"] = "project_name",
                    ["required"] = true,
                    ["input_type"] = "text",
                    ["max_length"] = 80,
                    ["width"] = "fill",
                    ["label"] = new JsonObject
                    {
                        ["tag"] = "plain_text",
                        ["content"] = "项目名",
                    },
                    ["label_position"] = "top",
                    ["placeholder"] = new JsonObject
                    {
                        ["tag"] = "plain_text",
                        ["content"] = "输入项目文件夹名称，例如：我的项目",
                    },
                },
                new JsonObject
                {
                    ["tag"] = "column_set",
                    ["flex_mode"] = "none",
                    ["horizontal_spacing"] = "default",
                    ["columns"] = new JsonArray(
                        FormColumn(Button(
                            "primary",
                            "确认新建",
                            value,
                            true,
                            "form_submit",
                            "runtime_new_submit")),
                        FormColumn(Button(
                            "default",
                            "取消",
                            cancelValue,
                            true,
                            name: "runtime_new_cancel"))),
                }),
        };
        return Card(
            "blue",
            $"新建 {displayName} 会话",
            [
                Markdown(string.IsNullOrWhiteSpace(workspaceRoot)
                    ? $"**运行环境：** {displayName}\n**默认工作区：** 尚未设置，请先在电脑端设置。"
                    : $"**运行环境：** {displayName}\n**默认工作区：** {workspaceRoot}"),
                form,
            ]);
    }

    public FeishuCardView RuntimeLaunchSubmitted(
        string runtime,
        string projectName,
        string workspaceRoot)
    {
        ValidateRuntime(runtime);
        if (string.IsNullOrWhiteSpace(projectName))
        {
            throw new ArgumentException("项目名不能为空。", nameof(projectName));
        }
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("工作区不能为空。", nameof(workspaceRoot));
        }
        return Card(
            "green",
            "已提交新建请求",
            [Markdown(
                $"**运行环境：** {RuntimeName(runtime)}\n**项目名：** {projectName.Trim()}" +
                $"\n**工作区：** {workspaceRoot.Trim()}\n\n" +
                "桌面助手正在处理启动请求，后续结果会继续发送到当前私聊。")]);
    }

    public FeishuCardView RuntimeLaunchCancelled(string runtime)
    {
        ValidateRuntime(runtime);
        return Card(
            "grey",
            "已取消新建",
            [Markdown($"未创建 {RuntimeName(runtime)} 会话。需要时可再次发送 /新建。")]);
    }

    public FeishuCardView PendingApproval(
        FeishuSessionView session,
        FeishuApprovalView approval)
    {
        var runtime = RuntimeName(session.Runtime);
        var highRisk = approval.RiskLevel == "high";
        var summary = $"**会话：** {session.Label}\n**工具：** {approval.ToolName}" +
            $"\n**目录：** {session.Cwd}" +
            (highRisk
                ? $"\n**风险：** 高（{approval.RiskReason ?? "命中高风险规则"}）"
                : "");
        return Card(
            highRisk ? "red" : "orange",
            highRisk ? $"{runtime} 高风险操作需要确认" : $"{runtime} 需要你的确认",
            [
                Markdown(summary),
                new JsonObject { ["tag"] = "hr" },
                Markdown($"**请求内容**\n```\n{Truncate(approval.ToolPreview, 2_600)}\n```"),
                Note("审批默认只在飞书等待；需要电脑端窗口时，请点击“转回 PC 审批”。"),
                ActionRow(
                    Button("primary", "批准一次", new()
                    {
                        ["action"] = FeishuCardActions.ApprovalAllow,
                        ["requestId"] = approval.RequestId,
                        ["sessionId"] = session.SessionId,
                    }),
                    Button("danger", "拒绝", new()
                    {
                        ["action"] = FeishuCardActions.ApprovalDeny,
                        ["requestId"] = approval.RequestId,
                        ["sessionId"] = session.SessionId,
                    }),
                    Button("default", "转回 PC 审批", new()
                    {
                        ["action"] = FeishuCardActions.ApprovalDesktop,
                        ["requestId"] = approval.RequestId,
                        ["sessionId"] = session.SessionId,
                    })),
            ]);
    }

    public FeishuCardView ResolvedApproval(
        FeishuSessionView session,
        FeishuApprovalView approval,
        string resolution,
        string status)
    {
        if (!ApprovalResolutions.All.Contains(resolution))
        {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }
        if (status is not ApprovalStatuses.Resolved and not ApprovalStatuses.Orphaned)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        var runtime = RuntimeName(session.Runtime);
        var template = status == ApprovalStatuses.Orphaned
            ? "grey"
            : resolution switch
        {
            ApprovalResolutions.Allow => "green",
            ApprovalResolutions.Deny => "red",
            _ => "grey",
        };
        var result = status == ApprovalStatuses.Orphaned
            ? "审批已失效，无需再处理。"
            : resolution switch
        {
            ApprovalResolutions.Allow => $"已批准，{runtime} 将继续执行。",
            ApprovalResolutions.Deny => $"已拒绝，{runtime} 会收到拒绝结果。",
            ApprovalResolutions.Local => "已在电脑端处理，无需再操作。",
            _ => "审批已过期，无需再处理。",
        };
        return Card(
            template,
            $"{runtime} 审批已处理",
            [Markdown($"**会话：** {session.Label}\n**工具：** {approval.ToolName}\n\n{result}")]);
    }

    public FeishuCardView DeferredApproval(
        FeishuSessionView session,
        FeishuApprovalView approval)
    {
        var runtime = RuntimeName(session.Runtime);
        return Card(
            "blue",
            $"{runtime} 已转回 PC 审批",
            [Markdown(
                $"**会话：** {session.Label}\n**工具：** {approval.ToolName}\n\n" +
                "已通知 AI CLI 飞书助手，请在电脑端审批窗口处理。")]);
    }

    public FeishuCardView PendingInput(
        FeishuSessionView session,
        string requestId,
        FeishuInputQuestionView question,
        int questionIndex,
        int questionCount,
        IReadOnlyList<string>? selectedAnswers = null,
        string? selectionKey = null)
    {
        ValidateQuestionPosition(questionIndex, questionCount);
        var runtime = RuntimeName(session.Runtime);
        var elements = new List<JsonNode?>
        {
            Markdown(
                $"**会话：** {session.Label}\n**{Truncate(question.Header, 80)}**\n" +
                Truncate(question.Question, 800)),
        };
        var actions = new List<JsonObject>();
        var selected = selectedAnswers?.ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var option in question.Options)
        {
            actions.Add(Button("default", question.Multiple && selected.Contains(option)
                ? $"✓ {Truncate(option, 36)}"
                : Truncate(option, 40), new()
                {
                    ["action"] = question.Multiple
                        ? FeishuCardActions.InputToggle
                        : FeishuCardActions.InputAnswer,
                    ["requestId"] = requestId,
                    ["sessionId"] = session.SessionId,
                    ["questionId"] = question.Id,
                    ["answer"] = option,
                }));
            AddSelectionKey(actions[^1], selectionKey);
        }
        if (question.Multiple)
        {
            actions.Add(Button("primary", "提交选择", new()
            {
                ["action"] = FeishuCardActions.InputSubmit,
                ["requestId"] = requestId,
                ["sessionId"] = session.SessionId,
                ["questionId"] = question.Id,
            }));
            AddSelectionKey(actions[^1], selectionKey);
        }
        actions.Add(Button("default", "转回本机回答", new()
        {
            ["action"] = FeishuCardActions.InputLocal,
            ["requestId"] = requestId,
            ["sessionId"] = session.SessionId,
            ["questionId"] = question.Id,
        }));
        AddSelectionKey(actions[^1], selectionKey);
        foreach (var row in actions.Chunk(3))
        {
            elements.Add(ActionRow(row.ToArray()));
        }
        elements.Add(Note(question.Options.Count == 0
            ? "请引用本卡片回复文字，或转回本机回答。"
            : question.Multiple
                ? "点击选项进行多选，完成后点击“提交选择”。"
                : "点击一个选项即可提交；也可以引用本卡片回复自定义答案。"));
        return Card(
            "orange",
            $"{runtime} 等待你回答（{questionIndex + 1}/{questionCount}）",
            elements);
    }

    public FeishuCardView RecordedInput(
        FeishuSessionView session,
        FeishuInputQuestionView question,
        IReadOnlyList<string> answers,
        int remainingQuestions,
        int questionIndex,
        int questionCount)
    {
        ValidateQuestionPosition(questionIndex, questionCount);
        ArgumentNullException.ThrowIfNull(answers);
        if (remainingQuestions < 1 || remainingQuestions >= questionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingQuestions));
        }
        var runtime = RuntimeName(session.Runtime);
        var result = question.IsSecret
            ? "已提供（已隐藏）"
            : Truncate(string.Join("、", answers), 500);
        return Card(
            "blue",
            $"{runtime} 已记录回答（{questionIndex + 1}/{questionCount}）",
            [Markdown(
                $"**会话：** {session.Label}\n**{Truncate(question.Header, 80)}**\n" +
                $"{Truncate(question.Question, 800)}\n\n**已记录：** {result}\n" +
                $"还剩 {remainingQuestions} 个问题。")]);
    }

    public FeishuCardView ResolvedInput(
        FeishuSessionView session,
        FeishuInputQuestionView question,
        IReadOnlyList<string>? answers,
        string resolution,
        int questionIndex,
        int questionCount)
    {
        ValidateQuestionPosition(questionIndex, questionCount);
        if (resolution is not ("answered" or "local" or "timeout" or "rejected"))
        {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }
        var runtime = RuntimeName(session.Runtime);
        var result = resolution switch
        {
            "answered" when question.IsSecret => "已提供（已隐藏）",
            "answered" => Truncate(string.Join("、", answers ?? []) is { Length: > 0 } text
                ? text
                : "（空）", 500),
            "local" => $"已转回电脑端，请在原 {runtime} 窗口回答。",
            "rejected" => $"已在原 {runtime} 窗口取消这组问题。",
            _ => "飞书回答已超时，已转回电脑端。",
        };
        return Card(
            resolution == "answered" ? "green" : "grey",
            $"{runtime} 补充信息{(resolution == "answered" ? "已提交" : "已处理")}" +
            $"（{questionIndex + 1}/{questionCount}）",
            [Markdown(
                $"**会话：** {session.Label}\n**{Truncate(question.Header, 80)}**\n" +
                $"{Truncate(question.Question, 800)}\n\n**结果：** {result}")]);
    }

    public IReadOnlyList<FeishuCardView> RuntimeError(
        FeishuSessionView session,
        string error,
        FeishuRuntimeRetryView? retry = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (retry is not null)
        {
            ValidateRetry(retry);
        }
        var runtime = RuntimeName(session.Runtime);
        var chunks = SplitError(RedactSensitiveText(error));
        return chunks.Select((chunk, index) =>
        {
            var elements = new List<JsonNode?>
            {
                Markdown($"**会话：** {session.Label}\n**错误信息**\n{chunk}"),
            };
            if (index == chunks.Count - 1 && retry is not null)
            {
                elements.Add(Note(RetryStatus(retry)));
                if (retry.State is not "stopped")
                {
                    elements.Add(ActionRow(Button(
                        "danger",
                        retry.State == "running" ? "停止后续自动重试" : "停止自动重试",
                        new()
                        {
                            ["action"] = FeishuCardActions.RetryStop,
                            ["sessionId"] = session.SessionId,
                            ["retryCycleId"] = retry.CycleId,
                        })));
                }
                else
                {
                    elements.Add(Note("你仍可以从飞书或电脑端重新发送任务。"));
                }
            }
            var part = chunks.Count > 1 ? $"（{index + 1}/{chunks.Count}）" : string.Empty;
            return Card("red", $"{runtime} 运行错误{part}", elements);
        }).ToArray();
    }

    public IReadOnlyList<FeishuCardView> RuntimeCompletion(
        FeishuSessionView session,
        string message)
    {
        ArgumentNullException.ThrowIfNull(session);
        var runtime = RuntimeName(session.Runtime);
        var safeMessage = RedactSensitiveText(message).Trim();
        var waitingForReply = FeishuMarkdownCards.LooksLikeQuestion(safeMessage);
        var fallback = $"{runtime} 已结束本轮处理。";
        var chunks = FeishuMarkdownCards.SplitMessage(
            safeMessage,
            fallback);
        var title = waitingForReply
            ? $"{runtime} 等待你回复"
            : $"{runtime} 本轮已完成";
        var template = waitingForReply ? "orange" : "green";
        var footer = session.ManagedByAssistant
            ? "下一轮请直接发送消息。"
            : "这个窗口不是由 AI CLI 飞书助手打开，不能从飞书回复。";

        return chunks.Select((chunk, index) =>
        {
            var part = chunks.Count > 1 ? $"（{index + 1}/{chunks.Count}）" : string.Empty;
            var elements = new List<JsonNode?>
            {
                Markdown($"**会话：** {session.Label}"),
                new JsonObject { ["tag"] = "hr" },
                Markdown($"**{runtime} 回复{part}**"),
            };
            elements.AddRange(FeishuMarkdownCards.ToElements(chunk));
            if (index == chunks.Count - 1)
            {
                elements.Add(Note(footer));
            }
            return Card(template, $"{title}{part}", elements);
        }).ToArray();
    }

    public FeishuCardView RuntimeActivity(
        FeishuSessionView session,
        IReadOnlyList<FeishuActivityEventView> events,
        string startedAt,
        bool completed = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(events);
        if (string.IsNullOrWhiteSpace(startedAt) ||
            !DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out _))
        {
            throw new ArgumentException("活动卡片开始时间无效。", nameof(startedAt));
        }

        var runtime = RuntimeName(session.Runtime);
        var elements = new List<JsonNode?>
        {
            Markdown(
                $"**会话：** {session.Label}\n" +
                $"**开始：** {ActivityTime(startedAt)}\n" +
                $"**目录：** {session.Cwd}"),
            new JsonObject { ["tag"] = "hr" },
        };
        foreach (var activity in events.TakeLast(6))
        {
            var label = RedactSensitiveText(activity.Label).Trim();
            if (label.Length == 0)
            {
                label = "活动更新";
            }
            elements.Add(Markdown(
                $"**{ActivityTime(activity.At)}　{Truncate(label, 240)}**"));
            if (!string.IsNullOrWhiteSpace(activity.Detail))
            {
                var detail = Truncate(
                    RedactSensitiveText(activity.Detail),
                    500);
                elements.AddRange(FeishuMarkdownCards.ToElements(detail).Take(2));
            }
        }
        if (events.Count == 0)
        {
            elements.Add(Markdown("正在准备任务…"));
        }
        if (!completed)
        {
            elements.Add(Note("同一轮只保留一张进度卡，工具执行与上下文压缩会在这里更新。"));
        }
        return Card(
            completed ? "green" : "blue",
            completed ? $"{runtime} 本轮处理完成" : $"{runtime} 正在处理",
            elements);
    }

    private static FeishuCardView Card(
        string template,
        string title,
        IEnumerable<JsonNode?> elements) => new(new JsonObject
        {
            ["config"] = new JsonObject
            {
                ["wide_screen_mode"] = true,
                ["update_multi"] = true,
            },
            ["header"] = new JsonObject
            {
                ["template"] = template,
                ["title"] = new JsonObject
                {
                    ["tag"] = "plain_text",
                    ["content"] = title,
                },
            },
            ["elements"] = new JsonArray(elements.ToArray()),
        });

    private static JsonObject Markdown(string content) => new()
    {
        ["tag"] = "div",
        ["text"] = new JsonObject
        {
            ["tag"] = "lark_md",
            ["content"] = content,
        },
    };

    private static JsonObject Note(string content) => new()
    {
        ["tag"] = "note",
        ["elements"] = new JsonArray(new JsonObject
        {
            ["tag"] = "plain_text",
            ["content"] = content,
        }),
    };

    private static JsonObject ActionRow(params JsonObject[] actions) => new()
    {
        ["tag"] = "action",
        ["actions"] = new JsonArray(actions),
    };

    private static JsonObject Button(
        string type,
        string text,
        JsonObject value,
        bool complexInteraction = false,
        string? actionType = null,
        string? name = null)
    {
        var button = new JsonObject
        {
            ["tag"] = "button",
            ["type"] = type,
            ["text"] = new JsonObject
            {
                ["tag"] = "plain_text",
                ["content"] = text,
            },
            ["behaviors"] = new JsonArray(new JsonObject
            {
                ["type"] = "callback",
                ["value"] = value,
            }),
        };
        if (complexInteraction)
        {
            button["complex_interaction"] = true;
        }
        if (actionType is not null)
        {
            button["action_type"] = actionType;
        }
        if (name is not null)
        {
            button["name"] = name;
        }
        return button;
    }

    private static void AddSelectionKey(JsonObject button, string? selectionKey)
    {
        if (string.IsNullOrWhiteSpace(selectionKey) ||
            button["behaviors"] is not JsonArray behaviors ||
            behaviors.Count != 1 ||
            behaviors[0] is not JsonObject behavior ||
            behavior["value"] is not JsonObject value)
        {
            return;
        }
        value["selectionKey"] = selectionKey;
    }

    private static JsonObject CommandValue(string action) => new()
    {
        ["action"] = action,
    };

    private static JsonObject RuntimeButton(
        string type,
        string text,
        string runtime,
        FeishuRuntimeNewContext context) => Button(
            type,
            text,
            RuntimeValue(FeishuCardActions.RuntimeNewSelect, runtime, context));

    private static JsonObject RuntimeValue(
        string action,
        string runtime,
        FeishuRuntimeNewContext context) => new()
        {
            ["action"] = action,
            ["runtime"] = runtime,
            ["flowId"] = context.FlowId,
            ["sourceMessageId"] = context.SourceMessageId,
            ["chatId"] = context.ChatId,
        };

    private static JsonObject FormColumn(JsonObject button) => new()
    {
        ["tag"] = "column",
        ["width"] = "auto",
        ["elements"] = new JsonArray(button),
    };

    private static string RuntimeName(string runtime) => runtime switch
    {
        "codex" => "Codex",
        "opencode" => "OpenCode",
        "claudecode" => "Claude Code",
        _ => runtime,
    };

    private static void ValidateRuntime(string runtime)
    {
        if (runtime is not ("codex" or "claudecode" or "opencode"))
        {
            throw new ArgumentOutOfRangeException(nameof(runtime));
        }
    }

    private static void ValidateContext(FeishuRuntimeNewContext context)
    {
        if (string.IsNullOrWhiteSpace(context.FlowId) ||
            string.IsNullOrWhiteSpace(context.SourceMessageId) ||
            string.IsNullOrWhiteSpace(context.ChatId))
        {
            throw new ArgumentException("新建会话卡片上下文不完整。", nameof(context));
        }
    }

    private static string Truncate(string? value, int length)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "（没有可展示的参数）" : value.Trim();
        return text.Length <= length ? text : string.Concat(text.AsSpan(0, length - 1), "…");
    }

    private static string ActivityTime(string? value)
    {
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            return timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
        return "--:--:--";
    }

    private static IReadOnlyList<string> SplitError(string? error)
    {
        var text = string.IsNullOrWhiteSpace(error) ? "未知错误" : error.Trim();
        var chunks = new List<string>((text.Length / ErrorChunkLength) + 1);
        for (var offset = 0; offset < text.Length; offset += ErrorChunkLength)
        {
            chunks.Add(text.Substring(offset, Math.Min(ErrorChunkLength, text.Length - offset)));
        }
        return chunks;
    }

    private static string RedactSensitiveText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        var environment = SensitiveEnvironmentValue().Replace(text, "$1=[已隐藏]");
        return SensitiveStructuredValue().Replace(environment, "$1[已隐藏]");
    }

    private static string RetryStatus(FeishuRuntimeRetryView retry) => retry.State switch
    {
        "scheduled" =>
            $"助手将在 {retry.DelaySeconds} 秒后自动重试（第 {retry.Attempt}/{retry.MaxAttempts} 次）。",
        "running" => $"助手已发起第 {retry.Attempt}/{retry.MaxAttempts} 次自动重试。",
        _ => "已停止自动重试。",
    };

    private static void ValidateRetry(FeishuRuntimeRetryView retry)
    {
        if (string.IsNullOrWhiteSpace(retry.CycleId) ||
            retry.State is not ("scheduled" or "running" or "stopped") ||
            retry.Attempt < 1 || retry.MaxAttempts < retry.Attempt ||
            retry.DelaySeconds < 0)
        {
            throw new ArgumentException("飞书自动重试卡片状态无效。", nameof(retry));
        }
    }

    [GeneratedRegex(
        "\\b([A-Z0-9_]*(?:SECRET|TOKEN|PASSWORD|PASSWD|API_KEY)[A-Z0-9_]*)" +
        "\\s*=\\s*([^\\s\"']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveEnvironmentValue();

    [GeneratedRegex(
        "(\"?(?:secret|token|password|passwd|api[_-]?key|authorization|cookie)\"?" +
        "\\s*[:=]\\s*)[\"']?[^\\s,}\"']+[\"']?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveStructuredValue();

    private static void ValidateQuestionPosition(int index, int count)
    {
        if (index < 0 || count <= index)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
