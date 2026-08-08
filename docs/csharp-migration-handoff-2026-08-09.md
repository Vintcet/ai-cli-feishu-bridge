# 纯 C# 迁移交接（2026-08-09）

## 1. 当前结论

- 上一份交接：`docs/csharp-migration-handoff-2026-08-08.md`。
- 分支：`main`，默认不推送。
- 已完成显式生产 Node → C# 命令入口、MainForm 控件和真实桌面发布包隔离演练。
- 隔离环境中的成功切换、提交后 C# 恢复、耐久写入失败及 Node 回滚恢复均已通过。
- 真实生产切换尚未执行；下一步是需要用户单独明确授权的生产状态变更，不应自动继续。

本轮关键提交：

- `44ee893 feat: add explicit production dotnet cutover command`
- `bf93795 feat: add desktop production cutover control`
- 本文所在纵切片：`test: verify isolated production host cutover`

## 2. 必须继续遵守的约束

1. 全程中文沟通。
2. 每个纵切片独立提交。
3. 默认不推送。
4. 不静默切换，也不在失败后猜测回退。
5. 任意时刻只能有一个生产 Active Owner。
6. 停止 Bridge Host 前必须通过认证管理 API 精确核对 Host kind、管理 API 版本、PID、ownership、Active Owner 和实例名。
7. 真实切换及观察期完成前，不删除 Node、`dist`、npm 脚本或现有回退能力。

## 3. 本轮完成内容

### 3.1 显式命令入口

- 新增 `--bridge-cutover-to-dotnet`。
- 交互运行要求人工确认；自动化必须显式携带 `--confirm-production-cutover`。
- 切换前运行启动恢复，随后认证当前 Node Production 的完整身份。
- 预检 Node 回退入口与 C# sidecar，只调用同一持久化生产切换服务，不在命令入口复制启停或检查点逻辑。
- `Completed` 和 `RolledBack` 才刷新持久化目标；其他不确定结果固定脱敏并锁住后续所有权操作。

### 3.2 MainForm 控件

- 新增“切换到 C#”按钮。
- 仅当持久化目标为 Node Production、Node 已认证在线且没有恢复/切换阻塞操作时可用。
- UI 使用与命令入口相同的风险说明、确认流程和持久化服务。

### 3.3 真实发布包隔离演练

新增 `scripts/verify-production-cutover-release.ps1`：

- 先运行 `npm run build`，确保被忽略的 `dist/` 含当前 Node Production 身份契约。
- 发布真实桌面单文件包，并验证同目录包含：
  - `AiCliFeishuControl.exe`
  - `AiCliFeishuTerminalHost.exe`
  - `AiCliFeishuBridgeHost.exe`
- 使用假飞书凭据、随机回环端口、临时 Store、禁用 OpenCode 自动发现和回环拒绝代理。
- 从真实发布桌面命令入口完成 Node → C#，验证 Node 精确认证后退出、唯一 `production-dotnet` 租约、C# `/health` 与 `/control/status`、Store loaded、`Completed` 检查点及一致的 `--cutover-operation`。
- 停止 C# 后执行 `--bridge-service`，确认只重启 C#，且不改写已提交检查点。
- 在 `NodeStopRequested` 阶段锁住检查点模拟耐久提交失败，确认入口安全失败、没有 Owner 在线；随后由 `--bridge-service` 启动新 Node 并把检查点 CAS 收敛到 `RolledBack`。
- 不连接真实飞书、不启动真实 CLI、不读取或写入仓库生产 `data/`；临时 Host 与目录全部清理。

## 4. 最终验证结果

- Bridge 六个 C# 测试项目：`565/565`。
  - Core：`40/40`
  - Storage：`10/10`
  - Runtime Adapters：`55/55`
  - Feishu Adapter：`80/80`
  - Replay：`4/4`
  - Host：`376/376`
- 桌面测试：`273/273`。
- Node 测试：`223/223`。
- 桌面 Release 构建：0 warning / 0 error。
- `npm run check`：通过，格式检查 `369` 个文件。
- `scripts/verify-production-cutover-release.ps1`：成功切换、提交后 C# 恢复、耐久失败和 Node 回滚恢复两条路径全部通过。

## 5. 当前生产状态

2026-08-09 只读核对结果：

- `data/bridge-host-cutover.checkpoint.json`：不存在。
- `data/bridge-active-owner.lock`：不存在。
- `data/owner.json`：不存在。
- `data/control-token.json`：存在。
- `.env` 未显式设置端口，按默认 `8765` 检查认证 `/health`：不可用。
- 因此持久化生产目标仍为 Node，但当前未观察到在线、已认证的 Active Owner。
- 仓库根目录仍是 2026-08-05 的旧桌面/Terminal Host 发布文件；根目录没有 `AiCliFeishuBridgeHost.exe`，尚未部署包含新入口的三件套。
- 本轮没有建立生产检查点，没有替换根目录发布包，也没有停止任何无法认证的生产进程。

## 6. 下一步：真实生产切换授权闸门

只有用户明确确认后，才能进入真实生产切换。该操作会改变生产状态，至少包括：

1. 备份现有 `data/`，保留原格式和可恢复副本。
2. 发布并部署新的桌面、Terminal Host、C# Bridge Host 三件套，同时保留旧发布包和 Node 回退路径。
3. 确认没有第二个桌面助手、后台切换器或未知 Active Owner。
4. 启动并认证 Node Production；当前端点离线，不能从离线/未知身份直接开始切换事务。
5. 执行显式切换，验证 `Completed` 检查点、唯一 C# `production-dotnet` 租约和认证健康状态。
6. 做最小真实冒烟：飞书连接、普通提示、审批/问答、Managed Terminal、OpenCode、设置和会话目录。
7. 停止并重启 C#，验证提交后恢复只恢复 C#。
8. 关键项失败时只使用耐久回退链，不手工同时启动 Node 和 C#。

## 7. 已知注意点

- 终态检查点对应的 Owner 离线时，新桌面程序启动会按恢复计划重启该 Owner；这会影响“主动停止后重新打开桌面”的体验。生产发布前应确认这是期望行为，或另做“保持停止”的持久化状态纵切片。
- MainForm 的人工接管阻塞在当前桌面进程内不会自动重试；人工处理后需要退出并重新打开。
- 根目录旧 EXE 与新代码版本号同为 `0.19.1.0`，不能只按文件版本区分；部署时应使用本次源代码重新发布的完整三件套并保留备份。
- 真实切换成功后仍应保留 Node 回退观察期，稳定后再讨论 M6 删除旧实现。

## 8. 粗略进度

- C# 功能迁移：约 `96%`。
- 切换基础设施、显式入口与耐崩溃恢复：约 `99%`。
- 含真实生产切换、冒烟、观察期和最终删除 Node 的整体迁移：约 `94%`。

当前剩余主线已从“实现入口”转为“经明确授权执行真实生产切换并观察”，不是继续大规模重写业务功能。
