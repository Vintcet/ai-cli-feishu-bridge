# Codex / Claude Code / OpenCode Feishu Bridge

[简体中文](README.md) | [English](README_EN.md)

Current version: `0.18.2`

This is an unofficial Windows-local bridge that connects Codex CLI, Claude Code, and OpenCode sessions to your own Feishu custom app. Each assistant session can have a private Feishu group, so you can receive completion and error notifications, handle approval or follow-up prompts, and continue the original CLI conversation while away from the computer.

The bridge service, session index, credentials, and settings stay on your computer. This project does not provide a cloud relay and does not bundle Codex CLI, Claude Code, OpenCode, or a Feishu app. You install and sign in to the CLI tools yourself and create your own Feishu custom app.

## What's new in 0.18.2

- The desktop Refresh button now waits for a fresh process scan, so externally closed Codex and Claude Code windows leave the active list promptly.
- Externally resumed sessions enter History after closing; continuing them from History relaunches them as managed sessions with two-way Feishu control.
- History no longer shows a model column that can become stale across resumes, and same-project Feishu groups receive stable suffixes such as `Codex｜project` and `Codex｜project（2）`.

## What's new in 0.18.1

- Long-running state now has bounded retention for sessions, approvals, message routes, and inbound deduplication records without evicting pending approvals.
- Codex transcript monitoring backs off inactive sessions and safely handles file replacement, partial UTF-8 content, and the final scan during shutdown.
- The Windows build baseline now uses Node.js 24, and path assertions accept equivalent long and 8.3 short-path representations.

## What's new in 0.18.0

- All local HTTP control paths now use the persistent control token; anonymous health checks no longer expose pairing or session details, and cross-site or non-JSON writes are rejected.
- Codex, Claude Code, and OpenCode share one low-risk automatic-approval policy, while high-risk operations still require a Feishu or local decision.
- Aliases can be set, changed, or cleared directly from History without changing the session ID, Feishu group binding, or resume directory.
- Temporary 400/408/409/429/5xx, busy-service, and timeout failures share one automatic-retry policy; each error card can stop the remaining attempts, and Codex transcript monitoring covers failures that skip the `Stop` hook.
- Long-running reliability fixes cover reused process IDs, cross-device approval state, attachment quotas, approval-log rotation, storage recovery, and OpenCode reconnect behavior.
- The desktop executable manages Node.js directly and performs authenticated graceful shutdown. X collapses to the tray, minimize stays in the taskbar, and tray activation restores the window to the foreground.

## Runtime support and features

All three runtimes share the same session, routing, card, approval, and status management. Only the local transport to the CLI differs:

| Runtime | Local integration | Full two-way sync |
| --- | --- | --- |
| Codex CLI | Hooks + managed terminal | Supported when launched by the desktop control panel |
| Claude Code | Hooks + managed terminal | Supported when launched by the desktop control panel |
| OpenCode | Local HTTP + SSE | Supported when OpenCode is started with a local port |

Main features:

- Send Feishu notifications when a turn completes, fails, or needs more information.
- Show Codex, Claude Code, and OpenCode approval requests as interactive Feishu cards.
- Approve or reject in Feishu first, with an explicit action to transfer the request to the PC approval window.
- Interrupt a running managed session from Feishu, or explicitly queue a message for the next turn.
- Answer Codex `request_user_input`, Claude Code `AskUserQuestion`, and OpenCode questions from Feishu.
- Optionally show a live progress card and mirror prompts entered on the computer.
- Split long prompts, final responses, and errors across multiple cards instead of truncating them, and convert Markdown tables into native Feishu tables.
- Detect temporary 400/408/409/429/5xx, high-demand, busy-service, timeout, and similar errors, use one consecutive-failure retry policy for Codex, Claude Code, and OpenCode, and include failures that skip the Codex `Stop` hook.
- Receive Feishu images/files into the project bridge directory and explicitly send generated project files back to Feishu.
- Keep real CLI sessions in local history and resume them in their original working directory; reopening a previously hidden session returns it to the normal history lifecycle.
- Automatically reopen a closed Codex, Claude Code, or OpenCode conversation when a message arrives in its Feishu session group, as long as the desktop control panel is still running in the tray.
- Automatically disband bridge-created session groups after seven days without session activity; the CLI conversation and project files are not deleted.
- Recover the original working directory when a known `resume`, `--resume`, or `--session` ID is entered manually.
- Create a session from a bot DM with `新建 codex <project>`, `新建 claude <project>`, or `新建 opencode <project>`; the project folder is found or created under the default workspace.
- Route multiple simultaneous windows strictly by session alias or session ID.
- Treat externally launched Codex/Claude Code sessions as notification-only, preventing Feishu and a local terminal from advancing the same conversation independently.

## Requirements

- Windows 10/11 x64.
- Git only when cloning the source or contributing; it is not required for a downloaded Release ZIP.
- Node.js 20 or later.
- PowerShell 7, available as `pwsh.exe`.
- .NET 8 Windows Desktop Runtime; building the desktop app from source requires the .NET 8 SDK.
- At least one of Codex CLI, Claude Code, or OpenCode installed and signed in.
- A Feishu custom app whose bot capability, permissions, events, availability, and published version you can manage.
- Windows Terminal is recommended; the launcher falls back to a normal console when it is unavailable.

## Get the desktop app and run it for the first time

### Use the Release ZIP

Windows users can download `codex-feishu-bridge-v0.18.2-windows-x64.zip` from [GitHub Releases](https://github.com/Vintcet/codex-feishu-bridge/releases). The archive includes the compiled bridge, production dependencies, and both desktop executables. Extract the complete archive instead of copying only the main executable. Before the first run, copy `.env.example` to `.env`, add your Feishu app settings, then launch `Codex飞书助手.exe` from the archive root.

### Build from source

To build from source instead, run:

```powershell
git clone https://github.com/Vintcet/codex-feishu-bridge.git
cd .\codex-feishu-bridge
npm install
Copy-Item .\.env.example .\.env
npm run build
dotnet publish .\desktop-control\CodexFeishuControl.csproj -c Release -o .\desktop-control\publish
```

Edit `.env`, then run the source-built application:

```powershell
.\desktop-control\publish\CodexFeishuControl.exe
```

`CodexFeishuControl.exe` is the desktop panel produced by a source build. Keep `CodexFeishuTerminalHost.exe` in the same directory. A packaged build may display the main executable as `Codex飞书助手.exe`; it is the same application. Create a desktop shortcut to whichever main executable you actually use.

## Create and configure the Feishu bot

You need a Feishu **custom app for your organization**, not an incoming-webhook “custom bot” added inside a group. The bridge uses the app's App ID and App Secret to open a WebSocket connection from your computer, so it does not require a public server, callback domain, or tunneling service.

### 1. Create a custom app and enable the bot capability

1. Open the [Feishu Open Platform developer console](https://open.feishu.cn/app) and sign in to the organization where you want to use the bot.
2. Choose “Create custom app” (企业自建应用), then set its name, description, and icon.
3. Open the app and add the “Bot” capability under application capabilities. If the console layout changes, see Feishu's [official bot-capability guide](https://open.feishu.cn/document/uAjLw4CM/ugTN1YjL4UTN24CO1UjN/trouble-shooting/how-to-enable-bot-ability).
4. Copy the **App ID** and **App Secret** from Credentials & Basic Information and put them in `.env` in the project directory:

```env
FEISHU_APP_ID=cli_xxxxx
FEISHU_APP_SECRET=xxxxx
```

The App Secret is an application password. Do not post it in chat, expose it in screenshots, or commit it to Git. `.env` is ignored by this repository; also exclude your private `.env` when distributing a build.

### 2. Add app-identity permissions

Open Permission Management and search by the exact scope IDs below. “Required” means required for the recommended one-private-group-per-session workflow. The final two scopes may be omitted only if you do not need any attachment input or output.

| Scope ID | Feishu console permission | Used for | Requirement |
| --- | --- | --- | --- |
| `im:message:send_as_bot` | Send messages as the app | Notifications, replies, approval cards, and card status updates | Required |
| `im:message.p2p_msg:readonly` | Read messages sent to the bot in a direct chat | Binding and management commands | Required |
| `im:message.group_msg` | Read all messages in a group | Receive ordinary session-group messages without requiring `@bot` | Required; sensitive scope |
| `im:chat:create` | Create groups | Create a private group for each assistant session | Required |
| `im:chat:update` | Update group information | Rename a group after a session alias changes | Required |
| `im:chat:delete` | Disband groups | Remove long-inactive session groups | Required |
| `im:chat:operate_as_owner` | Update groups created by the app | Let the bot manage and disband groups it created | Required |
| `im:message:readonly` | Read direct-chat and group messages | Download images and files attached to received messages | Required for attachments |
| `im:resource` | Get and upload image/file resources | Upload images or generated files back to Feishu | Required for attachments |

`im:message.group_msg` is a sensitive permission and may require approval by a Feishu administrator. `im:chat:update` and `im:chat:delete` authorize the API operations; `im:chat:operate_as_owner` authorizes a bot that created the group to manage that group. Configure both kinds of scopes—they do not replace one another.

### 3. Configure events and card callbacks

Open Events & Callbacks (some console versions call it Event Subscriptions), select **Receive events through a long connection**, and add:

| Type | Console name | Identifier | Used for |
| --- | --- | --- | --- |
| Event | Receive message v2.0 | `im.message.receive_v1` | Direct messages, group messages, images, and files |
| Callback | Card action callback | `card.action.trigger` | Approval buttons and follow-up answer choices |

When you save long-connection mode for the first time, the Feishu console normally checks that a client is already online. After putting the App ID and App Secret in `.env`, start the packaged `Codex飞书助手.exe` or the source-build output `desktop-control\publish\CodexFeishuControl.exe`, click `连接` (Connect), then return to the developer console and save long-connection mode. You do not need to bind a Feishu account or launch a CLI session yet. After permissions, events, and the app version have been published, disconnect and reconnect once from the desktop panel.

If your console shows events and callbacks on separate tabs, add one item on each tab. The local bridge initiates the long connection to Feishu; do not switch this project to webhook mode or enter a public request URL. See Feishu's [long-connection guide](https://open.feishu.cn/document/uAjLw4CM/ukTMukTMukTM/event-subscription-guide/long-connection-mode) for the platform-level behavior and limits.

### 4. Set availability and publish a version

1. Create a new app version under Version Management & Release.
2. Set the app's [availability](https://open.feishu.cn/document/home/introduction-to-scope-and-authorization/availability) to include at least the Feishu account that will bind to the bridge. Otherwise the bot may not be searchable and may be unable to add that user to a session group.
3. Submit and publish the version. If your organization requires administrator review, wait for approval.
4. Confirm that the latest version is shown as published before connecting the desktop app.

This is the most common setup mistake: **saving permission or event changes as a draft does not activate them**. Whenever you add permissions, change events/callbacks, or adjust availability, create and publish another app version, then disconnect and reconnect from the desktop control panel.

### 5. Connect, bind, and verify

The current desktop UI is Chinese. This guide shows the exact Chinese label first and an English explanation when useful.

1. Run the packaged `Codex飞书助手.exe` or the source-build output `desktop-control\publish\CodexFeishuControl.exe`, click `连接` (Connect), and wait for the Feishu status to show `已连接` (Connected).
2. Find the published bot in Feishu and open a direct chat with it.
3. Send the binding command displayed by the desktop panel. The first command is usually `绑定 <random-code>`. Once bound, only that Feishu account can manage this local bridge.
4. Launch a Codex, Claude Code, or OpenCode session from the desktop panel and wait for it to appear under `活跃会话` (Active Sessions).
5. Select the session and click `创建飞书群` (Create Feishu Group). Newly managed sessions also attempt this automatically.
6. Open the `Codex｜project`, `Claude｜project`, or `OpenCode｜project` group and send ordinary text. You do not need to mention the bot.
7. Test an approval card. If you need attachments, also send an image or file and verify download and file return.

The two Feishu entry points have different roles:

| Entry point | Purpose |
| --- | --- |
| Direct chat with the bot | Binding, status, workspace, new-session commands, session list, aliases, and help |
| Per-session private group | Continue the one CLI conversation bound to that group; ordinary text is forwarded unchanged |

If group creation fails, the local CLI still works and notifications temporarily fall back to the bot DM. Use the exact error shown in the desktop panel's Feishu Group column, add and publish the missing permission, then select the session and retry Create Feishu Group. You do not need to restart the CLI.

## Quick start: one assistant conversation per Feishu group

The recommended workflow is one private Feishu group for every managed Codex, Claude Code, or OpenCode window. Only you and the bot are in the group, and messages from that group can enter only its bound assistant session.

1. Complete the Feishu bot, permission, event/callback, availability, and publishing steps above.
2. Run the desktop executable you built/downloaded, or a `Codex飞书助手` shortcut you created for it. Click `连接` (Connect) and wait for Feishu to show as connected.
3. Send the displayed binding command to the bot in a direct chat.
4. Confirm the default workspace in `设置` (Settings). Use `新建 Codex`, `新建 Claude`, or `新建 opencode` to select a directory, send a `新建 ...` command in the bot DM, or resume a saved conversation from `历史记录` (History).
5. Select the target under `活跃会话` (Active Sessions) and click `创建飞书群` if a group was not created automatically.
6. Enter that session group and send ordinary text without `@bot`, an alias, or a short ID.

After a message is submitted, the bot returns a short acknowledgement such as `Codex 已接收。` (“Codex received it”). Completion notifications do not require quoted replies; send the next message directly in the same group.

If the CLI window has closed, the message is not discarded. As long as the desktop control panel is still running—even hidden in the system tray—it reopens the saved runtime in the saved directory and administrator mode, waits for the session to register, and then submits the original message. If the panel has been fully exited from the tray, the bridge reports a timeout instead of silently losing the message.

Bridge-created groups are disbanded after seven days without session activity by default. This removes only the Feishu group, not the CLI conversation, project directory, or local history. Resuming the session later creates a new group when needed.

| Location | Where ordinary text goes |
| --- | --- |
| `Codex｜project`, `Claude｜project`, or `OpenCode｜project` group | Only the assistant session bound to that group |
| Direct chat with the bot | Management commands, or a target selected with `@alias message` / `#short-id message` |
| Externally launched Codex/Claude Code | Notification-only; ordinary Feishu input is rejected |

Before use, confirm that the bot capability is enabled, all required scopes are present in a published version, `im.message.receive_v1` and `card.action.trigger` are configured for the long connection, your account is in the app's availability range, and the desktop panel has reconnected after the latest publication.

## Windows desktop control panel

For everyday use, no command line is required. The current panel UI is Chinese; key labels are translated below. Run whichever main executable exists in your copy:

```text
Packaged build: .\Codex飞书助手.exe
Source build:   .\desktop-control\publish\CodexFeishuControl.exe
```

If you want a desktop entry, manually create a shortcut named `Codex飞书助手` to the executable you use. The panel provides:

- `连接` / `断开` (Connect/Disconnect) using one status-aware button.
- `新建 Codex`, `新建 Claude`, and `新建 opencode` launchers with a project-directory picker.
- Optional per-window Windows administrator launch; UAC must still be confirmed on the computer.
- Real bridge-process and Feishu long-connection status.
- Bound-account count.
- Active session details including runtime, alias, project, short ID, status, queue length, model, source, working directory, open time, and last activity.
- History containing offline Codex/Claude Code sessions previously launched by the panel or identified by hooks, plus the current conversations of OpenCode instances connected through a local port. Continuing an external session from History relaunches it as managed.
- One-click or double-click resume in the original directory and administrator mode.
- Automatic recovery requests claimed when a message is sent to a closed session's Feishu group.
- Alias management from either Active Sessions or History without changing the original session or Feishu group binding, plus Open Directory for the selected session.
- Settings for the default workspace, routine progress messages, mirroring local prompts, temporary-error retries, automatic approval, and optional automatic-approval audit cards.

Clicking the window's X button uses a native collapse animation and hides the panel in the bottom-right system tray; ordinary minimize remains in the taskbar. Double-clicking the tray icon restores the window directly to the foreground, while only Exit from the tray menu terminates the panel process. Starting the EXE again also brings the existing panel forward. Exiting the panel still does not automatically stop an already running background bridge service.

Temporary-error retry handles recognizable 400/408/409/429/5xx, high-demand, busy-service, and timeout failures that are safe to replay. Configure 1–20 retries per consecutive failure batch, a 1–600 second base delay, and 0–120 seconds of random jitter. Codex, Claude Code, and OpenCode ask the same session to retry its previous task. Retry error cards include **Stop automatic retry**; if an attempt has already started, the action stops every later attempt in that batch. Any successful completion immediately resets the batch, so a later failure starts again at attempt 1 even when it happens moments later. Automatic retry writes only to a live assistant-managed window or a connected OpenCode session. Externally launched Codex/Claude Code sessions receive the error notification but are never injected with a retry and never resumed in a second background process. Codex sampling failures may write `task_complete.error` without running the `Stop` hook, so the bridge polls each active transcript from its current end and processes only newly appended errors.

Low-risk automatic approval uses the same risk rules for Codex, Claude Code, and OpenCode and is disabled by default. Only explicitly recognized tools whose complete input can be inspected are eligible. Unknown tools, oversized or incomplete input, deletion, destructive Git operations, dependency installation or publishing, network or cloud actions, permission or system changes, sensitive paths, and paths outside the project remain manual. Manual requests appear in Feishu first; the PC dialog opens only after **Transfer to PC approval** is selected, or automatically when Feishu delivery is unavailable. Successful low-risk approvals are silent by default; enable the separate audit-card setting to send one resolved information card without action buttons. Automatic processing never silently allows an uncertain operation.

## Launch a synchronized Codex window

1. Run the desktop control panel and connect Feishu.
2. Click `新建 Codex` and choose a project directory.
3. Optionally enter Codex arguments such as `resume <session-id>` or paste a complete `codex resume ...` command. If the session ID is known locally, the directory automatically changes to the saved working directory.
4. Enable Windows administrator mode only when the project needs it.
5. Work normally in the visible Windows Terminal/Codex window.

Every launch creates an independent terminal. The managed host uses a local named pipe restricted to the current Windows user, so Feishu input is written into the same visible Codex window. Administrator elevation affects only that window and its children.

When a window closes, its session moves from Active Sessions to History. Resume reuses the saved directory and administrator mode and safely passes `resume <session-id>`. Codex/Claude Code sessions resumed with arguments in an external terminal also enter History after closing, but ordinary Feishu text remains disabled while the external window is online. Continuing one from History relaunches it as a managed window with two-way Feishu control. Online sessions do not also appear in History. If Delete History was used, that ended record stays hidden until the conversation is reopened. Reopening clears the hidden marker, so the next close returns it to History; bridge startup also repairs legacy records that are active but still hidden.

The argument field is parsed only as Codex CLI arguments; it is never executed as a PowerShell command. A manually launched external Codex session can still be discovered by hooks for notifications and structured approvals, but ordinary Feishu text is not injected and no temporary second process is started.

## Launch a synchronized Claude Code window

Claude Code uses the same managed-window and Feishu-routing design:

1. Confirm that `claude --version` works.
2. Click `新建 Claude` and select the project directory.
3. To resume, enter `--resume <session-id>` or paste a complete `claude --resume ...` command.
4. Optionally select administrator mode and work in the visible terminal.

The panel first searches for `claude.exe` / `claude.cmd` on PATH, then common locations such as `%USERPROFILE%\.local\bin`, `%APPDATA%\npm`, and `%LOCALAPPDATA%\Programs\Claude`.

Managed Claude Code sessions support automatic registration and group creation, input to the visible window, Claude Code's native running-task queue, `PermissionRequest`, `AskUserQuestion` single/multiple/custom answers, tool and compaction activity, transcript-based final responses, and safe `--resume` from History.

Clicking Connect merges this project's hooks into `%USERPROFILE%\.claude\settings.json` while preserving unrelated hooks. Claude Code started manually in another terminal can send notifications and handle structured interactions, but ordinary Feishu input is intentionally disabled unless the session was launched through New Claude.

## Launch a synchronized OpenCode window

1. Click `新建 opencode` and select a project directory.
2. Optionally enter arguments such as `-s <session-id>` to resume or `-c` to continue the previous session.
3. The bridge reserves a loopback-only port from `5100–5999`.
4. The panel starts a visible `opencode --port <port>` window with the supplied arguments. It falls back to a normal console if Windows Terminal is unavailable.

The `opencode` executable must be on PATH or in a common directory such as `~\.local\bin`, `%APPDATA%\npm`, or `%LOCALAPPDATA%\Programs\opencode`.

### Automatic discovery of running OpenCode instances

About every 20 seconds, the bridge scans loopback listening ports and checks `/global/health`. This can attach to OpenCode started manually with `opencode --port <port>` as well as windows launched by the desktop panel. When the process exits and its subscription disconnects, the current session is removed and becomes eligible for History.

A plain OpenCode TUI started without `--port` does not expose the local HTTP API and cannot be discovered or injected. Use the desktop launcher or start it with `--port`. Set `OPENCODE_AUTO_DISCOVER=0` to disable discovery.

Each OpenCode port registers only its currently open conversation. Other historical conversations returned by the same instance are not counted as active. The current conversation receives completion, error, compaction, tool, permission, question, and two-way input synchronization. Permission bridging supports `permission.v2.asked/replied`, `/api/permission/request`, the V2 session reply route, and the legacy events and endpoints. Feishu “Allow” maps to `once`, “Reject” maps to `reject`, and the current card does not expose `always`. OpenCode questions support buttons, numbered quoted replies, comma-separated multiple choices, and semicolon-separated answers for multiple questions.

The bridge communicates only with OpenCode HTTP services on the local loopback interface. It does not use a headless `opencode serve` workflow because that would not provide a visible synchronized window.

## Multiple windows and strict routing

Every assistant window has a short ID and can be assigned a readable alias, for example:

```text
@main-project  codex+ #9b45397b
```

Recommended routing options:

1. Send ordinary text directly in the private group bound to that session.
2. In a bot DM, quote the relevant notification or send `@alias message`.
3. In a bot DM, send `#short-id message`.
4. If only one active session exists, ordinary DM text may target it directly.

When several sessions are active, the bridge does not guess. Send `会话` to list them. Managed windows accept immediate input or an explicit queued message; external sessions are notification-only.

“Active session” means:

- A managed Codex or Claude Code window is active from launch until close, even before its first prompt. Normal close removes it immediately; an abnormal exit may take about 20 seconds to expire.
- Each OpenCode listening port registers only the conversation currently open in that instance. Historical conversations merely returned by its API are not active. The current conversation is removed when the window or instance disconnects.
- Codex or Claude Code opened manually in another terminal may appear as External Session. The bridge validates the real CLI PID, executable name, and start time and removes the entry after the process exits. Rare sessions whose process information cannot be read expire after about five minutes.

## Feishu commands

The bot currently uses the following Chinese commands. Send management commands in a direct chat with the bot:

- `绑定 <随机绑定码>`: first-time binding; use the complete command currently shown by the desktop panel.
- `绑定`: restore the same owner's binding after that account used `解绑`.
- `解绑`: remove the binding.
- `工作区`: show the default workspace used by Feishu project creation.
- `新建 codex 项目名`: find or create the named folder under the default workspace, then launch Codex.
- `新建 claude 项目名`: same for Claude Code; `Claude Code` is also accepted.
- `新建 opencode 项目名`: same for OpenCode.
- `状态`: show bridge and session counts.
- `会话`: list active assistant sessions.
- `别名`: show aliases and alias help.
- `别名 #短ID 名称`: set an alias, for example `别名 #9b45397b 主项目`.
- `别名 #短ID 清除`: clear an alias; `别名 @旧别名 新名称` renames it.
- `帮助`: show reply instructions.
- `@别名 内容` or `#短ID 内容`: continue a selected assistant session from a bot DM.
- `排队 @别名 内容` or `排队 #短ID 内容`: queue for the next turn in a managed window.
- `发文件 @别名 内容` or `@别名 /sendfile 内容`: ask the assistant to return a generated file.

`新建` works only in the bot DM and only for a bound administrator. The project name must be one folder name: Chinese characters and spaces are allowed, but drive letters, slashes, `..`, and Windows-reserved filename characters are rejected. The desktop panel must still be running or in the tray. Remote launch always uses normal Windows privileges and never triggers UAC. Change the default workspace in desktop Settings.

Aliases are 1–20 characters and may contain Chinese characters, letters, digits, underscores, and hyphens, but no spaces. Active aliases must be unique and Latin letters are case-insensitive.

Approval cards also accept quoted replies `批准` (approve), `拒绝` (reject), or `本机确认` (transfer to the PC approval window without deciding). Follow-up cards accept option buttons or quoted option numbers/text. Separate multiple selections with commas, such as `1,3`, and multiple questions with Chinese semicolons, such as `1；2,3；custom answer`.

For attachments, send the image or file in a per-session group and then send `analyze this attachment` directly. In the bot DM with several active sessions, follow the attachment with `@alias analyze this attachment`. A minimal file-return test is `发文件 生成一个 test.txt 并发回来` in a session group, or `发文件 @alias 生成一个 test.txt 并发回来` in the bot DM. Staged files are stored under `data/uploads/<month>/`, limited to 25 MiB each by default, and cleaned after seven days.

## Local configuration

Create `.env` in the project directory from `.env.example`. Never share or commit `FEISHU_APP_SECRET`.

```env
FEISHU_APP_ID=cli_xxxxx
FEISHU_APP_SECRET=xxxxx
FEISHU_BIND_COMMAND=绑定
BRIDGE_HTTP_PORT=8765
CODEX_APPROVAL_TIMEOUT_MS=1200000
CODEX_SESSION_ACTIVE_MS=86400000
CODEX_TRANSCRIPT_POLL_INTERVAL_MS=750
CODEX_TRANSCRIPT_IDLE_POLL_INTERVAL_MS=5000
CODEX_TRANSCRIPT_ACTIVE_WINDOW_MS=30000
FEISHU_SESSION_GROUP_INACTIVE_MS=604800000
FEISHU_SESSION_GROUP_CLEANUP_INTERVAL_MS=3600000
RUNTIME_AUTO_LAUNCH_TIMEOUT_MS=120000
DEFAULT_WORKSPACE_ROOT=
CODEX_COMMAND=codex
```

When `DEFAULT_WORKSPACE_ROOT` is empty, the default is the parent directory of the bridge. You can select any real directory in desktop `设置` (Settings); do not copy a drive-specific path from another computer.

Codex and Claude Code hook scripts connect to `http://127.0.0.1:8765` by default. If you change the port, set `CODEX_FEISHU_BRIDGE_URL` in the CLI launch environment or update the hook configuration accordingly.

## Installation and startup

Windows login startup is not installed by default. For normal use, run the desktop executable or your shortcut and click Connect. The desktop executable starts Node.js directly and uses an authenticated local endpoint for graceful shutdown, without VBS or bridge lifecycle PowerShell wrappers. For automation without opening the panel, use `Codex飞书助手.exe --bridge-start` and `Codex飞书助手.exe --bridge-stop`.

The Release ZIP includes optional login-startup scripts. They create a limited, current-user scheduled task and do not elevate the bridge:

```powershell
pwsh -NoProfile -File .\scripts\install-autostart.ps1
pwsh -NoProfile -File .\scripts\uninstall-autostart.ps1
```

To run or debug only the Node.js bridge service:

The following commands do not provide the desktop panel. For managed windows, History, and automatic recovery, also build and run the desktop app as described in “Get the desktop app and run it for the first time”.

```powershell
cd <project-directory>\codex-feishu-bridge
npm install
npm run build
npm start
```

Development mode:

```powershell
npm run dev
```

Before submitting changes, run the full local validation:

```powershell
npm run lint
npm run format:check
npm test
npm run build
dotnet test .\desktop-control\tests\CodexFeishuTerminalHost.Tests.csproj -c Release
dotnet build .\desktop-control\CodexFeishuControl.csproj -c Release
```

`npm run format:check` is a zero-dependency text hygiene check for trailing whitespace and final newlines. The Windows CI workflow runs the same Node.js and .NET validation.

The bridge starts a Feishu WebSocket connection, a hook HTTP server bound only to `127.0.0.1`, and a health endpoint at `http://127.0.0.1:8765/health` by default.

### Rebuild the desktop executables

```powershell
dotnet publish .\desktop-control\CodexFeishuControl.csproj -c Release -o .\desktop-control\publish
```

The output contains `CodexFeishuControl.exe` and `CodexFeishuTerminalHost.exe`. Keep both in the same directory. They use the locally installed .NET 8 Windows Desktop Runtime. You may copy or rename the main UI executable to `Codex飞书助手.exe` for the documented shortcut name, but do not rename or omit the terminal host.

## Codex hooks

The user-level Codex hook file is `%USERPROFILE%\.codex\hooks.json`. Connect runs `scripts/install-hooks.ps1`, removes only older entries installed by this bridge, and preserves unrelated hooks.

Installed hook events include `SessionStart`, `SessionEnd`, `PermissionRequest`, `Stop`, `PreToolUse` including `request_user_input`, `PostToolUse`, `PreCompact`, `PostCompact`, and `UserPromptSubmit`.

`Stop` covers normal completion and follow-up notifications, but Codex does not run it for every sampling failure. For newly appended `task_complete.error` records, the bridge polls active JSONL transcripts and reuses the same Feishu error-card, deduplication, and optional retry path. Polling starts at the current end of each transcript to avoid replaying old failures. By default it checks every 750ms for 30 seconds after a new activity registration or transcript change, then backs off to every 5 seconds until the next change or hook immediately restores fast polling. Configure these periods with `CODEX_TRANSCRIPT_POLL_INTERVAL_MS`, `CODEX_TRANSCRIPT_IDLE_POLL_INTERVAL_MS`, and `CODEX_TRANSCRIPT_ACTIVE_WINDOW_MS`.

On the first new Codex window, Codex may ask you to review and trust the hook. Verify its path and content and approve normally; do not use unsafe flags to bypass hook trust. After hook configuration or compiled hook output changes, reconnect/restart the bridge and open a new Codex window.

## Claude Code hooks

The user-level Claude Code settings file is `%USERPROFILE%\.claude\settings.json`. Connect runs `scripts/install-claude-code-hooks.ps1` and merges matcher-group command hooks while preserving existing user and plugin hooks.

Installed events include `SessionStart`, `SessionEnd`, `PermissionRequest`, `PreToolUse`, `PostToolUse`, `PostToolUseFailure`, `PreCompact`, `PostCompact`, `UserPromptSubmit`, and `Stop`. `AskUserQuestion` is converted to a Feishu follow-up card, and `Stop` reads the final assistant message from the JSONL transcript referenced by `transcript_path`.

The installer removes only obsolete entries from earlier versions of this bridge and is idempotent. Run `claude doctor` in any safe directory; a healthy configuration should include `No installation issues found.`. Reconnect and open a new Claude Code window after configuration or `dist/hooks` changes.

## Data and security

Runtime state and audit data stay under `data/`, while Feishu credentials remain in `.env` at the project root. The project provides and uses no project-operated relay server:

- `bindings.json`, `sessions.json`, `message-routes.json`, `approvals.json`, and `settings.json` hold binding, session, routing, approval, and settings state.
- `control-token.json` stores the random local-control token generated on first startup.
- `approval-events.log` and rotated backups provide a local audit trail for approval requests, notification delivery, and decision sources.
- `uploads/` contains files received from Feishu or staged for an explicit return.

These files, rotated logs, quarantined corrupt files, and `.env` are ignored by Git. Release ZIPs explicitly exclude `.env`, `data/`, and internal review files. JSON state is written through a temporary file and atomically replaced. A file that fails structural validation is preserved as `*.corrupt-*`, while the bridge starts from safe defaults instead of overwriting the evidence.

To keep long-running installations from repeatedly parsing and rewriting unbounded JSON, message routes are retained for seven days and capped at 3,000 records, while inbound deduplication records are capped at 5,000. Resolved or orphaned approvals are retained for 24 hours and capped at 500 records. Pending approvals are never removed by the count limit, and the rotating `approval-events.log` remains the long-term audit trail. This maintenance does not change sessions, aliases, or Feishu group bindings stored in `sessions.json`.

The hook HTTP service listens only on `127.0.0.1`. Anonymous health checks omit pairing codes, sessions, approvals, and settings. Control and hook writes require the persistent random token and a JSON content type, and cross-site browser requests are rejected. Approval content receives basic redaction of common token, secret, password, and API-key fields plus length limits. Approval logs rotate at 5 MiB by default with up to five backups. The attachment staging area defaults to 500 files and 1 GiB total and cleans files older than seven days.

## Behavior notes

For a managed Codex window, Feishu text is written to the original console input buffer. Ordinary messages use Enter to interrupt; the explicit `排队` prefix uses Tab for the next turn. Stop hooks send the response from that same window to the bound group.

Managed Claude Code input also enters the original visible window. Claude Code receives both immediate and explicit queued messages with Enter and manages its running-task queue itself. `PermissionRequest`, `AskUserQuestion`, and final responses remain inside the original session.

Externally launched Codex/Claude Code windows receive key notifications and structured approval/follow-up handling only. Ordinary messages, queued tasks, and file requests are rejected so a second process cannot advance the same conversation concurrently. Use the corresponding New button for full two-way synchronization. This project does not use a Codex `app-server` background executor and does not provide a WeChat channel.

File return is explicit. After `发文件` or `/sendfile`, the assistant must include `BRIDGE_SEND_FILE: <absolute-path>` in its final response. The bridge accepts only regular files inside the current project directory, with an allowed extension and a maximum size of 30 MiB.

## Resume parameters and working directories

All three CLIs expose enough local session data to recover the original working directory, but their resume arguments do not consistently change directories. The desktop panel therefore treats the saved `session.cwd` as the source of truth:

| Runtime | Resume argument | Directory behavior |
| --- | --- | --- |
| Codex | `resume <session_id>` | Codex supports `-C/--cd`; the panel launches directly in the saved directory |
| Claude Code | `--resume <session_id>` / `-r <session_id>` | Claude Code has no separate `--cwd`; the panel launches in the saved directory |
| OpenCode | `-s <session_id>` / `--session <session_id>` | The directory is positional/current-process state; the panel launches in the saved directory and preserves a local port |

When a complete known session ID is entered in a New-window argument field, the project directory is filled from local history. Automatic recovery from a Feishu group uses the same saved directory and does not rely on the CLI to guess it.

## Troubleshooting

- No Feishu notifications: confirm that the bridge process is running and open `/health`.
- Codex hooks do not fire: open a new Codex window and complete the hook trust review.
- Codex shows a high-demand error but Feishu receives nothing: update and restart the bridge, keep the managed session active, and verify the bridge is running. Transcript monitoring reports these failures without relying on `Stop`.
- Claude Code hooks do not fire: click Connect to merge `%USERPROFILE%\.claude\settings.json`, run `claude doctor`, close the old window, and open a new one.
- Card buttons do nothing: confirm `card.action.trigger` is configured and the newest app version is published.
- Wrong-session risk with several windows: send `会话`, then quote the correct notification or use an alias/short ID; prefix with `排队` for the next turn.
- A reply immediately fails: verify `codex --version`, `claude --version`, or `opencode --version` in the same PowerShell environment.
- A managed window says it is closed: reopen it with the corresponding New button or resume it from History.
- A group message does not reopen a closed session: keep the desktop panel running or in the tray and confirm that the bridge and desktop versions match.
- A resumed session disappears again after closing: restart the updated bridge to migrate legacy records. New versions clear the hidden-history marker whenever a managed session becomes active again.
- A Feishu `新建` command does not open a window: send `工作区`, verify the default directory, keep the panel running, and use a one-folder project name without path separators.
- Inactive groups are not disbanded: publish `im:chat:delete` and `im:chat:operate_as_owner`; the bridge checks about once per hour by default.
- OpenCode waits for registration: confirm that `opencode` is installed and keep the new window open while the reserved port health check retries.
- Feishu input does not enter the local window: the session's Mode must be Window Sync or Administrator Sync; External Session is intentionally notification-only.
- An administrator window does not launch: complete UAC locally; Feishu cannot approve UAC.
- New hooks do not take effect: click Connect again, close old CLI windows, and open new ones. Codex also requires hook trust approval.
- Received attachments fail: downloading requires published `im:message:readonly`; uploading a returned file requires published `im:resource`. Also check the local file-size limit.

## License and project notice

The source code is released under the [MIT License](LICENSE). You may use, modify, and distribute it while retaining the copyright and license notice. The software is provided “as is”, without express or implied warranties.

This is an unofficial community project and is not affiliated with, authorized by, or endorsed by OpenAI, Anthropic, OpenCode, or Feishu. Their names and trademarks belong to their respective owners. Third-party CLIs, SDKs, online services, and generated content remain subject to their own licenses, terms of service, and usage policies; this project's MIT License grants no rights to those products.
