# 纯 C# 迁移交接（2026-08-08）

> 续接结果与当前生产切换闸门见 `docs/csharp-migration-handoff-2026-08-09.md`。

## 1. 续接入口

- 仓库：`K:\AI\codex+\codex-feishu-bridge`
- 分支：`main`
- 功能代码基线：`60aec7c refactor: recover production host on startup`
- 历史会话：`019fd887-dc2b-7ab0-b204-5848f8683f75`
- 下一窗口应从“显式生产 Node → C# 切换入口”继续。
- 本文提交后本地预计领先 `origin/main` 11 个提交；本轮未推送。

建议新窗口首句：

> 继续 `K:\AI\codex+\codex-feishu-bridge` 的纯 C# 迁移。先读 `docs/csharp-migration-handoff-2026-08-08.md`，从 `60aec7c` 后面的“显式生产切换入口”开始。每个纵切片独立提交，不推送，中文回复。

## 2. 必须继续遵守的约束

1. 全程中文沟通。
2. 目标是纯 C#；Node 只作为尚未完成真实切换前的现有实现和回退路径。
3. 每个纵切片独立提交。
4. 默认不推送；只有用户明确要求时才 `git push`。
5. 不允许静默切换或启动失败后猜测回退。
6. 任意时刻只能有一个生产 Active Owner。
7. 停止任何进程前必须通过认证管理 API 核对 Host kind、管理 API 版本、PID、ownership 和实例名；不要按进程名或裸 PID 停止。
8. 不在真实切换前删除 Node、`dist`、npm 脚本或回退能力。

## 3. 本窗口完成的三个纵切片

### 3.1 `842a9e8 refactor: compose production host cutover`

- 新增 `BridgeHostProductionCutover` 生产组合根。
- 固定生产端点为 `http://127.0.0.1:{port}/`。
- 固定 C# Active 实例为 `production-dotnet`。
- 严格只读加载 `data/control-token.json`：恰好 64 位十六进制；缺失、畸形、过长、重复 token 字段或重解析点均 fail closed，错误不回显令牌。
- 组合正式 `ProductionBridgeStoreHandoffInspector`、持久化协调器、恢复观察器、恢复执行器及 Node/C# 真实进程工厂。
- 构造阶段不探测网络、不启动/停止进程、不写检查点、不取得租约。
- `BridgeHostTarget` 新增 `DotNetProduction`，但环境变量仍只接受 `node` / `dotnet-shadow`；`dotnet` 继续被拒绝。

### 3.2 `ee3cf6d refactor: refresh persistent production host target`

- 新增 `BridgeHostProductionTargetSelector`。
- 当前生产目标由耐久检查点终态决定：
  - 检查点缺失或 `RolledBack`：Node Production；
  - 只有绑定 `production` / `production-dotnet` 的 `Completed`：C# Production；
  - 非终态、损坏、不可读、读取变化或孤儿临时文件：拒绝猜测，要求恢复/人工接管。
- `BridgeClient` 可刷新目标，刷新失败不会覆盖最后可信的内存目标。
- 普通“连接”流程禁止直接启动 C# Active，必须走持久化切换或恢复执行器。

### 3.3 `60aec7c refactor: recover production host on startup`

- 新增 `BridgeHostStartupRecovery`。
- 桌面窗口、`--bridge-start` 和 `--bridge-service` 启动前先检查恢复状态。
- 无检查点时只刷新 Node 目标，不启动或停止进程。
- 有检查点时调用现有恢复执行器，按稳定端点、Active Owner 租约、Store 交接和检查点 CAS 证据收敛。
- 若非终态检查点下原 Node 已被认证且与 live 租约一致，不碰进程，仅把检查点 CAS 收敛到 `RolledBack`，使持久化目标重新明确。
- Busy、检查点孤儿文件、损坏/变化、Store 交接不安全、身份不一致或执行失败时，桌面锁住连接、断开、Runtime 启动和设置操作，只显示脱敏的人工接管提示。
- 对外 `BridgeHostRecoveryInspection` 仍只包含粗粒度计划和文件版本；执行器所需的已认证身份放在内部执行证据类型中，不扩大 UI 信息面。

## 4. 验证结果

当前功能基线已通过：

- 桌面测试：`248/248`。
- 其中恢复观察器/执行器/启动恢复相关筛选测试：`46/46`。
- 桌面 Release 构建：0 warning / 0 error。
- `npm run check`：通过，格式检查 362 个文件。

此前已通过但本窗口未重新全量执行的基线：

- Bridge 六个 C# 测试项目合计：`565/565`。
- C# Host 测试：`376/376`。
- Node 测试：`223/223`。
- 真实发布单文件 C# Active Host 的隔离授权启停演练已通过。

建议下一窗口在新增切换入口后至少重新执行：

```powershell
dotnet test desktop-control\tests\AiCliFeishuTerminalHost.Tests.csproj -c Release --nologo
dotnet build desktop-control\AiCliFeishuControl.csproj -c Release --nologo
npm run check
```

真实切换前还应重新执行全部 C# 项目测试与 Node 测试。

## 5. 当前仓库和生产状态

在 2026-08-08 交接时：

- 工作树在功能提交后为干净状态；交接文档将单独提交。
- `main` 在交接文档提交前领先 `origin/main` 10 个提交。
- `data/bridge-host-cutover.checkpoint.json`：不存在。
- `data/owner.json`：不存在。
- `data/control-token.json`：存在。
- 认证 `/health`：不可连接，生产桥接当前离线。
- 桌面程序和 C# Host 未运行。
- 系统里看到一个 `node.exe`（当时 PID 43920），但无法通过 Bridge 健康端点认证，不能把它视为生产 Owner，也绝不能仅按该 PID/进程名停止。
- 仓库根目录没有 `AiCliFeishuBridgeHost.exe`。
- 开发构建产物 `bridge-dotnet/src/AiCliFeishu.Bridge.Host/bin/Release/net8.0/AiCliFeishuBridgeHost.exe` 存在。
- 根目录现有 `AI CLI飞书助手.exe` 是旧发布程序，不包含本窗口新增入口；真实演练前必须发布并使用新桌面包。

结论：尚未发生真实 Node → C# 生产切换；当前也没有生产 Active Owner 或切换检查点。

## 6. 下一纵切片：显式生产切换入口

推荐目标：新增一个明确、可确认、不可静默触发的 Node → C# 切换命令/界面入口，并独立提交。

建议实现顺序：

1. 在 `BridgeClient` 或独立桌面服务中新增显式 `CutoverProductionHostAsync`。
2. 切换前调用认证状态 API，构造完整 `BridgeCutoverHostIdentity`；必须验证：
   - `hostKind == node`；
   - `managementApiVersion == 1`；
   - `ownershipMode == active`；
   - `activeOwner == true`；
   - `instanceName == production`；
   - PID 大于 0。
3. 确认无启动恢复阻塞状态；检查点必须缺失，或为允许新 operation 的安全终态。
4. 明确向用户说明：将停止 Node、核对 Store 刷盘与租约释放、启动 C# Active；失败时只按持久化证据回退。
5. 使用 `BridgeHostProductionCutover.CutoverAsync(expectedNode)`，不要复制协调逻辑。
6. 映射 `Completed`、`RolledBack`、`FailedSafe`、`Busy`、`CheckpointRecoveryRequired`、`CheckpointConflict`、`Unavailable`、`Cancelled` 为固定脱敏提示。
7. 只有 `Completed` 后才刷新 `BridgeClient` 到 C# Production；`RolledBack` 刷新回 Node；其他状态锁住所有权操作并要求人工接管。
8. 不要在按钮逻辑里直接 `Process.Start` C#，也不要直接写检查点。

入口形式可先做命令行 `--bridge-cutover-to-dotnet`，再接 MainForm 按钮；若拆成两个纵切片，应分别提交。

## 7. 显式入口后的必做演练

1. 发布新的桌面单文件包，确认发布目录同时包含：
   - 新桌面 EXE；
   - `AiCliFeishuTerminalHost.exe`；
   - `AiCliFeishuBridgeHost.exe`。
2. 在随机端口、临时数据目录、假飞书凭据和回环拒绝代理下，从真实桌面入口执行完整切换。
3. 验证：
   - Node 身份被精确认证后才停止；
   - Node 退出且端口离线；
   - `owner.json` 释放；
   - Store 兼容且已刷盘；
   - C# 启动参数含同一 `--cutover-operation`；
   - C# 获得唯一 Active 租约；
   - `/health` 与 `/control/status` 返回 C# Production 身份；
   - `Completed` 检查点耐久发布；
   - C# 退出后，启动恢复只重启 C#；
   - 模拟提交失败时恢复器回退 Node 并收敛到 `RolledBack`。
4. 演练不得连接真实飞书、不得启动真实 CLI、不得触碰生产 Store。

## 8. 真实生产切换清单

隔离演练全部通过后再做：

1. 备份 `data/`，但不要修改原文件格式。
2. 确认新发布桌面程序和 C# sidecar 可用。
3. 先启动并认证现有 Node Production；当前交接时桥接是离线的，切换事务不能从离线状态直接开始。
4. 确认没有第二个桌面助手、后台切换器或未知 Active Owner。
5. 执行显式切换。
6. 验证检查点为 `Completed`、`owner.json` 为 C# `production-dotnet`、健康端点和飞书链路正常。
7. 做最小真实冒烟：飞书连接、普通提示、审批/问答、Managed Terminal、OpenCode、设置与会话目录。
8. 测试停止/重启后的 C# 恢复。
9. 若任一关键项失败，使用现有持久化回退链；不要手工同时启动 Node 和 C#。
10. 稳定观察后再讨论删除 Node。

## 9. 已知注意点

- 当前 MainForm 的人工接管阻塞在本次进程内不会自动重试；人工处理后需退出并重新打开程序。
- 只要存在终态检查点且 Owner 离线，新桌面程序启动时会按恢复计划重启该终态 Owner；这会影响“用户主动断开后再次打开程序”的 UX，真实发布前应确认这是期望行为，或另行增加明确的“保持停止”持久状态。
- `--bridge-stop` 不会先执行恢复，避免为了停止而先重启 Owner。
- 当前根目录旧桌面 EXE 尚未替换，不要误以为源代码接入已经进入生产。
- 真实切换前不要删除 Node；真实切换后也应先保留回退观察期。

## 10. 粗略进度

- C# 功能迁移：约 `93%`。
- 切换基础设施与耐崩溃恢复：约 `96%`。
- 含显式入口、隔离桌面演练、真实切换、观察期和最终删除 Node 的整体迁移：约 `90%`。

主要剩余工作是显式切换入口、真实入口隔离演练、生产切换与稳定观察，而不是大规模业务功能重写。
