# AI CLI 飞书助手 C# 架构与迁移方案

## 1. 决策

项目最终统一迁移到 C#，删除生产包中的 Node.js、npm、`dist` 和 JavaScript 运行时依赖。

迁移采用渐进替换，不同时重写全部功能。现有 Node 桥接服务在对应功能被 C# 实现、回放测试和实际验收前继续作为唯一生产执行者；C# 新实现先建立协议与纯业务核心，再依次接管运行时、存储和飞书链路。

这项决策解决的是长期维护成本，但当前故障的根因仍然是边界混乱，而不是 TypeScript 语言本身。因此迁移过程中仍遵守“CLI 差异只进入 Adapter、核心只处理标准协议”的原则。

## 2. 最终进程结构

```text
                             Feishu Cloud
                                  ↕
┌────────────────── AiCliFeishuBridgeHost.exe ──────────────────┐
│  Feishu Adapter                                               │
│         ↕                                                     │
│  Bridge Core ── Session / Approval / Input / Retry / Launch   │
│         ↕                                                     │
│  Bridge Protocol + Runtime Adapter Registry                   │
│         ↕                         ↕                           │
│  Codex / Claude Adapter          OpenCode Adapter              │
│  Hook HTTP + Terminal IPC        HTTP API + SSE                │
└───────────────↕────────────────────────────↕───────────────────┘
                ↕                            ↕
     AiCliFeishuTerminalHost.exe       OpenCode Process
                ↕
        Codex / Claude Code

          AiCliFeishuControl.exe
          WinForms control plane
                    ↕
          Local authenticated API
                    ↕
          AiCliFeishuBridgeHost.exe
```

三个进程均为 C#：

- `AiCliFeishuBridgeHost.exe` 是后台宿主，使用 .NET Generic Host / ASP.NET Core，承载飞书长连接、本机 Hook HTTP、后台 Worker、核心状态机和持久化；
- `AiCliFeishuControl.exe` 是 WinForms 控制面板，只展示和修改状态，不承担后台任务调度；
- `AiCliFeishuTerminalHost.exe` 保留为终端 Sidecar，隔离 CLI 控制台、输入和生命周期。版本化宿主逻辑继续保留。

控制面板退出或重启不应中断后台桥接；终端宿主异常不应带崩后台服务。

## 3. 代码结构

迁移期间新增代码放在 `bridge-dotnet/`，避免与现有桌面端和 Node 代码混杂：

```text
protocol/v1/                         语言无关 JSON Schema 与样例
bridge-dotnet/src/
  AiCliFeishu.Bridge.Protocol/       C# 协议模型、序列化和验证
  AiCliFeishu.Bridge.Core/           纯业务、能力模型、端口接口
  AiCliFeishu.Bridge.Adapters.*/     CLI、飞书、存储的实现（后续）
  AiCliFeishu.Bridge.Host/           后台进程装配（后续）
bridge-dotnet/tests/
  AiCliFeishu.Bridge.Core.Tests/     协议与核心契约测试
desktop-control/                     WinForms 与 TerminalHost
src/                                 迁移期间保留的 Node 实现
```

依赖只能向内：Host 和 Adapter 可以依赖 Core / Protocol，Core 只能依赖 Protocol，Protocol 不依赖任何 SDK、桌面 UI 或基础设施。

## 4. 标准桥接协议

`protocol/v1/` 是跨语言事实来源。协议版本初始固定为 `1`，TypeScript 与 C# 都必须通过相同样例和字段契约测试。

事件和命令使用统一信封：

```json
{
  "protocolVersion": 1,
  "runtime": "codex",
  "session": {
    "externalId": "session-id",
    "cwd": "K:\\project"
  },
  "traceId": "trace-id",
  "correlationId": "optional-related-id"
}
```

`traceId` 串联一次完整链路；`correlationId` 关联审批请求与结果、补充问题与答案、命令与执行结果。

### 标准事件

- `session.started`、`session.ended`
- `turn.started`、`turn.activity`、`turn.completed`、`turn.failed`
- `approval.requested`、`approval.resolved_externally`
- `input.requested`、`input.resolved_externally`
- `runtime.connected`、`runtime.disconnected`

`approval.requested` 和 `input.requested` 必须携带绝对时间 `expiresAt`，让超时策略在 Adapter
边界被明确归一化，而不是由 Core 猜测。补充问题的每个 question 可携带 `allowsCustom`；缺失时
按 `true` 处理，以兼容 CLI 允许用户输入自定义答案的默认语义。当前 v1 仍处在切换前的 Shadow
迁移阶段，因此直接收紧 v1 契约，不额外保留尚未成为生产入口的旧 C# 契约。

### 标准命令

- `prompt.send`
- `approval.resolve`
- `input.resolve`
- `session.launch`
- `session.resume`
- `session.stop`

协议只表达桥接层语义。例如核心层只表达“本次允许”“会话内允许”“拒绝”，Codex、Claude Code 和 OpenCode Adapter 分别翻译为自己的原生参数。

## 5. 分层职责

### Runtime Adapter

- 接收原生 Hook、SSE、HTTP 响应或终端状态；
- 将原始输入翻译为标准 `RuntimeEvent`；
- 将标准 `RuntimeCommand` 翻译为 API、终端输入或 Hook 响应；
- 声明运行时能力；
- 不负责飞书卡片、群聊路由、业务重试或持久化策略。

### Bridge Core

- 统一管理会话、轮次、审批、补充问题、消息队列、重试、启动任务和通知状态；
- 只接收标准事件，只发出标准命令；
- 决定业务“做什么”，不决定某个 CLI“怎么做”；
- 不引用 WinForms、飞书 SDK、Hook payload、SSE payload 或具体 CLI Client。

### Feishu Adapter

- 将核心标准视图渲染成文字或卡片；
- 将飞书消息和卡片回调翻译为核心输入；
- 负责飞书长连接、发送、更新和回调协议；
- 不直接调用任何 CLI Adapter。

## 6. 同步 Hook 与异步 SSE

传输可以不同，业务语义必须相同。

同步 Hook 的审批请求在原 HTTP 请求中等待结果：Adapter 产生 `approval.requested`，等待核心返回对应的 `approval.resolve`，再转换为 Hook 响应。异步 SSE 产生相同事件，之后 Adapter 将标准决定调用到 CLI API。

Bridge Core 不区分审批来自阻塞 Hook 还是 SSE。超时、重复点击、本地已处理和飞书卡片失效由同一审批状态机处理；等待方式和响应格式留在 Adapter 内。

## 7. 运行时能力

```text
prompt.send       发送实时消息
prompt.queue      CLI 原生支持消息排队
approval.resolve  执行审批决定
input.resolve     回答补充问题
session.launch    新建会话
session.resume    恢复已有会话
session.stop      停止会话
activity.stream   持续产生结构化活动事件
```

Core 创建命令前必须检查能力。能力缺失时返回明确错误，不能静默套用其他 CLI 的行为。外部或历史 Codex 通过一次性进程恢复属于 `session.resume`，不与已连接终端的 `prompt.send` 混为一条路径。

## 8. 状态所有权与切换原则

迁移期间最危险的是 Node 和 C# 同时修改状态或同时响应飞书，因此遵守以下规则：

- 任一功能在任一时刻只有一个 Active Owner；
- C# 影子模式可以读取、归一化、记录和对比，但不得发送飞书消息、执行 CLI 命令或写生产状态；
- 功能切换以完整纵切片进行，例如“OpenCode 实时消息”从输入到状态到输出整体切换；
- 切换必须有回退开关，回退时仍只有一个写入者；
- 迁移前保持现有 JSON 数据格式可读，不顺带更换数据库；
- 最终切换前停止 Node、刷盘、由 C# 取得单实例锁，然后启动 C# Active Owner。

## 9. 可观测性

标准日志阶段为：

```text
ingress.received
adapter.normalized
core.accepted
core.persisted
feishu.sent
feishu.callback_received
core.command_created
adapter.command_started
adapter.command_completed
adapter.command_failed
```

每条跨层链路使用同一个 `traceId`。日志至少包含阶段、运行时、内部会话 ID、外部会话 ID、事件或命令类型和耗时；不得包含令牌、完整敏感命令、秘密答案、附件内容或不必要的文件正文。

## 10. 迁移阶段

### M0：协议与 C# 核心骨架

- 建立版本化 JSON Schema 和跨语言样例；
- 建立 C# Protocol / Core / Tests 工程；
- 实现能力模型、Adapter 注册表、命令调度边界和协议验证；
- 保留 TypeScript Adapter 作为迁移期隔离层。

验收：Node 全套测试和桌面端测试不回退；C# 能反序列化并验证共享样例；调度器不会将命令交给能力不匹配的 Adapter。

### M1：行为录制与回放基线

- 对现有 Node 的 Hook、SSE、核心状态变化、飞书输出建立脱敏录制；
- 建立 C# 影子归一化和差异报告；
- 为审批、问答、消息、重试和启动建立黄金样例。

验收：C# 对同一输入生成与 Node 等价的标准事件和业务决定，且影子模式没有外部副作用。

M1 的旁路录制默认关闭。需要采样时设置
`AI_CLI_FEISHU_MIGRATION_RECORDING=1`，Node 会把脱敏后的 JSONL 写入
`data/migration-recordings/node-behavior-v1.jsonl`。录制失败只记一次错误，
不能阻断 Hook、CLI 或飞书链路；记录中正文、路径、标识符和秘密均不以原文出现。

共享契约和五类黄金样例位于 `protocol/migration/v1/`。C# 回放器只读输入，
不加载生产 Store、不连接 CLI、不调用飞书，也不写回录制文件：

```powershell
dotnet run --project .\bridge-dotnet\src\AiCliFeishu.Bridge.Replay\AiCliFeishu.Bridge.Replay.csproj -- .\data\migration-recordings\node-behavior-v1.jsonl
```

输出包括总记录数、匹配数、差异数、非法记录数，并按记录 ID 和 JSON 路径列出差异；
存在差异、非法记录或空输入时返回非零退出码。M1 期间 Node 仍是唯一 Active Owner，
C# 仅做影子归一化和对比。

### M2：存储与纯业务状态机

- 在 C# 实现现有 JSON 存储的兼容读取和原子写入；
- 迁移会话目录、审批、补充问题、消息路由、重试和启动任务；
- WinForms 继续通过本机 API 读取，不直接访问存储文件。

验收：使用生产数据副本可无损启动；状态迁移和保留策略通过回放测试；Node 与 C# 不同时写文件。

M2 的 C# Storage Adapter 默认以 `ReadOnly` 打开 Node Store，只兼容读取
`bindings.json`、`sessions.json`、`message-routes.json`、`approvals.json`、
`settings.json` 和 `control-token.json`。未显式建模的 JSON 字段通过扩展字段原样保留；
非法结构只报告错误，不隔离、不覆盖生产文件。只有测试或迁移工具创建的独立副本才能显式使用
`ReadWriteCopy`，写入采用同目录临时文件刷盘后原子替换。

会话、审批、补充问题、消息路由、重试和启动任务已作为无 IO 的不可变状态转换放入 Core。
审批、补充问题、消息去重、重试和启动均使用“创建/领取/完成”边界，重复完成不会再次产生业务决定；
路由和审批保留策略与 Node 的时间和数量限制保持一致，待审批记录不受已完成记录数量上限影响。

可只读检查现有 Store；该命令不会写入 `data`：

```powershell
dotnet run --project .\bridge-dotnet\src\AiCliFeishu.Bridge.StoreVerify\AiCliFeishu.Bridge.StoreVerify.csproj -- .\data
```

需要验证原子写回时，必须提供一个不存在或为空、且不同于生产 Store 的副本目录：

```powershell
dotnet run --project .\bridge-dotnet\src\AiCliFeishu.Bridge.StoreVerify\AiCliFeishu.Bridge.StoreVerify.csproj -- .\data --roundtrip-copy .\.m2-store-copy
```

M2 仍不建立后台 Host，也不连接 CLI 或飞书。Node 继续独占生产 Store；C# 的写入能力仅用于
副本验收，正式取得 Active Owner 要等 M5 的停止 Node、刷盘和单实例锁切换流程。

### M3：Runtime Adapter

- 新增 `AiCliFeishu.Bridge.Adapters.ManagedTerminal`，由 Codex 和 Claude Code 共用；
- 新增 `AiCliFeishu.Bridge.Adapters.OpenCode`，隔离 OpenCode HTTP、权限 API 回退和 SSE；
- TerminalHost 命名管道只在 Managed Adapter 内处理，Core 只看到 `prompt.send` / `approval.resolve` /
  `input.resolve` / `session.*` 标准命令；
- Hook 字段、OpenCode `properties`、SSE 分块和 CLI 私有错误均先在 Adapter 归一化，再交给标准事件；
- `RuntimeCommandContext` 在所有端口间携带 `commandId`、`traceId` 和 `correlationId`；
- 同步 Hook 的等待、重复请求缓存以及 Claude Code `updatedInput` 回包留在 Managed Adapter；
- OpenCode 的事件源、启动/恢复后的 `WaitUntilReadyAsync` 留在 OpenCode Adapter；仅对带稳定
  request / part ID 的一次性交互和消息部件更新做有界去重，`session.idle` 等轮次事件不去重；
- Node 仍是唯一 Active Owner。M3 只编译和回放，不启动生产 Host、不连接真实飞书、不写生产 Store、不操作真实 CLI。

验收：三种 CLI 的标准契约测试共享同一组命令断言；命名管道、OpenCode HTTP 回退、SSE 分块、Hook
同步回包和重复事件均有 Fake 线协议测试；Core 不出现 CLI 私有 payload 或 Manager。运行：

```powershell
dotnet test .\bridge-dotnet\tests\AiCliFeishu.Bridge.RuntimeAdapters.Tests\AiCliFeishu.Bridge.RuntimeAdapters.Tests.csproj -c Release
```

### M4：飞书 Adapter

- 新增 `AiCliFeishu.Bridge.Adapters.Feishu`，把飞书 HTTP、WebSocket、protobuf、消息、附件、
  群聊和卡片限制在同一个外层 Adapter；
- 飞书原生事件必须先经 `FeishuEventNormalizer` 转换为 `FeishuIntent`，再进入 Core；`/`、
  `/新建` 等全局命令不依赖某个活跃 CLI 会话；命令目录、新建运行时选择和项目表单由 C#
  Renderer 生成，卡片 action 只允许登记过的白名单；
- 飞书 Adapter 只能调用 Core 的标准输入端口，不能引用或直接调用 ManagedTerminal / OpenCode
  Runtime Adapter。Core 产生标准 `RuntimeCommand` 后，再由 M3 的 Adapter Registry 分派；
- `ApprovalStateMachine` 和 `InputStateMachine` 是审批、问答的唯一业务决策方。飞书先操作时 Core
  完成状态迁移，本地重复操作无效；CLI 本地先处理时 Core 完成状态迁移，关联飞书卡片全部更新为
  无按钮的“已处理”，飞书重复点击无效；
- 卡片同步使用 `(messageId, revision)` 幂等台账。发送失败释放 claim，允许同一 revision 重试；
- WebSocket 实现与飞书 Node SDK 的 `pbbp2.Frame` 字段号、0 起始分片、callback ACK 和 ping 契约
  对齐；协议编解码不引入额外 protobuf 包；
- 附件下载采用流式大小限制，超限或失败删除半文件；上传在 401 / 403 刷新 token 后重新创建
  文件流和 multipart，不复用已经消费或释放的请求体；
- Node 仍是唯一生产 Active Owner。M4 只运行 Fake HTTP、内存 WebSocket 和协议回放，不连接真实
  飞书、不发送真实消息、不写生产 Store、不启动或停止真实 CLI。

验收：飞书事件只能经过“原生线协议 → 标准意图 → Core → 标准命令 → Runtime Adapter”；
消息命令、命令目录和新建流程卡片、卡片白名单、HTTP token 刷新、群聊、附件、protobuf、
乱序/重复分片、接收或 ping 故障后的断线重连、
事件 ACK、审批/问答双端幂等和卡片 revision 均有无外部副作用的契约测试。运行：

```powershell
dotnet test .\bridge-dotnet\tests\AiCliFeishu.Bridge.FeishuAdapter.Tests\AiCliFeishu.Bridge.FeishuAdapter.Tests.csproj -c Release
```

### M5：后台 Host 与正式切换

- 建立独立 Bridge Host 生命周期、单实例锁和健康检查；
- 控制面板切换为启动和管理 C# Host；
- 发布包改为纯 .NET，不再携带 Node、npm、`dist` 或 `node_modules`；
- 灰度运行后执行正式切换和回退演练。

验收：控制面板重启不影响桥接；安装包无需 Node；三种 CLI、飞书、开机启动和版本化 TerminalHost 全链路通过。

M5 先以被动模式建立 `AiCliFeishu.Bridge.Host` 装配根和测试边界：

- Generic Host / Kestrel 只监听回环地址，默认使用与当前生产端口隔离的 `8876`；
- `data/bridge-host-{instance}.lock` 使用跨进程独占写句柄实现单实例租约，进程退出后可重新取得；
- 生产 Store 另有跨语言 `data/bridge-active-owner.lock/owner.json` 所有权租约，契约版本保存在 `protocol/ownership/v1/`。Node 会在加载或写入 Store 前，以完整临时目录写入、刷盘并原子发布该租约；第二个在线 Owner 会被拒绝，有效但 PID 已退出的残留可原子隔离回收，路径类型或元数据损坏则安全拒绝。平滑停止时只有完成 Controller/CLI 清理、Store 刷盘和行为记录器关闭后才释放租约，且释放前核对 `leaseId`，不会删除已替换的 Owner；
- Passive C# Host 的第一个子系统只读取共享租约并以不含路径、PID、leaseId 或时间的粗粒度健康状态报告 `live`、`stale`、`missing` 或 `invalid`，不会创建、隔离、删除或取得生产租约。`BridgeHostOptions.Validate()` 仍硬性拒绝 `active`，因此这一切片只建立交接协议，不改变生产流量或写入所有权；
- C# 已实现与 Node 同契约的原子取得器，用隔离目录验证完整元数据刷盘、目录原子发布、在线 Owner 拒绝、死亡租约确定性墓碑、并发回收唯一胜者和释放身份校验。Active 分支现将它登记为第一个生产能力和位于其他运行时工作之前的 Hosted Service，使未来完整 Active Host 必须先取得共享租约并在停机末尾释放；
- Host 装配根现按所有权模式显式分成 Infrastructure、Passive 和预留 Active 三部分：Passive 分支唯一注册只读 Store、无网络飞书、空 OpenCode 事件源和拒绝执行的 Runtime 端口；Active 分支不会继承任何 Passive 实现，目前只具备 Active Owner 租约这一项生产能力，因此不能形成可启动的生产对象图；
- `BridgeProductionAssemblyPreflight` 在构建 ServiceProvider、启动 Hosted Service 或监听 Kestrel 之前静态审查装配描述符。未来 Active Host 必须对 Active Owner 租约、生产 Store、持久化业务状态、飞书凭据/事件/发送、Managed Terminal 目录/传输/生命周期/Hook ingress/回包、OpenCode 目录/事件/传输/生命周期 15 项能力逐项声明唯一且类型匹配的生产所有者；缺失、重复、未注册或回退到 Passive 实现均 fail closed，预检本身不解析服务，因而不会取得租约、读写 Store、打开网络或启动 CLI；
- `/health` 无令牌时只返回存活结果，携带既有本机控制令牌时才返回进程、生命周期、组件和所有权状态；
- `/control/shutdown` 沿用 JSON Content-Type、Fetch Metadata 和定时比较控制令牌的安全边界；
- 后台子系统按注册顺序启动、逆序停止，启动失败会清理已启动组件并保留 `faulted` 健康状态；
- Host 装配只暴露三条标准边界：`IRuntimeEventSink → IBridgeRuntimeEventHandler`、`IFeishuIntentSink → IBridgeFeishuIntentHandler`、`IBridgeRuntimeCommandGateway → RuntimeCommandDispatcher → IRuntimeAdapter`；Runtime 事件先通过 Bridge Protocol 校验，再串行进入唯一状态处理器，成功事件有界去重、失败事件允许重试；飞书意图必须属于登记类型且只能进入唯一业务决策处理器；
- `ReadOnlyNodeStoreShadow` 只用 M2 的 `NodeStoreAccess.ReadOnly` 加载现有 Store 并生成 C# Core 投影；缺失 Store 不创建文件，非法 Store 不隔离、不修复、不覆盖，健康摘要只包含文件、会话、路由、审批和绑定数量或不兼容文件名，不暴露令牌、路径和业务标识；
- `BridgeBusinessStateOwner` 是 Host 内唯一的 Runtime 事件和飞书意图处理器；它必须在 `ReadOnlyNodeStoreShadow` 成功投影后启动，再以不可变的 `SessionDirectoryState`、`ApprovalRegistryState` 和 `InputRegistryState` 维护唯一内存业务快照。标准事件只能通过 Core 状态机原子更新该快照；未知请求或非法顺序不会产生部分更新，Event ID 重复和已完成请求的语义重复不会增加业务 revision；
- 认证的本机 `POST /control/runtime-events` 是标准 Runtime 事件的临时接入口：只接受 `application/json`，先做控制令牌、Fetch Metadata 和 Bridge Protocol 校验，再串行进入唯一 ingress；重复 Event ID 幂等返回 202，非法 JSON 返回 400，协议或业务顺序错误返回 422。该入口在 Shadow 模式下仍不启动 CLI、不写 Store、不发送飞书；
- 认证的本机 `POST /control/feishu-intents` 是 Feishu Adapter 的标准意图接入口：只接受 `application/json`，先做控制令牌、Fetch Metadata 和意图白名单校验，再进入唯一 Feishu ingress；返回标准 callback 结果，不把 Feishu SDK 或原始事件带入 Core。Shadow 模式下 callback 明确为只读 warning，禁止伪装成功或产生外部副作用；
- 认证的本机 `POST /control/runtime-commands` 是标准 Runtime 命令的控制面入口：只接受 `application/json`，先做控制令牌、Fetch Metadata 和 Bridge Protocol 校验，再由唯一命令网关检查目标会话就绪状态并分派。Passive/Shadow 所有权会明确返回 503，禁止调用任何 Runtime Adapter；只有未来经过显式 Active Owner 切换后才允许执行。成功或已完成命令重复返回 202，协议错误返回 422，未注册或未就绪 Adapter 返回 503；
- 三个标准入口只有在业务状态已从兼容 Store 投影初始化后才接收事件、意图或命令；Store 缺失时以空投影初始化，Store 不兼容或投影失败时明确返回 503，不把所有权/初始化故障误报成协议 422 或业务成功；
- Shadow 的飞书意图不会调用 Runtime Adapter、发送飞书消息或写 Store，而是明确返回“只读观测、未执行”的 warning。此拒绝是所有权闸门，不得伪装成业务成功；
- C# Host 将业务状态从生命周期探针中分离：`GET /control/status` 必须携带本机控制令牌并通过 Fetch Metadata 检查，除 Store 聚合计数外只返回业务状态的初始化标志、来源状态、revision、Shadow 飞书意图拒绝计数以及会话/审批/补充问题数量；`?refresh=1` 会从磁盘重新执行一次只读投影；会话 ID、工作目录、Open ID、Chat ID、消息/请求 ID、工具预览、用户内容、控制令牌及完整路径均不会进入响应；公开 `/health` 的语义和响应保持不变；
- 认证状态同时返回标准边界清单：唯一 Runtime 事件/飞书意图处理器数量、Runtime 命令所有权闸门状态，以及已注册 Adapter 的 runtime 和标准能力名。该清单不探测或暴露任何具体会话，只用于控制面判断装配完整度；Passive Host 固定报告 `blocked_passive_owner`；
- Host 已注册 Codex、Claude Code 和 OpenCode 三个真实标准 Adapter 类型，使协议分派和能力清单进入正式装配根；其底层在 Passive 模式使用显式拒绝端口，所有会话均报告未就绪，不连接命名管道、不调用 OpenCode HTTP、不启动/停止 CLI，也不回写同步 Hook。命令仍先经过 `BridgeRuntimeCommandIngress` 的所有权闸门，因此不会到达这些端口；
- Runtime 入站侧也已把 Managed Terminal 的 Hook Normalizer / Bridge 与 OpenCode 的事件 Normalizer / Pump 接入唯一 `IRuntimeEventSink`，认证状态只报告固定组件清单。Passive 模式不开放 Hook HTTP 路由，并使用立即结束的 OpenCode 事件源，不订阅 SSE、不连接 CLI；装配校验只解析对象图，不处理真实 Runtime 事件；
- Host 也将 Feishu Adapter 的事件源、事件规范化器、标准意图 Sink、卡片渲染器、交互协调器、事件泵和 Gateway 纳入唯一装配边界，并在认证状态中报告不含凭据的组件清单。Passive 模式只装配空事件源和显式拒绝外发的 Gateway，不创建真实 WebSocket、不请求飞书 API、不发送或更新消息；边界校验只解析对象图，不运行事件泵；
- Feishu Event Pump 已进入 Host 子系统生命周期，并排在业务状态初始化之后启动、在业务状态停止之前退出。Passive 模式会实际运行一次立即结束的空事件源来验证托管链路，并以 `feishu-event-pump: passive` 报告健康状态；它仍不建立 WebSocket、不处理真实事件；
- OpenCode Endpoint Directory 现在同时支持按会话查找与枚举就绪实例，OpenCode Event Pump 也进入 Host 子系统生命周期。Passive 目录固定返回空集合，Worker 在看到任何就绪端点时会在订阅前拒绝启动，并以 `opencode-event-pump: passive` 报告健康状态，因此 Shadow 运行不会构造假端点或请求 SSE；
- Node 与 C# Host 的认证健康响应统一声明 `hostKind`、`managementApiVersion`、`ownershipMode`、`activeOwner` 和进程号；公开 `/health` 仍只返回 `{ ok: true }`。停止请求必须回传预期 Host 类型、管理 API 版本和刚探测到的进程号，Host 会和自身身份再次比对，防止控制面板因端口复用或进程重启停错目标；
- 控制面板不再把 `/control/shutdown` 的 `202 Accepted` 当作停止完成：它会继续等待已认证的目标 PID 真正退出并确认端口离线；原 PID 仍在时继续等待，同身份新 PID、无法认证的占用者或超时都会明确中止，给后续 Store 锁交接和 Active Owner 切换提供无竞态的停止边界；
- 控制面板新增显式 `AI_CLI_FEISHU_BRIDGE_HOST=dotnet-shadow` 灰度入口：仅在设置该值时才启动 `AiCliFeishuBridgeHost.exe --ownership passive --instance desktop-shadow`，固定使用隔离端口 `8876`，不安装生产 Hook，也不开放 CLI 启动、审批或设置写操作；未设置或设置为 `node` 时仍严格使用现有 Node 生产 Host 和 `.env` 中的端口，不会静默回退到 C#；
- 桌面发布和 Windows ZIP 现在携带版本一致的单文件 `AiCliFeishuBridgeHost.exe` sidecar，并校验该文件存在、版本正确且没有 loose DLL、runtimeconfig、deps 或 PDB 残留；这只让显式 `dotnet-shadow` 灰度入口在安装产物中可用，不改变 Node 的默认生产所有权；
- CI 会运行全部 C# 测试，并从干净临时目录发布桌面产物、校验三个 EXE 的版本和单文件边界，再按控制面板使用的 `DOTNET_ROLL_FORWARD=Major` 环境真实启动 Passive Host 并探测回环 `/health`；验证数据和进程只存在于隔离临时目录，不接触生产 Store、CLI 或飞书；
- 桌面控制层新增纯 `BridgeHostCutoverTransaction` 模型，用认证身份、目标 PID、Store 刷盘/兼容性和 Active Owner 租约证据约束 `停止 Node → 确认原 PID 离线 → 验证 Store 可交接 → 记录 C# 启动 PID → 验证 C# Active → 完成`。事件越序会被拒绝且不改变状态，重复的当前阶段事件幂等；Node 身份不匹配、Store 未刷盘或不兼容、租约仍为 `live`/`invalid` 时安全中止；C# 进程一旦启动，身份或健康验证失败只能进入 `RollbackRequired`，并强制 `停止 C# → 确认其 PID 离线 → 启动并验证新的 Node PID` 后才能标记回退完成；
- 纯 `BridgeHostCutoverTransaction` 模型本身不执行进程、HTTP、Store、租约或飞书操作；公开快照只包含阶段、是否仍需回退和粗粒度失败原因，不暴露 PID、路径、令牌或 leaseId；
- 在纯模型之上增加了只依赖 `IBridgeHostCutoverOperations` 的 `BridgeHostCutoverCoordinator`，但当前仍只在桌面隔离测试中使用。它把发起 Node 停止请求作为不可取消安全阶段的起点：请求发出后，调用方取消不会打断离线确认、Store 检查或恢复链；Store 未刷盘/不兼容且没有 Active Owner 冲突时会恢复并验证 Node；Node 停止结果不确定、租约为 `live`/`invalid`、C# 启动返回无效 PID，或“启动调用抛错但实际是否启动未知”时不会猜测所有权，不自动启动任何 Owner，而是返回 `FailedSafe` 并要求后续人工/专门恢复流程；
- 协调器的操作接口只表达停止、离线验证、只读 Store/租约检查、启动和身份验证，不向状态机暴露实现类型、真实路径、控制令牌、HTTP 客户端或进程对象；记录型假操作继续覆盖成功、租约冲突、Store 恢复、身份替换、回退失败、取消和并发事务隔离；
- 桌面控制层已有未接入生产入口的 `BridgeHostCutoverProcessOperations` 真实进程适配器。它只接受精确的本机回环 HTTP Origin、禁止自动重定向，且要求显式注入控制令牌、Node/C# 进程启动描述和只读 Store/租约检查器；Node 通过认证 `/health`、C# 通过认证 `/control/status` 读取身份，停止请求携带 Host/API/PID 三重身份，离线确认复用 `BridgeHostExitWaiter` 并同时检查目标 PID 和端口；未知 HTTP 响应、无法认证的占用者或探测超时都不会被误判为离线，停止或启动结果不确定时统一进入 `OwnershipUncertain`；
- Node 的认证 `/health` 现在包含固定 `instanceName: production`，使切换事务可以在停止前验证完整 Node 身份；公开、未认证的健康响应仍只包含存活信息；
- 真实进程演练已在随机回环端口、临时工作目录和专用最小 ASP.NET 测试子进程中覆盖 `Node → C#` 成功切换、C# 身份不匹配后的停止与 Node 回退，以及 `live`/`invalid` 租约冲突下不启动第二 Owner。演练不读取或写入生产 Store，不连接 CLI 或飞书，临时进程和目录在测试结束后清理；
- Active Owner 租约契约和只读观察器已从 Web Host 下沉到 Storage Adapter，Host 的被动所有权守卫、隔离取得器和桌面交接检查器共用同一套 `owner.json` 严格解析与进程存活判定；桌面仍只以 `ReferenceOutputAssembly="false"` 构建 Web Host sidecar，不会把 ASP.NET Host 程序集编入客户端；
- 桌面控制层新增未接入生产入口的 `ProductionBridgeStoreHandoffInspector`。它只以 `NodeStoreAccess.ReadOnly` 加载已登记的六个 Node Store 文件，并在校验前后各观察一次共享租约：只有稳定的 `missing + Store 兼容` 同时证明 Node 已按平滑关闭顺序完成 `store.close()` 并允许交接；稳定 `stale` 只表示旧 PID 死亡，不能证明崩溃前已刷盘，因此要求恢复 Node；`live`/`invalid` 在读取 Store 前立即安全拒绝，并保留 `StoreCompatible` 为未否定状态而不伪造一次未执行的 Store 检查结论；
- 交接检查期间租约状态或完整身份发生任何变化（例如 `missing → live`、`stale A → stale B` 或租约消失）都会返回粗粒度 `invalid` 证据，禁止启动新 Owner。Store 文件缺失继续采用 Node 的空文档兼容回退；结构不兼容、畸形 JSON、I/O 或访问失败只报告不兼容，不修复、不隔离、不重写或创建任何生产文件；取消与未知程序错误不会被吞成兼容证据；
- 桌面控制层新增纯 `BridgeHostRecoveryPlanner`，把 `Completed` 作为唯一切换提交点：提交前若观察到身份和 `live` 租约完全一致的 C# Owner，只生成 `停止 C# → 确认离线 → 重新检查 Store → 启动并验证 Node` 的回退步骤；提交后才允许保留或在无 Owner 且租约稳定缺失时重启 C#。Node 已恢复且身份/租约一致时不执行动作；端点不确定、认证身份与租约不一致、`stale`/`invalid`、隐藏的 `live` Owner 或提交后意外出现 Node 时一律只返回人工接管；
- 恢复计划公开结果只包含粗粒度 disposition、reason 和固定步骤名，不含 PID、路径、令牌、leaseId 或时间。纯 `BridgeHostRecoveryPlanner` 仍不探测端点、不执行停止/启动、不持久化切换检查点；后续的观察器和执行器必须把它当作无副作用的决策函数使用；
- 桌面控制层新增未接入生产入口的版本化 `bridge-host-cutover.checkpoint.json` 检查点契约与 `BridgeHostCutoverCheckpointStore`：阶段、失败原因、预期 Node 身份、目标 .NET 实例名和必要进程号使用严格字符串枚举与精确字段集；读取区分 `missing`、`present`、`invalid`、`unavailable`，不会因路径类型错误、畸形 JSON、未知字段或权限/I/O 故障猜测为缺失；写入先校验、同目录临时文件写入并刷盘，再原子替换，失败或取消会清理临时文件且不覆盖旧检查点；JSON 不含控制令牌、Store leaseId、业务路径或用户内容；
- 桌面控制层新增未接入生产入口的 `BridgeHostRecoveryObserver`：先读检查点，再以双重采样读取认证回环 `/health` 身份和共享 Active Owner 租约，最后复读检查点；检查点、端点身份或租约在采样窗口内变化时只返回人工接管，连接被拒绝才可判定离线，未认证响应、超时、畸形响应和其他传输异常一律是不确定；稳定证据才交给纯规划器。观察器不读取或修改 Store，不停止/启动进程，也不把 PID、路径、控制令牌或 leaseId 暴露到检查结果；真正执行 `InspectStoreHandoff` 仍必须紧贴 Owner 启动前重新完成；
- 桌面控制层新增未接入生产入口的 `BridgeHostCutoverCheckpointWriter`：以持有式 OS 文件句柄保证同一数据目录只有一个遵守协议的检查点写者；写入前核对 operationId、当前检查点状态、阶段迁移白名单和严格递增的 `updatedAt`，相同检查点幂等返回，活动 operationId 冲突或非法迁移只返回冲突而不覆盖文件；只有安全终态 `Completed`/`RolledBack` 后才允许显式开始新的 operationId。锁文件保持为空且释放后不删除，避免删除/重建路径造成双写者；每次读取保留原始字节 SHA-256 文件版本，写入前执行版本比较并交换，原文件即使被重写成语义相同的内容也会报告版本冲突；进程崩溃后 OS 会释放持有式锁，正式检查点仍保持最后一次原子发布结果，同 operationId 可从该阶段继续，其他 operationId 仍受终态闸门约束；
- 检查点写入者的崩溃恢复协议同样只存在于隔离模块：取得同一把写锁后若发现严格匹配 `bridge-host-cutover.checkpoint.json.<pid>.<nonce>.tmp` 的遗留文件，新写者返回 `RecoveryRequired`，不会把未发布内容当作检查点；显式恢复再次持锁并只把这些文件移动到 `bridge-host-cutover.orphaned` 隔离目录，不删除、不回放、不覆盖正式检查点。畸形临时文件名、目录或重解析点伪装、隔离目标冲突和 I/O/权限异常都保持原状并要求人工接管；该协议目前仍未接入协调器或生产入口；
- 桌面控制层新增未接入生产入口的 `BridgeHostRecoveryExecutor`：它先做预读，再取得并在整个恢复期间持有同一把检查点写锁；锁内重新读取检查点、双重采样认证端点与 Active Owner 租约，并复读检查点，所有自动动作都绑定检查点原始字节 SHA-256 版本。每次停止或启动副作用前都会再次核对文件版本，紧贴 Owner 启动前重新检查 Store/租约交接；提交前停止 C# 时要求 checkpoint 中 PID、Host 类型、管理 API、active 所有权和实例名构成的完整预期身份与实时身份逐字段一致，任一不确定证据都停止自动恢复；
- 恢复启动 Node 或 C# 后，执行器先验证实际启动 PID 的完整身份，再通过观察器对端点身份和 Active Owner 租约双采样，最后再次验证同一启动 PID，以确认身份和租约稳定收敛。成功恢复 Node 会用检查点文件版本 CAS 将未完成操作收敛到 `RolledBack` 并记录恢复 PID；已是 `RolledBack` 的检查点保持历史不重写，提交后的 C# 恢复保持 `Completed`。执行结果只公开粗粒度 state/plan，不携带 PID、路径、令牌或租约身份；
- 隔离恢复执行器已有真实子进程集成演练：测试专用 Host fixture 可选在每个随机临时数据目录中原子发布与自身 PID/Host/实例绑定的 `owner.json`，只在确认 leaseId 和 PID 仍属于自己时清理租约；演练覆盖离线提交前检查点启动真实 Node 并 CAS 收敛到 `RolledBack`、离线 `Completed` 启动真实 C# 且保持检查点字节不变、在线提交前 C# 经完整身份核对后停止并恢复真实 Node，以及实例身份不匹配时拒绝停止或启动任何 Owner。fixture 的租约在监听前建立、优雅退出时释放，交接检查直接读取该隔离租约，不读取生产 Store 或路径；
- 桌面控制层新增仍未接入入口的 `BridgeHostPersistentCutoverCoordinator`：取得检查点写锁后先拒绝未完成/无效旧检查点，再在任何所有权副作用前持久化 `Planned` 和对应阶段意图；Node 停止一旦开始即使用不可取消安全序列，阶段观察成功后立即刷盘，回退停止 C# 时使用检查点绑定的完整 Host/API/PID/实例身份，且紧贴 Node 启动前再次检查 Store 刷盘、兼容性和租约缺失。只有 `Completed` 检查点写入成功才报告提交，持久化冲突、不可用或恢复要求只返回粗粒度结果并保留最后一个耐久阶段，不继续执行未记录副作用；
- 真实进程适配器为持久化协调增加了窄范围启动绑定回调：`Process.Start` 取得 PID 后、启动方法返回前必须由协调器把 `DotNetStartRequested` 或 `NodeRollbackStartRequested` 原子写入检查点；绑定写入失败会直接停止协调器，恢复器随后只按已持久化证据决策。OS 进程建立与回调开始之间仍存在无法由当前进程 API 消除的极小崩溃窗口；该窗口保持保守人工恢复，不猜 PID、不凭 PID 停止 C#、不弱化身份验证。隔离测试覆盖写锁冲突、孤儿临时文件、取消边界、阶段写入失败/异常/CAS 冲突、启动返回异常、回退前二次交接、严格时间戳与检查点/公开结果信息最小化；
- 切换事务、内存/持久化协调器、恢复执行器和真实进程适配器都没有被 `BridgeClient`、`MainForm`、`Program`、Host DI、启动配置或发布脚本注册；当前 `active` 所有权仍被硬性拒绝，Host 不连接真实飞书、不启动 CLI、不写生产 Store，Node 仍是唯一 Active Owner。

被动 Host 的本地无外部副作用验证：

```powershell
dotnet test .\bridge-dotnet\tests\AiCliFeishu.Bridge.Host.Tests\AiCliFeishu.Bridge.Host.Tests.csproj -c Release
```

Host 子系统的初始化顺序固定为 `PassiveOwnerGuard → Boundary validation → ReadOnlyNodeStoreShadow → BridgeBusinessStateOwner → Feishu Event Pump → OpenCode Event Pump`，停止时按相反顺序执行。Node 和 C# 回收死亡 PID 的有效租约时，都会按旧 `leaseId` 原子改名并保留确定性墓碑；并发启动者因此不能把刚建立的新租约误当成旧租约移动。控制面板的纯切换事务、内存/持久化隔离协调器、真实进程适配器、正式只读 Store/租约检查器、保守恢复计划、未接入入口的耐崩溃检查点存储、只读恢复观察器、隔离检查点写者协议和隔离恢复执行器已经固定阶段、提交点、采样顺序、身份边界、失败回退、刷盘和冲突证据语义，检查点文件版本 CAS、写入者崩溃后的孤儿临时文件隔离、启动 PID 返回前的持久化绑定、恢复后的端点/租约稳定收敛，以及对应真实子进程演练也已完成，但仍没有生产执行入口。生产装配审查已建立明确的 Passive/Active 分支和静态完整性预检，并完成 Active Owner 租约这一项生产能力；后续必须继续按能力清单逐个实现并登记其余 14 项真实生产所有者，在 15 项能力全部满足且全链路审查通过前，不得把切换能力接入 `BridgeClient`、`MainForm`、`Program`、启动配置或发布脚本，也不得解除 `active` 闸门。任何时刻仍只允许一个生产写入者。

### M6：删除旧实现

- 删除 Node 入口、TypeScript 业务代码和 npm 发布步骤；
- 保留必要的协议样例和迁移记录；
- 更新 CI、安装脚本和文档。

验收：生产和 CI 均不调用 Node；仓库只保留 C# 生产实现；旧数据完成备份和兼容验证。

## 11. 明确不做

- 不做一次性“大爆炸”重写；
- 不把所有职责塞进 WinForms；
- 不在迁移中同时更换数据库；
- 不拆远程微服务；
- 不允许 Node 与 C# 双写或重复发送飞书消息；
- 不为了表面统一而删除历史会话恢复和版本化终端宿主。

## 12. 最终完成定义

- 生产包完全不依赖 Node.js 和 npm；
- Bridge Core 不包含 Codex、Claude Code、OpenCode 或飞书协议特例；
- 新增 CLI 主要只需新增 Adapter、运行时清单项和契约测试；
- UI、后台 Host 和 TerminalHost 生命周期互相隔离；
- 任一次消息、审批或问答故障能由 `traceId` 定位到明确层级；
- 同一业务语义在所有 CLI 上共享核心状态机和验收测试。
