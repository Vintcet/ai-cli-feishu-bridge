using System.Text.Json.Nodes;
using AiCliFeishu.Bridge.Core;

namespace AiCliFeishu.Bridge.Adapters.Feishu;

public sealed class FeishuCardRenderer : IFeishuCardRenderer
{
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
        string resolution)
    {
        if (!ApprovalResolutions.All.Contains(resolution))
        {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }
        var runtime = RuntimeName(session.Runtime);
        var template = resolution switch
        {
            ApprovalResolutions.Allow => "green",
            ApprovalResolutions.Deny => "red",
            _ => "grey",
        };
        var result = resolution switch
        {
            ApprovalResolutions.Allow => $"已批准，{runtime} 将继续执行。",
            ApprovalResolutions.Deny => $"已拒绝，{runtime} 会收到拒绝结果。",
            ApprovalResolutions.Local => $"已转回电脑端，请在原 {runtime} 窗口确认。",
            _ => "飞书审批已超时，已转回电脑端确认。",
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
        IReadOnlyList<string>? selectedAnswers = null)
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
        }
        actions.Add(Button("default", "转回本机回答", new()
        {
            ["action"] = FeishuCardActions.InputLocal,
            ["requestId"] = requestId,
            ["sessionId"] = session.SessionId,
        }));
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

    private static void ValidateQuestionPosition(int index, int count)
    {
        if (index < 0 || count <= index)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
