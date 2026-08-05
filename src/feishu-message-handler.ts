import { randomUUID } from "node:crypto";

import {
  appendAttachmentsToPrompt,
  parseFeishuContent,
} from "./attachments.js";
import {
  ApprovalCoordinator,
  approvalActionFromText,
  approvalText,
} from "./approval-coordinator.js";
import { buildRuntimeSelectionCard } from "./cards.js";
import {
  runtimeDefinition,
  runtimeDisplayName,
  runtimeReceivedText,
  truncate,
  type SessionRecord,
} from "./domain.js";
import { addFileReturnInstruction } from "./file-transfer.js";
import { FileTransferCoordinator } from "./file-transfer-coordinator.js";
import { ManagedTerminalRouter } from "./managed-terminal.js";
import {
  aliasCommandUsage,
  newRuntimeCommandUsage,
  parseAliasCommand,
  parseBindCommand,
  parseExplicitAlias,
  parseExplicitSession,
  parseNewRuntimeCommand,
  parsePromptDirectives,
} from "./message-command-parser.js";
import { OpenCodeManager } from "./opencode-manager.js";
import { RuntimeLaunchCoordinator } from "./runtime-launch-coordinator.js";
import { SessionDirectory } from "./session-directory.js";
import { BridgeStore } from "./store.js";
import {
  inputAnswerUsage,
  parseUserInputAnswers,
  UserInputCoordinator,
} from "./user-input-coordinator.js";

type FeishuEvent = Record<string, any>;

interface FeishuMessageHandlerDependencies {
  store: BridgeStore;
  bindCommand: string;
  files: FileTransferCoordinator;
  runtimeLaunches: RuntimeLaunchCoordinator;
  sessionDirectory: SessionDirectory;
  inputs: UserInputCoordinator;
  approvals: ApprovalCoordinator;
  managedTerminals: ManagedTerminalRouter;
  opencode?: OpenCodeManager;
  queuedPromptCount: () => number;
  initializeSessionGroups: () => Promise<void>;
  respond: (
    sourceMessageId: string,
    chatId: string,
    text: string,
  ) => Promise<string | undefined>;
  respondCard: (
    sourceMessageId: string,
    chatId: string,
    card: Record<string, unknown>,
  ) => Promise<string | undefined>;
  resumeSession: (
    session: SessionRecord,
    prompt: string,
    sourceMessageId: string,
    chatId: string,
    queueRequested: boolean,
    requestFileReturn: boolean,
  ) => Promise<void>;
}

export class FeishuMessageHandler {
  constructor(private readonly dependencies: FeishuMessageHandlerDependencies) {}

  async handle(data: FeishuEvent): Promise<void> {
    const {
      store,
      files,
      runtimeLaunches,
      sessionDirectory,
      inputs,
      approvals,
      managedTerminals,
      opencode,
    } = this.dependencies;
    const openId = data.sender?.sender_id?.open_id;
    const message = data.message;
    const chatId = message?.chat_id;
    const messageId = message?.message_id;
    const chatType = message?.chat_type ?? "unknown";
    const parsedContent = parseFeishuContent(message);
    const text = parsedContent.text;

    if (!openId || !chatId || !messageId) {
      console.warn("[message] Ignored a message without sender, chat, or message id.");
      return;
    }

    if (!(await store.claimInboundMessage(messageId))) {
      console.log(`[message] Ignored duplicate Feishu message ${messageId}.`);
      return;
    }

    console.log(
      `[message] Received Feishu ${String(message?.message_type ?? "text")} (${text.length} chars, ${parsedContent.attachments.length} attachments, ${chatType}).`,
    );

    const bindAttempt =
      chatType === "p2p"
        ? parseBindCommand(text, this.dependencies.bindCommand)
        : { matched: false };
    if (bindAttempt.matched) {
      const result = await store.bindOwner(
        {
          openId,
          chatId,
          chatType,
          boundAt: new Date().toISOString(),
        },
        bindAttempt.code,
      );
      if (result === "invalid_code") {
        await this.respond(
          messageId,
          chatId,
          `绑定码不正确。请在电脑端 AI CLI 飞书助手中查看本机绑定命令，再发送“${this.dependencies.bindCommand} 绑定码”。`,
        );
        return;
      }
      if (result === "owner_mismatch") {
        await this.respond(
          messageId,
          chatId,
          "这个助手已经设置了唯一管理员，其他飞书账号不能绑定或控制本机 Codex。",
        );
        return;
      }
      await this.respond(
        messageId,
        chatId,
        result === "bound"
          ? "绑定成功，你已成为这台电脑上 Codex 助手的唯一管理员。"
          : "管理员绑定已恢复。现在可以继续接收通知和回复 Codex。",
      );
      void this.dependencies.initializeSessionGroups().catch((error) => {
        console.warn("[feishu] Could not initialize existing session groups:", error);
      });
      return;
    }

    if (chatType === "p2p" && text === "解绑") {
      const removed = await store.removeBinding(openId);
      await this.respond(
        messageId,
        chatId,
        removed ? "已解绑。" : "当前账号还没有绑定。",
      );
      return;
    }

    if (!store.isBound(openId)) {
      await this.respond(
        messageId,
        chatId,
        store.getOwnerOpenId()
          ? "飞书连接正常，但这个助手只允许已设置的管理员账号操作。"
          : `飞书连接正常。请先在电脑端查看随机绑定码，然后私聊发送“${this.dependencies.bindCommand} 绑定码”。`,
      );
      return;
    }

    // Bridge slash commands belong to the Feishu control surface, not to an
    // individual AI runtime. Handle the known commands before resolving a
    // session-group route so they never depend on the active-session count.
    const normalizedCommand = text.trim().toLocaleLowerCase("en-US");
    if (isSlashCommand(normalizedCommand, "new", "新建")) {
      const sent = await this.dependencies.respondCard(
        messageId,
        chatId,
        buildRuntimeSelectionCard(
          store.getSettings().workspaceRoot || undefined,
          {
            flowId: randomUUID(),
            sourceMessageId: messageId,
            chatId,
          },
        ),
      );
      if (!sent) {
        await this.respond(
          messageId,
          chatId,
          "运行环境选择卡片发送失败，请稍后重试 /新建。",
        );
      }
      return;
    }
    if (isSlashCommand(normalizedCommand, "workspace", "工作区")) {
      const workspaceRoot = store.getSettings().workspaceRoot;
      await this.respond(
        messageId,
        chatId,
        workspaceRoot
          ? `默认工作区：${workspaceRoot}\n新建命令示例：新建 codex 我的项目`
          : "尚未设置默认工作区。请在电脑端“设置”中选择。",
      );
      return;
    }
    if (isSlashCommand(normalizedCommand, "status", "状态")) {
      const sessions = sessionDirectory.listActive();
      const pending = sessions.filter(
        (session) => session.status === "pending_approval",
      ).length;
      await this.respond(
        messageId,
        chatId,
        `飞书桥接在线，当前账号已绑定。活跃会话 ${sessions.length} 个，待审批 ${pending} 个，待补充 ${inputs.pendingCount} 个，排队 ${this.dependencies.queuedPromptCount()} 条。\n${sessionDirectory.activeDefinition()}`,
      );
      return;
    }
    if (isSlashCommand(normalizedCommand, "sessions", "会话", "会话管理")) {
      await this.respond(messageId, chatId, sessionDirectory.formatSessionList());
      return;
    }
    if (isSlashCommand(normalizedCommand, "aliases", "别名", "会话别名")) {
      await sessionDirectory.handleAliasCommand({}, messageId, chatId);
      return;
    }
    if (isSlashCommand(normalizedCommand, "help", "帮助")) {
      await this.respond(messageId, chatId, bridgeCommandHelpText());
      return;
    }

    const groupSession =
      chatType === "p2p" ? undefined : store.findSessionByFeishuChatId(chatId);
    if (chatType !== "p2p" && !groupSession) {
      await this.respond(
        messageId,
        chatId,
        codexNotReceived("当前群未绑定会话。"),
      );
      return;
    }
    if (groupSession) {
      await store.touchSessionActivity(groupSession.sessionId);
    }

    const attachmentKey = files.attachmentKey(openId, chatId);
    if (parsedContent.attachments.length > 0) {
      try {
        await files.downloadAndStage(
          attachmentKey,
          messageId,
          parsedContent.attachments,
        );
      } catch (error) {
        const detail = error instanceof Error ? error.message : String(error);
        await this.respond(
          messageId,
          chatId,
          `附件接收失败：${truncate(detail, 500)}`,
        );
        return;
      }
      if (!text) {
        const staged = files.peek(attachmentKey);
        await this.respond(
          messageId,
          chatId,
          groupSession
            ? `已安全保存 ${parsedContent.attachments.length} 个附件（当前暂存 ${staged.length} 个）。下一条直接发送处理要求即可。`
            : `已安全保存 ${parsedContent.attachments.length} 个附件（当前暂存 ${staged.length} 个）。下一条请发送处理要求；有多个窗口时请写成“@别名 要求”或“#短ID 要求”。`,
        );
        return;
      }
    }

    // Plain-language administration remains private so words such as “状态”
    // or “帮助” can still be sent to an assistant in its session group. Known
    // slash commands were already handled above as bridge-level commands.
    const isPrivateChat = chatType === "p2p";
    const privateCommand = normalizedCommand;

    if (isPrivateChat) {
      const newRuntimeCommand = parseNewRuntimeCommand(text);
      if (newRuntimeCommand) {
        await runtimeLaunches.handleNewCommand(
          newRuntimeCommand,
          messageId,
          chatId,
        );
        return;
      }
      if (text.trim() === "新建") {
        const sent = await this.dependencies.respondCard(
          messageId,
          chatId,
          buildRuntimeSelectionCard(
            store.getSettings().workspaceRoot || undefined,
            {
              flowId: randomUUID(),
              sourceMessageId: messageId,
              chatId,
            },
          ),
        );
        if (!sent) {
          await this.respond(
            messageId,
            chatId,
            "运行环境选择卡片发送失败，请稍后重试 /新建。",
          );
        }
        return;
      }
      if (/^新建(?:\s|$)/iu.test(text)) {
        await this.respond(messageId, chatId, newRuntimeCommandUsage());
        return;
      }
    }

    if (
      isPrivateChat &&
      (text === "工作区" ||
        privateCommand === "workspace")
    ) {
      const workspaceRoot = store.getSettings().workspaceRoot;
      await this.respond(
        messageId,
        chatId,
        workspaceRoot
          ? `默认工作区：${workspaceRoot}\n新建命令示例：新建 codex 我的项目`
          : "尚未设置默认工作区。请在电脑端“设置”中选择。",
      );
      return;
    }

    if (isPrivateChat && text === "状态") {
      const sessions = sessionDirectory.listActive();
      const pending = sessions.filter(
        (session) => session.status === "pending_approval",
      ).length;
      await this.respond(
        messageId,
        chatId,
        `飞书桥接在线，当前账号已绑定。活跃会话 ${sessions.length} 个，待审批 ${pending} 个，待补充 ${inputs.pendingCount} 个，排队 ${this.dependencies.queuedPromptCount()} 条。\n${sessionDirectory.activeDefinition()}`,
      );
      return;
    }

    if (
      isPrivateChat &&
      (text === "会话" ||
        privateCommand === "sessions")
    ) {
      await this.respond(messageId, chatId, sessionDirectory.formatSessionList());
      return;
    }

    if (isPrivateChat) {
      const aliasCommand = parseAliasCommand(text);
      if (aliasCommand) {
        await sessionDirectory.handleAliasCommand(
          aliasCommand,
          messageId,
          chatId,
        );
        return;
      }
      if (/^别名(?:\s|$)/.test(text)) {
        await this.respond(messageId, chatId, aliasCommandUsage());
        return;
      }
    }

    if (
      isPrivateChat &&
      (text === "帮助" || privateCommand === "/")
    ) {
      await this.respond(messageId, chatId, bridgeCommandHelpText());
      return;
    }

    if (!text) {
      await this.respond(
        messageId,
        chatId,
        "没有识别到文字或可下载的附件。请发送“帮助”查看用法。",
      );
      return;
    }

    const quotedRoute = store.findMessageRoute([
      message?.parent_id,
      message?.root_id,
    ]);

    if (quotedRoute?.kind === "input" && quotedRoute.requestId) {
      const waiter = inputs.get(quotedRoute.requestId);
      if (!waiter) {
        await this.respond(messageId, chatId, "这组问题已经处理或失效。");
        return;
      }
      const questionId = waiter.messageCards.find(
        (item) => item.messageId === quotedRoute.messageId,
      )?.questionId;
      const targetQuestions = questionId
        ? waiter.questions.filter((question) => question.id === questionId)
        : waiter.questions;
      const answers = parseUserInputAnswers(text, targetQuestions);
      if (!answers) {
        await this.respond(
          messageId,
          chatId,
          inputAnswerUsage(targetQuestions),
        );
        return;
      }
      const result = questionId
        ? await inputs.recordAnswer(
            quotedRoute.requestId,
            questionId,
            answers[questionId] ?? [],
          )
        : (await inputs.answer(quotedRoute.requestId, answers))
          ? "submitted"
          : "stale";
      const inputSession = store.getSession(waiter.sessionId);
      await this.respond(
        messageId,
        chatId,
        result === "submitted"
          ? receivedText(inputSession)
          : result === "recorded"
            ? "已记录这张问题卡片的答案，请继续处理其他问题。"
            : result === "failed"
              ? notReceivedText(
                  inputSession,
                  "暂时无法把答案交给助手，请稍后重试。",
                )
              : notReceivedText(inputSession, "问题已处理或失效。"),
      );
      return;
    }

    if (
      quotedRoute?.requestId &&
      store.hasPendingApprovalForSession(quotedRoute.sessionId)
    ) {
      const approvalAction = approvalActionFromText(text);
      if (approvalAction === "desktop") {
        const requested = await approvals.requestDesktop(
          quotedRoute.requestId,
          "feishu_text",
        );
        await this.respond(
          messageId,
          chatId,
          requested
            ? "已转回 PC 审批，电脑端审批窗口将在下一次状态刷新时弹出。"
            : "这条审批已经处理或失效。",
        );
      } else if (approvalAction) {
        const approvalSession = store.getSession(quotedRoute.sessionId);
        const completed = await approvals.complete(
          quotedRoute.requestId,
          approvalAction,
          { source: "feishu_text" },
        );
        await this.respond(
          messageId,
          chatId,
          completed
            ? approvalText(approvalAction, approvalSession)
            : "这条审批已经处理或失效。",
        );
      } else {
        await this.respond(
          messageId,
          chatId,
          "这个会话正在等待审批。请点击审批卡片按钮，或引用卡片回复“批准”“拒绝”或“本机确认”。",
        );
      }
      return;
    }

    const leadingDirectives = parsePromptDirectives(text);
    const explicit =
      parseExplicitSession(leadingDirectives.prompt) ??
      parseExplicitAlias(leadingDirectives.prompt);

    let target: SessionRecord | undefined;
    let prompt = leadingDirectives.prompt;
    let queueRequested = leadingDirectives.queue;
    let fileReturnRequested = leadingDirectives.fileReturn;

    if (groupSession) {
      target = store.getSession(groupSession.sessionId) ?? groupSession;
      // A session group is already an unambiguous route. Ignore @alias/#id
      // prefixes here so one group can never accidentally steer another session.
      if (explicit) {
        prompt = explicit.prompt;
      }
    } else if (explicit) {
      const matches =
        explicit.kind === "short"
          ? sessionDirectory.findByShortToken(explicit.token)
          : sessionDirectory.findByAlias(explicit.token);
      const address =
        explicit.kind === "short"
          ? `#${explicit.token}`
          : `@${explicit.token}`;
      if (matches.length !== 1) {
        await this.respond(
          messageId,
          chatId,
          matches.length === 0
            ? codexNotReceived(`没有找到 ${address} 对应的活跃会话。`)
            : explicit.kind === "short"
              ? codexNotReceived(`${address} 匹配到多个会话。`)
              : codexNotReceived(`${address} 不是唯一别名。`),
        );
        return;
      }
      target = matches[0];
      prompt = explicit.prompt;
    } else if (quotedRoute) {
      target = sessionDirectory
        .listActive()
        .find((session) => session.sessionId === quotedRoute.sessionId);
    } else {
      const activeSessions = sessionDirectory.listActive();
      if (activeSessions.length === 1) {
        target = activeSessions[0];
      } else {
        await this.respond(
          messageId,
          chatId,
          activeSessions.length === 0
            ? codexNotReceived("当前没有活跃会话。")
            : codexNotReceived("有多个活跃会话，请指定目标。"),
        );
        return;
      }
    }

    if (!target) {
      await this.respond(
        messageId,
        chatId,
        groupSession
          ? codexNotReceived("对应窗口已关闭。")
          : codexNotReceived("对应会话不可用。"),
      );
      return;
    }
    const nestedDirectives = parsePromptDirectives(prompt);
    prompt = nestedDirectives.prompt;
    queueRequested ||= nestedDirectives.queue;
    fileReturnRequested ||= nestedDirectives.fileReturn;
    if (!prompt) {
      await this.respond(messageId, chatId, codexNotReceived("内容为空。"));
      return;
    }
    const attachments = files.take(attachmentKey);
    prompt = appendAttachmentsToPrompt(prompt, attachments);
    if (fileReturnRequested) {
      prompt = addFileReturnInstruction(prompt);
    }
    const targetRuntime = runtimeDefinition(target.runtime);
    if (
      groupSession &&
      target.managedByAssistant === true &&
      !target.clientProcessId &&
      !runtimeLaunches.isAvailable(target)
    ) {
      await runtimeLaunches.queueResume(target, {
        prompt,
        sourceMessageId: messageId,
        chatId,
        requestFileReturn: fileReturnRequested,
        queueRequested,
      });
      return;
    }
    if (
      !managedTerminals.isManaged(target) &&
      targetRuntime.transport !== "http_event_stream"
    ) {
      await this.respond(
        messageId,
        chatId,
        externalSessionInputBlockedMessage(target),
      );
      return;
    }
    if (
      targetRuntime.transport === "http_event_stream" &&
      !opencode?.findActiveInstanceBySession(target.sessionId)
    ) {
      await this.respond(
        messageId,
        chatId,
        notReceivedText(target, "opencode 窗口未连接。"),
      );
      return;
    }
    await this.dependencies.resumeSession(
      target,
      prompt,
      messageId,
      chatId,
      queueRequested,
      fileReturnRequested,
    );
  }

  private async respond(
    sourceMessageId: string,
    chatId: string,
    text: string,
  ): Promise<string | undefined> {
    return await this.dependencies.respond(sourceMessageId, chatId, text);
  }
}

function externalSessionInputBlockedMessage(session: SessionRecord): string {
  return notReceivedText(
    session,
    "这个窗口不是由 AI CLI 飞书助手打开，不能从飞书回复。请回到原窗口继续。",
  );
}

function codexNotReceived(reason: string): string {
  return notReceivedText(undefined, reason);
}

function isSlashCommand(
  normalizedText: string,
  ...names: string[]
): boolean {
  return names.some((name) => normalizedText === `/${name}`);
}

function bridgeCommandHelpText(): string {
  return "一级命令：\n/新建 — 新建会话\n/会话 — 会话管理\n/状态 — 查看状态\n/工作区 — 查看工作区\n/别名 — 会话别名\n/帮助 — 全部功能\n\n发送 /新建 后，从同一张卡片选择 Codex、Claude Code 或 OpenCode，再填写项目名。英文命令 /new、/sessions、/status、/workspace、/aliases、/help 继续兼容；旧的“新建 codex 项目名”等文本命令也仍可使用。\n\n会话消息：引用助手通知回复；或发送 @别名 内容。需要排队时发送“排队 @别名 内容”，需要返回文件时发送“发文件 @别名 要求”。也可以先发图片或文件，下一条再发处理要求。";
}

function receivedText(
  session: Pick<SessionRecord, "runtime"> | undefined,
): string {
  return runtimeReceivedText(session?.runtime);
}

function notReceivedText(
  session: Pick<SessionRecord, "runtime"> | undefined,
  reason: string,
): string {
  return `${runtimeDisplayName(session?.runtime)} 未接收：${reason}`;
}
