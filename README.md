# Codex / Claude Code / OpenCode 飞书桥接器

[简体中文](README.md) | [English](README_EN.md)

当前版本：`0.18.0`

这是一个运行在 Windows 本机的非官方桥接器，把 Codex CLI、Claude Code 和 OpenCode 会话连接到你自己的飞书企业自建应用。每个助手会话可以绑定一个独立私有群，让你在电脑外接收完成或错误通知、处理权限审批和补充问题，并继续向原会话发送消息。

桥接服务、会话索引和配置都保存在本机；项目不提供云端中转服务，也不捆绑 Codex CLI、Claude Code、OpenCode 或飞书应用。使用者需要自行安装并登录目标 CLI，并自行创建飞书应用。

## 0.18.0 更新摘要

- 本机 HTTP 控制面统一使用持久令牌鉴权，匿名健康检查不再暴露配对码或会话信息，并拒绝跨站及非 JSON 写请求；
- Codex、Claude Code、OpenCode 共用低风险自动审批规则，高风险命令继续发送飞书人工审批；
- 历史记录可直接设置、修改或清除别名，操作不会改变原会话 ID、飞书群绑定或恢复目录；
- 修复旧 PID 被其他程序复用后会话短暂重现、审批两端状态延迟、附件与审批日志无界增长等长期运行问题；
- 桌面 EXE 直接管理 Node.js 桥接进程，支持鉴权平滑关闭；X 收进托盘、最小化留在任务栏，托盘双击直接恢复到前台。

三种运行时共用同一套会话、路由、飞书卡片、审批和状态管理，只有与目标 CLI 通信的底层方式不同：

| 运行时 | 本机接入方式 | 完整双向同步 |
| --- | --- | --- |
| Codex CLI | Hooks + 托管终端 | 由桌面助手启动时支持 |
| Claude Code | Hooks + 托管终端 | 由桌面助手启动时支持 |
| OpenCode | 本机 HTTP + SSE | 使用带端口的窗口时支持 |

主要功能：

- Codex 本轮完成或等待补充信息时发送飞书通知；
- Codex 请求执行权限时优先发送飞书审批卡片，需要时可转回 PC 审批；
- 在飞书批准或拒绝，也可在桌面助手本机审批窗口处理；两端状态自动同步；
- Codex 运行中通过飞书直接插话，或明确排队到下一轮；
- `request_user_input` 问题通过飞书卡片/引用回复回答；
- 默认只推送完成、补充信息、审批和错误等关键节点，可选开启实时进度卡；
- 可选把电脑端提交的 Codex 消息同步到对应飞书会话群；从飞书发来的消息不会重复回显；
- 电脑端输入、助手最终回复和运行错误过长时会拆成多条连续卡片，保留完整内容；回复中的 Markdown 表格会转换为飞书原生表格；
- 429、503、高负载、服务繁忙和超时等临时错误使用独立错误卡通知，并对 Codex、Claude Code 和 OpenCode 使用统一的连续失败重试计数；即使 Codex 未触发 `Stop` Hook，也会从活动 JSONL 转录中识别并推送；
- 可选自动允许低风险审批请求；高风险请求仍会推送人工确认；
- 飞书图片/文件安全保存到项目桥接目录，下一条消息交给目标助手处理；
- 用户明确要求时，把助手生成的项目文件回传飞书；
- 由桌面助手启动的 Codex / Claude Code 窗口支持飞书输入与本地窗口双向同步；
- 助手启动过的真实 CLI 会话会保留在历史记录中，可在原目录一键继续；曾手动隐藏的会话重新恢复后也会重新进入正常历史生命周期；
- 会话群对应窗口已经关闭时，群内下一条消息会请求托盘中的桌面面板自动打开原 Codex、Claude Code 或 OpenCode 会话，窗口登记后再发送原消息；
- 助手创建的会话群超过 7 天没有会话活动会自动解散；以后从历史恢复时会按需创建新群；
- 手工填写 `resume` / `--resume` / `--session` 参数时，会从本机历史记录自动识别并回填原工作目录；
- 可在机器人私聊发送 `新建 codex 项目名`、`新建 claude 项目名` 或 `新建 opencode 项目名`；项目会在默认工作区中查找，不存在时自动创建；
- 外部终端打开的 Codex 只接收通知、审批和补充信息，不接受普通飞书输入，避免与本机窗口同时推进同一对话；
- 同时运行多个助手窗口，并按会话别名或 `session_id` 严格路由；
- Claude Code 作为独立运行时接入：支持托管窗口、历史恢复、权限审批、`AskUserQuestion` 补充信息、工具活动和完成通知；
- 可选接入 opencode：同样一个对话对应一个飞书群，支持完成/错误/审批/工具活动通知与飞书输入双向同步（加法式接入，不影响 Codex 原有路径）。

### 运行环境

- Windows 10/11 x64；
- Git（也可以直接下载 GitHub 源码 ZIP）；
- Node.js 20 或更高版本；
- PowerShell 7（命令名为 `pwsh.exe`）；
- .NET 8 Windows Desktop Runtime；从源码编译桌面端时需要 .NET 8 SDK；
- Codex CLI、Claude Code、OpenCode 中至少安装一个；
- 一个可创建机器人、开通所需权限并发布版本的飞书企业自建应用；
- Windows Terminal 为推荐项，未安装时会回退到普通控制台。

## 获取程序与首次构建

Windows 用户可以从 [GitHub Releases](https://github.com/Vintcet/codex-feishu-bridge/releases) 下载 `codex-feishu-bridge-v0.18.0-windows-x64.zip`。压缩包已包含编译后的桥接服务、生产依赖和两个桌面 EXE；请完整解压后使用，不要只拿主程序一个文件。首次运行前，把 `.env.example` 复制为 `.env` 并填写飞书应用配置，然后双击根目录的 `Codex飞书助手.exe`。

从源码构建时执行：

```powershell
git clone https://github.com/Vintcet/codex-feishu-bridge.git
cd .\codex-feishu-bridge
npm install
Copy-Item .\.env.example .\.env
npm run build
dotnet publish .\desktop-control\CodexFeishuControl.csproj -c Release -o .\desktop-control\publish
```

然后编辑 `.env`，并运行源码构建出的程序：

```powershell
.\desktop-control\publish\CodexFeishuControl.exe
```

`CodexFeishuControl.exe` 是源码构建出的桌面面板，`CodexFeishuTerminalHost.exe` 必须与它保留在同一目录。已打包版本可以把主程序显示为 `Codex飞书助手.exe`，功能相同。需要桌面入口时，可为实际使用的主程序手动创建快捷方式。

## 飞书机器人：从创建到可用

这里需要的是一个**企业自建应用**，不是群聊里的“自定义机器人 Webhook”。桥接器使用应用的 App ID / App Secret 建立飞书 WebSocket 长连接，因此不需要公网服务器、回调域名或内网穿透。

### 1. 创建企业自建应用并启用机器人

1. 打开[飞书开放平台开发者后台](https://open.feishu.cn/app)，登录要使用机器人的企业账号。
2. 点击“创建企业自建应用”，填写应用名称、描述和图标。
3. 进入应用详情，在“应用能力”中添加“机器人”能力；控制台改版时可参考[飞书官方机器人能力说明](https://open.feishu.cn/document/uAjLw4CM/ugTN1YjL4UTN24CO1UjN/trouble-shooting/how-to-enable-bot-ability)。
4. 在“凭证与基础信息”中复制 **App ID** 和 **App Secret**，写入项目目录的 `.env`：

```env
FEISHU_APP_ID=cli_xxxxx
FEISHU_APP_SECRET=xxxxx
```

App Secret 等同于应用密码：不要发到群里、截图公开或提交到 Git。仓库已忽略 `.env`，发布安装包时也不要把自己的 `.env` 一并分发。

### 2. 添加应用身份权限

进入“权限管理”，按下面的**权限标识**搜索并开通。表中“必需”是指启用本项目推荐的“一会话一私有群”完整功能；附件两项只在完全不收发文件时可以省略。

| 权限标识 | 飞书控制台名称 | 本项目用途 | 要求 |
| --- | --- | --- | --- |
| `im:message:send_as_bot` | 以应用的身份发消息 | 发送通知、回复、审批卡片并更新卡片状态 | 必需 |
| `im:message.p2p_msg:readonly` | 读取用户发给机器人的单聊消息 | 接收绑定和管理命令 | 必需 |
| `im:message.group_msg` | 获取群组中所有消息 | 接收会话群内未 `@机器人` 的普通消息 | 必需，敏感权限 |
| `im:chat:create` | 创建群 | 为每个助手会话创建独立私有群 | 必需 |
| `im:chat:update` | 更新群信息 | 设置别名后同步修改群名 | 必需 |
| `im:chat:delete` | 解散群 | 自动解散长期不活跃的会话群 | 必需 |
| `im:chat:operate_as_owner` | 更新应用所创建群的群信息 | 允许作为群创建者的机器人更新和解散自己创建的群 | 必需 |
| `im:message:readonly` | 获取单聊、群组消息 | 下载用户消息中的图片和文件 | 附件功能必需 |
| `im:resource` | 获取与上传图片或文件资源 | 上传图片或生成文件并回传飞书 | 附件功能必需 |

`im:message.group_msg` 属于敏感权限，部分企业需要飞书管理员审批。`im:chat:update` / `im:chat:delete` 是调用对应接口的权限，`im:chat:operate_as_owner` 是机器人以“应用创建的群之创建者”身份管理群的权限；要同时配置，不能互相替代。

### 3. 配置事件与卡片回调

进入“事件与回调”（不同控制台版本也可能显示为“事件订阅”），把订阅方式设置为**使用长连接接收事件**，然后添加：

| 类型 | 名称 | 标识 | 用途 |
| --- | --- | --- | --- |
| 事件 | 接收消息 v2.0 | `im.message.receive_v1` | 接收私聊、群聊、图片和文件消息 |
| 回调 | 卡片回调交互 | `card.action.trigger` | 接收审批按钮和补充信息选项 |

第一次保存长连接订阅方式时，飞书控制台通常会检查是否已经有客户端在线。前面把 App ID / App Secret 写入 `.env` 后，可以先启动打包版 `Codex飞书助手.exe`，或源码构建产物 `desktop-control\publish\CodexFeishuControl.exe`，再点击“连接”；看到桥接服务开始连接后，回到开放平台保存长连接配置。此时不需要先绑定飞书账号，也不需要启动任何 CLI 会话。事件、权限和版本发布完成后，再从桌面助手断开并重新连接一次。

如果控制台把“事件”和“回调”分成两个标签页，请分别添加。长连接由本机桥接器主动连向飞书，不需要填写公网请求地址；不要改成 Webhook 模式。飞书官方原理和限制见[长连接模式说明](https://open.feishu.cn/document/uAjLw4CM/ukTMukTMukTM/event-subscription-guide/long-connection-mode)。

### 4. 设置可用范围并发布版本

1. 在“版本管理与发布”中新建版本。
2. 把[应用可用范围](https://open.feishu.cn/document/home/introduction-to-scope-and-authorization/availability)至少设为准备绑定的飞书账号；否则机器人可能搜不到用户，也无法把该用户拉入会话群。
3. 提交审核并发布。企业策略要求管理员审核时，等待管理员批准。
4. 确认最新版本状态已经是“已发布”，再连接桌面助手。

这是最常见的漏项：**保存权限或事件草稿不会生效**。以后每次增加权限、修改事件或调整可用范围，都必须重新创建并发布应用版本，然后在桌面助手中先断开、再点击“连接”。

### 5. 连接、绑定并验证

1. 双击打包版 `Codex飞书助手.exe`，或运行源码构建出的 `desktop-control\publish\CodexFeishuControl.exe`；点击“连接”，等待界面显示“飞书已连接”。
2. 在飞书中找到刚发布的机器人，进入私聊。
3. 把桌面控制面板显示的命令发给机器人，首次通常是 `绑定 <随机绑定码>`。绑定后只有该飞书账号能管理本机助手。
4. 从桌面新建一个 Codex、Claude Code 或 OpenCode 会话，等待其出现在“活跃会话”。
5. 选中会话并点击“创建飞书群”；新建的托管会话通常也会自动尝试创建。
6. 进入 `Codex｜项目名`、`Claude｜项目名` 或 `OpenCode｜项目名` 群，直接发送普通文字测试。群内不需要 `@机器人`。
7. 再测试一次审批卡片；需要附件功能时，也发送一张图片或文件验证下载与回传权限。

飞书中的两个入口用途不同：

| 入口 | 用途 |
| --- | --- |
| 机器人私聊 | 绑定、状态、工作区、新建会话、会话列表、别名和帮助等管理命令 |
| 独立会话群 | 继续该群绑定的 CLI 对话；所有普通文字都会原样发送给对应助手 |

如果群创建失败，本地 CLI 仍可正常使用，通知会暂时回退到机器人私聊。先根据桌面“飞书群”列中的具体错误补齐权限并发布新版本，再选中会话点击“创建飞书群”重试，不需要重开 CLI。

## 快速开始：一个助手对话对应一个飞书群

本项目推荐的使用方式是：每个由桌面助手打开的 Codex、Claude Code 或 opencode 窗口，都绑定一个独立的飞书私有群。群里只有你和机器人，群内消息只会进入对应的助手对话，不会串到其他窗口。

按下面顺序操作：

1. 在飞书开放平台完成机器人、权限和事件配置，并创建、发布最新应用版本。
2. 运行实际使用的桌面 EXE（或你为它创建的 `Codex飞书助手` 快捷方式），点击“连接”，等待顶部显示“飞书已连接”。
3. 查看控制面板提示的绑定命令，在机器人私聊中发送它（首次通常是 `绑定 <随机绑定码>`）。绑定成功后，只有这个飞书账号能控制本机助手。
4. 在“设置”中确认默认工作区。之后既可点击“新建 Codex”“新建 Claude”或“新建 opencode”选择目录，也可在机器人私聊发送 `新建 codex 项目名`；需要恢复旧对话时，可在“历史记录”中选中会话并点击“继续对话”。
5. 新建的托管会话会自动尝试建群；如果没有自动创建，在“活跃会话”中选中目标会话并点击“创建飞书群”。创建成功后，列表“飞书群”列会显示 `Codex｜项目名`、`Claude｜项目名` 或 `OpenCode｜项目名`。
6. 进入对应群，直接发送普通文字即可。群内不需要 `@机器人`、别名或短 ID。

普通消息提交后，机器人只返回简短结果，例如 `Codex 已接收。` 或 `Claude Code 已接收。`；失败消息也会按目标运行时显示原因。
完成通知不再提示引用回复；需要继续时，直接在对应会话群发送下一条消息即可。

如果对应 CLI 窗口已经关闭，群内消息不会立即报废：只要桌面控制面板仍在运行（隐藏到托盘也可以），它会自动按该会话保存的运行时、工作目录和管理员模式打开原对话，登记成功后再提交这条消息。控制面板如果已从托盘彻底退出，桥接器会等待一段时间并在群里明确提示超时，不会静默丢失。

助手创建的会话群按最后会话活动清理，默认连续 7 天无活动后自动解散。解散只影响飞书群，不删除 CLI 对话、项目目录或本机历史记录；之后在桌面恢复该会话时会重新创建群。

群的路由关系如下：

| 位置 | 发送内容后的去向 |
| --- | --- |
| `Codex｜项目名` / `Claude｜项目名` / `OpenCode｜项目名` 会话群 | 只进入该群对应的助手窗口 |
| 机器人私聊 | 绑定、状态、会话列表、别名、帮助等管理命令；也可以用 `@别名 内容` 指定会话 |
| 外部手动打开的 Codex / Claude Code | 只接收通知、审批和补充信息，不接受普通飞书输入，也不会自动创建会话群 |

新建的托管会话会自动尝试创建对应群。如果应用权限尚未生效，或列表中显示“创建失败”，先确认应用版本已经发布，再选中该会话点击“创建飞书群”重试；不需要重新打开 Codex。设置会话别名后，已创建的群名也会同步更新。

### 使用前必须满足

- 飞书应用已启用机器人能力；
- 已添加并发布本文“飞书机器人：从创建到可用”一节列出的权限；
- 已订阅 `im.message.receive_v1` 和 `card.action.trigger`，并使用长连接；
- 你在应用的可用范围内；
- 修改权限或事件后，必须创建并发布新应用版本，再在助手中先断开、重新“连接”。

## Windows 桌面控制面板

日常使用无需命令行。运行下面任意一个实际存在的主程序：

```text
打包版：.\Codex飞书助手.exe
源码构建：.\desktop-control\publish\CodexFeishuControl.exe
```

如需桌面入口，可为实际运行的 EXE 手动创建名为 `Codex飞书助手` 的快捷方式。控制面板提供：

- 连接/断开（同一个按钮会根据服务状态切换）；
- “新建 Codex”：选择项目目录并启动一个可与飞书同步的 Windows Terminal 窗口（PowerShell 7 + Codex）；
- “新建 Claude”：选择项目目录并启动一个可与飞书同步的 Windows Terminal 窗口（PowerShell 7 + Claude Code）；
- 可为单个新窗口选择 Windows 管理员启动，管理员模式会触发本机 UAC；
- 桥接服务是否运行；
- 飞书长连接的真实状态；
- 已绑定账号数量；
- 活跃助手会话列表，包括运行时、自定义别名、项目、会话短 ID、状态、排队数、模型、来源、工作目录、打开时间和最近活动；
- “历史记录”包含助手启动过、当前已不在线的 Codex / Claude Code 会话，以及通过本机端口接入过的 OpenCode 当前对话；手动从外部终端启动的 Codex / Claude Code 不进入这里；
- 飞书会话群向已关闭对话发送消息时，托盘中的面板会自动领取恢复请求并复用同一套“继续对话”启动流程；
- 在活跃会话或历史记录中选中会话后，可点击“设置别名”设置、修改或清除别名；别名变化不会改变原会话和飞书群绑定；“打开目录”打开当前选中会话的工作目录；
- 在“活跃会话”页双击会话行也可打开对应工作目录；
- “设置”中可选择飞书新建项目使用的默认工作区，并控制普通过程信息、电脑端输入同步、临时错误自动重试、低风险审批自动允许及自动审批留痕；设置立即生效并保存在本机。

点击控制面板右上角 X 时，窗口会以系统原生收拢动画隐藏到右下角托盘；普通最小化仍保留在任务栏。双击托盘图标会直接把窗口恢复到前台，右键选择“退出”才会关闭控制面板进程。再次双击 EXE 也会直接唤回已运行的窗口。退出控制面板仍不会停止后台桥接服务。

“临时错误自动重试”只处理能够识别和安全重放的 400/408/409/429/5xx、高负载、服务繁忙及超时错误。可配置每批连续失败重试 1～20 次、基础间隔 1～600 秒，以及随机增加 0～120 秒；每次实际等待为“基础间隔 + 0～随机增加秒”。Codex、Claude Code 和 OpenCode 都会在原会话中请求重试上一项任务；自动重试错误卡底部会显示“停止自动重试”，重试已经发出时点击它会停止本批后续尝试。任意一次成功完成都会立即清零本批计数，之后即使很快再次失败也会从第 1 次重新计算。Codex 的服务端采样失败可能直接写入 `task_complete.error` 而不执行 `Stop` Hook，因此桥接器会从活动转录当前文件末尾开始轮询，只处理之后新追加的错误，避免重放旧通知。

“低风险审批自动允许”对进入桥接器的 Codex、Claude Code 和 OpenCode 权限请求使用同一套风险规则，默认关闭。开启后，只有明确白名单内且参数能够完整检查、未涉及删除/破坏性 Git、依赖安装发布、网络与云端操作、权限或系统配置修改、敏感路径及项目目录外路径的请求才会直接允许；未知工具、超长或无法完整判断的参数一律转人工。人工审批默认只发送飞书卡，不会同时弹出电脑端窗口；点击卡片中的“转回 PC 审批”或引用回复“本机确认”后，电脑端才会弹出，飞书不可用时则自动回退到 PC。自动允许成功时默认不发送卡片；同时开启“自动审批后发送处理留痕”时，才发送一张没有操作按钮的已处理信息卡。自动处理失败或审批超时不会静默放行。

## 新建同步 Codex 窗口

推荐按以下顺序使用：

1. 双击桌面的 `Codex飞书助手`；
2. 点击“连接”，等待飞书状态变为“已连接”；
3. 点击“新建 Codex”，选择项目目录；
4. 如需特殊启动方式，在“Codex 启动参数”中填写参数，例如 `resume 019faef0-d0bb-7703-af82-17ee9b45397b`；也可以直接粘贴完整的 `codex resume ...`；匹配到本机历史会话时，项目目录会自动切换为该会话保存的目录；
5. 按需勾选“以 Windows 管理员身份启动”，普通项目建议不要勾选；
6. 在弹出的 Windows Terminal / Codex 窗口中正常工作。

每次“新建 Codex”都会打开一个独立的 Windows Terminal 窗口。未安装 Windows Terminal 时会自动回退到普通 Windows 控制台。这类窗口由助手托管：飞书回复会通过仅限当前 Windows 用户访问的本机命名管道写入同一个 Codex 窗口，所以飞书输入和 Codex 回答都会在本地窗口中显示。管理员窗口只提升该窗口及其子进程，不会提升其他普通 Codex 窗口。UAC 必须在电脑端确认，不能通过飞书绕过。

关闭托管窗口后，会话会从“活跃会话”移动到“历史记录”。继续历史会话时，助手沿用原工作目录和原管理员模式，并安全传入 `resume 会话ID`；不需要复制或输入完整 ID。若直接在对应飞书会话群继续发送消息，托盘中的桌面面板也会执行同一恢复流程。仍在线的会话不会同时出现在历史记录中；外部终端手动打开的 Codex / Claude Code 不进入历史记录，而使用本机端口接入的 OpenCode 当前对话属于可恢复会话，会在断开后进入历史记录。若曾点击“删除记录”，该结束会话保持隐藏；一旦它被再次恢复，隐藏标记会自动清除，之后关闭时会重新出现在历史记录中。桥接器启动时也会修复旧版本遗留的“活动但仍被隐藏”记录。

“Codex 启动参数”只解析为 Codex CLI 参数，不会作为 PowerShell 命令执行。留空等同于直接运行 `codex`；填写 `resume 会话ID` 等同于运行 `codex resume 会话ID`。

直接从其他终端手动运行的 Codex 仍会被 Hooks 发现并发送通知，但普通飞书文字不会写入或另起临时进程；请回到原窗口继续，避免电脑端与飞书同时推进同一会话。

## 新建同步 Claude Code 窗口

Claude Code 使用与 Codex 相同的托管窗口和飞书路由机制：

1. 先确认 `claude --version` 可正常运行；
2. 在桌面助手中点击“新建 Claude”，选择项目目录；
3. 如需恢复旧对话，在“Claude Code 启动参数”中填写 `--resume 019faef0-d0bb-7703-af82-17ee9b45397b`；也可以粘贴完整的 `claude --resume ...`；
4. 按需选择管理员模式，然后在弹出的 Windows Terminal / Claude Code 窗口中正常工作。

桌面助手会优先使用 PATH 中的 `claude.exe` / `claude.cmd`，也会检查 `%USERPROFILE%\.local\bin`、`%APPDATA%\npm` 和 `%LOCALAPPDATA%\Programs\Claude` 等常见安装位置。找到的真实可执行文件路径会直接交给托管宿主，因此不要求这些备用目录已经加入 PATH。

托管 Claude Code 窗口支持：

- 会话启动后自动登记，并创建或恢复对应的 `Claude｜项目名` 飞书群；
- 飞书普通消息写入当前可见窗口；运行中发送的消息由 Claude Code 自己排队，明确使用“排队”前缀时也不会另起后台进程；
- `PermissionRequest` 通过飞书或本机审批，允许和拒绝结果回到原 Claude Code 请求；
- `AskUserQuestion` 支持单选、多选、自定义答案和选项预览；飞书答案以 Claude Code 需要的 `updatedInput.answers` / `annotations` 格式返回；
- 工具失败、上下文压缩开始和完成也会进入同一张活动卡；关闭会话时会立即释放仍在等待的审批或提问；
- `Stop` 从 Claude Code 的 JSONL transcript 读取最终回复并发送完成通知；
- 关闭窗口后进入“历史记录”，点击“继续对话”会在原目录安全传入 `--resume 会话ID`。

点击“连接”时，助手会把本项目的 Claude Code Hooks 合并到 `%USERPROFILE%\.claude\settings.json`，保留其中其他 Hook。外部终端手动启动的 Claude Code 也能触发通知、审批和补充信息，但按设计不接受普通飞书输入；需要完整双向同步时请使用“新建 Claude”。

## 新建同步 opencode 窗口

桌面助手也支持启动 opencode 同步窗口，流程与“新建 Codex”一致：

1. 点击“新建 opencode”，选择项目目录；
2. 如需特殊启动方式，在“opencode 启动参数”中填写参数，例如 `-s 019faef0-d0bb-7703-af82-17ee9b45397b`（恢复历史会话）或 `-c`（继续上次会话）；
3. 桥接服务会在 `5100–5999` 端口池中为该窗口保留一个仅本机访问的端口；
4. 助手随后在一个可见的 Windows Terminal 窗口中启动 `opencode --port <端口>`（附加填写的参数）；未安装 Windows Terminal 时回退到普通控制台。

opencode 需要先安装并可用：要求 `opencode` 命令在 PATH 中，或位于 `~\.local\bin`、`%APPDATA%\npm`、`%LOCALAPPDATA%\Programs\opencode` 等常见目录。

### 自动发现已运行的 opencode

桥接器默认会每隔约 20 秒扫描本机回环地址上的监听端口，自动连接检测到的 opencode 服务（健康检查 `/global/health`）。因此不一定非要由桌面助手启动：

- 你手动运行 `opencode --port <端口>`（任意可见端口）也会被自动发现；
- 从桌面“新建 opencode”启动的窗口同样由自动发现兜底接管；
- 窗口关闭（进程退出）后，桥接器会在订阅断开时立即移除对应实例，相关会话移入“历史记录”。

需要说明：仅 `opencode --port <端口>`（或 `opencode serve`）才在本机暴露 HTTP 服务；直接运行不带端口的 `opencode` TUI 时本机不暴露 HTTP，无法被自动发现或注入，请用桌面按钮或 `--port` 启动。可通过 `OPENCODE_AUTO_DISCOVER=0` 关闭自动发现。

opencode 窗口接入后体验与 Codex 托管窗口一致：

- 会话自动登记，并自动创建/恢复对应的独立飞书群；
- 本轮完成、错误、上下文压缩和工具活动都会同步通知到对应群；
- opencode 请求权限时会发送飞书审批卡片：飞书“允许”对应 `once`，“拒绝”对应 `reject`；兼容 `permission.v2.asked/replied`、`/api/permission/request`、V2 会话回复端点，以及旧版权限事件和端点，当前卡片暂不暴露 `always`；
- opencode 的 `question.asked` 会转成飞书补充信息卡：少量单选可直接点按钮，多选可引用回复 `1,3`，多问题用中文分号分隔；允许自定义答案时也可直接填写文字；
- 问题或权限若先在电脑端处理，飞书卡片和会话状态会自动同步；桥接器重连后也会补捞仍未处理的请求；
- 飞书可直接插话，也可加 `排队` 前缀交给下一轮；队列在窗口恢复后自动重放；
- 关闭 opencode 窗口后，会话移入“历史记录”，可一键继续。

桥接器只与本机回环地址上的 opencode HTTP 服务通信，不使用无界面的 `opencode serve`（那样没有可见窗口，也就无法双向同步）。

## 多窗口如何避免串线

每个助手窗口都有会话短 ID，也可以设置一个更容易识别的别名，例如：

```text
@主项目  codex+ #9b45397b
```

飞书回复有四种方式：

1. 推荐：直接进入该助手会话对应的独立飞书群，发送普通文字；
2. 群内无需 `@机器人`，也无需写别名或短 ID；
3. 在机器人私聊中，可以引用对应通知回复，或发送 `@别名 回复内容`；
4. 私聊中也可用 `#短ID 回复内容`，只有一个活跃会话时可直接发送普通文字。

存在多个活跃会话时，桥接器不会猜测目标。发送 `会话` 可以查看列表。托管窗口可以在当前轮插话，也可以把消息排到下一轮；外部会话只能通知，不能通过飞书输入。

飞书消息中的换行会被当作普通文字注入本地窗口。Codex 默认用 `Enter` 插话，使用“排队”命令时用 `Tab` 交给下一轮；Claude Code 始终用 `Enter` 提交，并由 Claude Code 在运行中自行排队。

“活跃会话”的定义：

- 由桌面助手新建的 Codex / Claude Code 托管窗口，从窗口打开起就算活跃，不需要先提交任务；只要窗口没有关闭就持续显示，正常关闭后立即移除，异常退出时最多等待约 20 秒心跳过期；
- opencode 每个监听端口只登记当前打开的对话，同一实例中可查询到的其他历史对话不会因此显示为活跃；无论窗口由桌面按钮、手动 `--port` 还是自动发现接入，当前对话会在窗口关闭或实例断开后立即移除；
- 从其他终端手动打开的 Codex / Claude Code 也会显示，但“方式”会明确标为“外部会话”。桥接器会同时核对真实 CLI 进程的 PID、名称和启动时间，进程关闭后自动移除，也不会因 Windows 复用 PID 产生误报；极少数无法取得进程信息的会话只临时保留约 5 分钟。

## 本地配置

`.env` 位于本项目目录：

```text
.\.env
```

参考 `.env.example` 填写。`FEISHU_APP_SECRET` 不要发到聊天中，也不要提交到 Git。

```env
FEISHU_APP_ID=cli_xxxxx
FEISHU_APP_SECRET=xxxxx
FEISHU_BIND_COMMAND=绑定
BRIDGE_HTTP_PORT=8765
CODEX_APPROVAL_TIMEOUT_MS=1200000
CODEX_SESSION_ACTIVE_MS=86400000
CODEX_TRANSCRIPT_POLL_INTERVAL_MS=750
FEISHU_SESSION_GROUP_INACTIVE_MS=604800000
FEISHU_SESSION_GROUP_CLEANUP_INTERVAL_MS=3600000
RUNTIME_AUTO_LAUNCH_TIMEOUT_MS=120000
DEFAULT_WORKSPACE_ROOT=
CODEX_COMMAND=codex
```

`DEFAULT_WORKSPACE_ROOT` 留空时默认使用桥接器目录的上一级，也可以在桌面“设置”中选择一个真实存在的目录；不要直接照抄其他机器的盘符路径。

Codex 和 Claude Code Hook 脚本默认连接 `http://127.0.0.1:8765`。如需修改端口，还要给启动对应 CLI 的环境设置 `CODEX_FEISHU_BRIDGE_URL`，或同步更新 Hook 脚本配置。

## 安装与启动

### 日常使用（当前为手动启动）

Windows 登录自启动当前未安装。每次需要使用时，运行桌面面板 EXE 或自己的快捷方式，点击状态按钮连接；服务运行后同一个按钮会变为“断开”。无需输入命令行。

桌面 EXE 会直接启动 Node.js 桥接进程；断开时通过带本机控制令牌的接口平滑停止，不再经过 VBS 或桥接启停 PowerShell 脚本。需要自动化但不打开面板时，可使用 `Codex飞书助手.exe --bridge-start` 和 `Codex飞书助手.exe --bridge-stop`。

### 仅运行或调试桥接服务

下面的命令只启动 Node.js 桥接服务，不包含桌面面板。需要完整的托管窗口、历史记录和自动恢复功能时，请按前文“获取程序与首次构建”同时构建并运行桌面端。

```powershell
cd <项目目录>\codex-feishu-bridge
npm install
npm run build
npm start
```

开发时也可运行：

```powershell
npm run dev
```

提交改动前可运行完整的本地校验：

```powershell
npm run lint
npm run format:check
npm test
npm run build
dotnet test .\desktop-control\tests\CodexFeishuTerminalHost.Tests.csproj -c Release
dotnet build .\desktop-control\CodexFeishuControl.csproj -c Release
```

`npm run format:check` 是零额外依赖的文本卫生检查，检查受版本控制的源码和文档是否存在行尾空白或缺少末尾换行。仓库中的 Windows CI 会执行同一组 Node.js 与 .NET 校验。

桥接器会同时启动：

- 飞书 WebSocket 长连接；
- 仅监听 `127.0.0.1` 的 Hook HTTP 服务；
- 默认健康检查地址 `http://127.0.0.1:8765/health`。

## Codex Hooks

全局 Hook 配置位置：

```text
%USERPROFILE%\.codex\hooks.json
```

连接按钮会先运行 `scripts/install-hooks.ps1`，把本桥接器 Hook 合并到：

```text
%USERPROFILE%\.codex\hooks.json
```

脚本只移除本桥接器自己以前安装的同名命令，保留其他 Hook。使用九类 Hook：

- `SessionStart`：Codex 会话启动、恢复或压缩后登记；当前 Codex 通常会在新窗口提交第一条任务时首次触发；
- `SessionEnd`：Codex 会话结束后从活跃列表移除；
- `PermissionRequest`：等待飞书审批；桥接器不可用或超时时返回本机原生审批；
- `Stop`：发送正常完成或等待回复通知；Codex 服务端采样失败不一定执行该 Hook。
- `PreToolUse`（含 `request_user_input`）：发送远程问题并同步工具活动；
- `PostToolUse`：更新工具完成活动；
- `PreCompact` / `PostCompact`：更新上下文压缩活动；
- `UserPromptSubmit`：标记新一轮任务开始，帮助“排队”准确判断运行状态。

对于未经过 `Stop` 的 `task_complete.error`，桥接器会轮询活动 Codex 会话的 JSONL 转录并复用同一套错误卡、去重和自动重试逻辑。轮询从登记时的文件末尾开始，默认间隔由 `CODEX_TRANSCRIPT_POLL_INTERVAL_MS=750` 控制。

首次打开新的 Codex 窗口时，Codex 会显示 Hook 信任审核。确认路径和内容无误后正常批准即可。不要使用绕过 Hook 信任的危险参数。

Hook 配置或编译结果变化后，需要重启桥接器，并新开 Codex 窗口才能可靠加载。

## Claude Code Hooks

Claude Code 的用户级配置位置是：

```text
%USERPROFILE%\.claude\settings.json
```

点击“连接”时，桌面助手会运行 `scripts/install-claude-code-hooks.ps1`，以 Claude Code 要求的 matcher group + `type: command` 格式合并以下事件，并保留其他插件或用户已有的 Hook：

- `SessionStart` / `SessionEnd`：登记和结束 Claude Code 会话；
- `PermissionRequest`：等待飞书或本机审批，离线或超时则回退到 Claude Code 原生处理；
- `PreToolUse`：把 `AskUserQuestion` 转成飞书补充信息卡，并记录其他工具开始活动；
- `PostToolUse`：记录工具完成活动；
- `PostToolUseFailure`：记录工具失败和错误摘要；
- `PreCompact` / `PostCompact`：记录上下文压缩开始和完成；
- `UserPromptSubmit`：同步电脑端提交的新任务；
- `Stop`：从 `transcript_path` 指向的 JSONL 转录中读取最终 assistant 回复并发送完成通知。

安装脚本会清理本桥接器早期 direct-hook 条目和旧发布目录留下的同名 Hook，但不会删除其他 Hook；重复运行不会重复添加。可以在任意隔离目录或项目目录运行 `claude doctor` 检查配置；正常结果应包含 `No installation issues found.`。配置或 `dist/hooks` 变化后，请重新点击“连接”并新开 Claude Code 窗口。

当前仍是手动启动，不会安装登录自启动任务。

### 重新编译桌面 EXE

```powershell
dotnet publish .\desktop-control\CodexFeishuControl.csproj -c Release -o .\desktop-control\publish
```

发布结果包含两个单文件程序：界面程序 `CodexFeishuControl.exe` 和必须与它放在同一目录的 `CodexFeishuTerminalHost.exe`。两者都使用本机的 .NET 8 Windows Desktop Runtime。若希望沿用文档和桌面快捷方式中的名称，可以把界面程序复制或重命名为 `Codex飞书助手.exe`，不要改动或漏掉同步宿主。

## 飞书命令

- `绑定 <随机绑定码>`：首次绑定；必须使用桌面面板当前显示的完整命令
- `绑定`：唯一管理员执行过“解绑”后，用同一飞书账号恢复绑定
- `解绑`：解除绑定
- `工作区`：查看飞书新建项目所用的默认工作区
- `新建 codex 项目名`：进入默认工作区中的同名目录；不存在时创建，然后启动 Codex
- `新建 claude 项目名`：同上，启动 Claude Code；也支持写成 `Claude Code`
- `新建 opencode 项目名`：同上，启动 OpenCode
- `状态`：查看桥接器和会话数量
- `会话`：列出活跃助手会话
- `别名`：列出会话别名和命令说明
- `别名 #短ID 名称`：设置别名，例如 `别名 #9b45397b 主项目`
- `别名 #短ID 清除`：清除别名；也可用 `别名 @旧别名 新名称` 修改
- `帮助`：查看回复方式
- `@别名 内容`：通过别名指定一个助手会话继续
- `#短ID 内容`：指定一个助手会话继续
- `排队 @别名 内容`：托管窗口排到下一轮；外部会话会拒绝普通输入
- `排队 #短ID 内容`：按短 ID 排队
- `发文件 @别名 内容`：要求目标助手最终把生成文件发回飞书
- `@别名 /sendfile 内容`：同上

新建命令只在机器人私聊中生效，并且只允许已绑定管理员使用。项目名是单层文件夹名称，可以包含中文和空格，但不能包含盘符、斜杠、`..` 或 Windows 文件名保留字符。桌面面板必须仍在运行或托盘中；飞书启动的新窗口默认使用普通权限，不会远程触发管理员提升。默认工作区可在桌面“设置”中修改；首次升级时默认使用桥接器目录的上一级。

别名为 1–20 个字符，可包含中文、字母、数字、下划线和短横线，不允许空格；活跃会话之间不能重名，拉丁字母不区分大小写。

审批卡片还支持引用回复 `批准`、`拒绝` 或 `本机确认`；`本机确认`只把待处理项转到 Codex 飞书助手的 PC 审批窗口，不会直接批准或拒绝。

补充信息卡片支持点击单选选项；也可以引用卡片回复选项编号/文字。多选题用逗号分隔，例如 `1,3`；多问题按顺序用中文分号分隔，例如 `1；2,3；自定义答案`。题目禁止自定义答案时，只接受列出的选项。

附件用法：在独立会话群里先发送图片或文件，下一条直接发 `分析这个附件`；在机器人私聊且有多个会话时，下一条写成 `@别名 分析这个附件`。最短的文件回传测试是在会话群发送 `发文件 生成一个 test.txt 并发回来`，或在私聊发送 `发文件 @别名 生成一个 test.txt 并发回来`。附件会保存到 `data/uploads/<月份>/`，单个默认最多 25 MiB，暂存区默认最多 500 个文件、总计 1 GiB，并定期清理 7 天前的暂存文件；这些限制可通过 `.env` 中的 `FEISHU_INBOUND_FILE_MAX_BYTES`、`FEISHU_UPLOAD_MAX_FILES`、`FEISHU_UPLOAD_MAX_BYTES` 和 `FEISHU_UPLOAD_TTL_MS` 调整。

## 数据与安全

本地状态存放在 `data/`：

- `bindings.json`
- `sessions.json`
- `message-routes.json`
- `approvals.json`
- `settings.json`

这些 JSON 文件以及 `.env` 都被 `.gitignore` 排除。Hook HTTP 服务只监听本机回环地址。发送审批内容前会对常见 token、secret、password 和 API key 字段做基础脱敏，并限制消息长度。

## 开源协议与项目声明

本项目源代码采用 [MIT License](LICENSE) 开源。你可以使用、修改和分发，但需要保留原版权和许可声明；软件按“现状”提供，不附带任何明示或默示担保。

本项目是社区维护的非官方工具，与 OpenAI、Anthropic、OpenCode 或飞书不存在隶属、授权或背书关系。相关名称和商标归各自权利人所有；第三方 CLI、SDK、在线服务及其生成内容仍分别受其自身许可证、服务条款和使用政策约束，MIT License 不会授予这些第三方产品的权利。

## 行为说明

对于通过“新建 Codex”打开的托管窗口，飞书文字回复会直接写入该窗口的控制台输入缓冲区。会话群内直接发送普通文字，机器人私聊则使用 `@别名 内容` 或 `#短ID 内容`；这些消息默认用 Enter 插话。明确加 `排队` 前缀时使用 Tab，交给下一轮。Stop Hook 继续负责把同一窗口产生的回答通知到对应会话群。

对于通过“新建 Claude”打开的托管窗口，输入同样写入原控制台缓冲区；Claude Code 使用 Enter 接收普通消息和显式排队消息，并在正在执行任务时由自身维护队列。`PermissionRequest`、`AskUserQuestion` 和最终回复都在原会话内完成，不会启动第二个 Claude Code 进程。

对于从其他终端单独打开的非托管 Codex / Claude Code 窗口，桥接器只发送关键通知，并允许处理审批和结构化补充信息；普通消息、排队任务和文件请求都会被拒绝。这样不会在原窗口之外再启动一个同会话进程，避免电脑端与飞书两边同时推进、产生互相打架的分支。需要完整双向同步时，请改用桌面助手对应的“新建”按钮。本项目不使用 Codex `app-server` 后台执行器，也不提供微信通道。

文件回传采用显式协议，只有在使用 `发文件` 或 `/sendfile` 后才会发送。目标助手必须在最终回复中输出 `BRIDGE_SEND_FILE: 绝对路径`；桥接器只接受当前项目目录内、允许扩展名且不超过 30 MiB 的普通文件。

## 恢复参数与工作目录

三种 CLI 的会话记录都能提供原工作目录，但恢复参数本身不统一负责切换目录，因此桌面助手把本机保存的 `session.cwd` 作为唯一启动依据：

| 运行时 | 恢复参数 | 目录行为 |
| --- | --- | --- |
| Codex | `resume <session_id>` | CLI 提供 `-C/--cd`，恢复选择器默认还会按当前目录筛选；助手直接在保存的原目录启动 |
| Claude Code | `--resume <session_id>` / `-r <session_id>` | `--continue` 明确使用当前目录，CLI 没有独立 `--cwd` 参数；助手在保存的原目录启动 |
| OpenCode | `-s <session_id>` / `--session <session_id>` | 项目目录是位置参数或进程当前目录；助手在保存的原目录启动并保留本机端口 |

因此，在“新建”窗口手工填写上述恢复参数时，只要该完整会话 ID 仍存在于本机助手历史记录，项目目录会自动回填。飞书群触发自动恢复时也使用同一份保存目录，不依赖 CLI 猜测。

## 排查

- 没有飞书通知：确认桥接器进程仍在运行，并访问 `/health`。
- Codex 没触发 Hook：新开 Codex 窗口并完成 Hook 信任审核。
- Codex 显示高负载等错误但飞书没收到：确认桥接版本已更新、会话由助手托管且桥接器仍在运行；这类错误由活动转录监视器补发，不依赖 `Stop` Hook。
- Claude Code 没触发 Hook：点击一次“连接”合并 `%USERPROFILE%\.claude\settings.json`，运行 `claude doctor` 检查配置，然后关闭旧窗口并新开 Claude Code。
- 卡片按钮无反应：确认已订阅 `card.action.trigger`，并发布最新应用版本。
- 多窗口回复串线风险：发送 `会话`，再引用对应通知或使用 `@别名` / `#短ID`；需要下一轮时加前缀 `排队`。
- 回复后立即报错：按目标运行时确认 `codex --version` 或 `claude --version` 可在同一 PowerShell 环境正常运行。
- 托管窗口显示“窗口已关闭”：对应的 Windows Terminal / CLI 窗口已经退出，请点击相应“新建”按钮重新打开。
- 已关闭会话的群消息没有自动打开窗口：确认桌面面板仍在运行或托盘中，并确认桥接版本与桌面版本一致；面板彻底退出时不会自动启动 GUI。
- 恢复过的会话关闭后仍不在历史记录：重启新版桥接器以迁移旧记录；新版会在恢复活动时自动清除历史隐藏标记。
- 飞书“新建”命令没有打开窗口：先私聊发送 `工作区` 检查默认目录，并确认桌面面板仍在运行或托盘中；项目名不能包含路径分隔符。
- 长期不活跃群没有自动解散：确认飞书应用已发布 `im:chat:delete` 和 `im:chat:operate_as_owner`；桥接器默认每小时检查一次。
- opencode 窗口显示等待登记但没有会话：确认 `opencode` 命令已安装且可用，并让新窗口保持打开；端口保留后会自动重试健康检查。
- 飞书回复没有进入本地窗口：确认会话列表“方式”一栏显示“窗口同步”或“管理员同步”；外部会话按设计只通知，不接受普通飞书输入。
- 管理员窗口没有启动：回到电脑端完成 UAC 确认；飞书不能代替 UAC。
- 新增 Hook 后没有生效：点击一次“连接”完成 Codex 与 Claude Code 配置合并，关闭旧 CLI 窗口并新开窗口；Codex 还需通过 Hook 信任审核。
- 附件接收失败：下载收到的附件需要 `im:message:readonly`，上传回传附件需要 `im:resource`；确认两项都已加入并发布，且文件未超过本机限制。
