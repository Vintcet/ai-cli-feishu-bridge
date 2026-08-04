import {
  buildResolvedUserInputQuestionCard,
  buildUserInputCards,
  buildUserInputQuestionCard,
} from "./cards.js";
import type {
  SessionRecord,
  UserInputAnswers,
  UserInputQuestion,
} from "./domain.js";
import { truncate } from "./domain.js";
import { FeishuGateway } from "./feishu.js";
import { OpenCodeManager } from "./opencode-manager.js";
import { BridgeStore } from "./store.js";

export type UserInputResolution =
  | { kind: "answered"; answers: UserInputAnswers }
  | { kind: "local" | "timeout" | "rejected" };

export type UserInputAnswerResult =
  | "submitted"
  | "recorded"
  | "failed"
  | "empty"
  | "stale";

export type UserInputToggleResult = "selected" | "deselected" | "stale";

export interface UserInputMessageCard {
  messageId: string;
  questionId: string;
  chatId: string;
}

export interface UserInputWaiter {
  source: "hook" | "opencode";
  sessionId: string;
  turnId: string;
  cwd: string;
  questions: UserInputQuestion[];
  messageCards: UserInputMessageCard[];
  answers: UserInputAnswers;
  selections: Record<string, UserInputAnswers>;
  timer: NodeJS.Timeout;
  resolve?: (resolution: UserInputResolution) => void;
  opencodeRequestId?: string;
}

export interface UserInputRegistration {
  source: "hook" | "opencode";
  sessionId: string;
  turnId: string;
  cwd: string;
  questions: UserInputQuestion[];
  opencodeRequestId?: string;
  timeoutMs?: number;
}

interface UserInputCoordinatorDependencies {
  store: BridgeStore;
  feishu: FeishuGateway;
  opencode?: OpenCodeManager;
  inputTimeoutMs: number;
  onOpenCodeQuestionAnswered?: (
    sessionId: string,
    requestId: string,
  ) => void;
}

export class UserInputCoordinator {
  private readonly waiters = new Map<string, UserInputWaiter>();
  private readonly submitting = new Set<string>();

  constructor(private readonly dependencies: UserInputCoordinatorDependencies) {}

  get pendingCount(): number {
    return this.waiters.size;
  }

  get(requestId: string): UserInputWaiter | undefined {
    return this.waiters.get(requestId);
  }

  createHookWaiter(
    requestId: string,
    registration: Omit<UserInputRegistration, "source" | "opencodeRequestId">,
  ): Promise<UserInputResolution> {
    return new Promise<UserInputResolution>((resolve) => {
      this.register(requestId, { ...registration, source: "hook" }, resolve);
    });
  }

  registerOpenCode(
    requestId: string,
    registration: Omit<UserInputRegistration, "source">,
  ): void {
    this.register(requestId, { ...registration, source: "opencode" });
  }

  discard(requestId: string): void {
    const waiter = this.waiters.get(requestId);
    if (!waiter) {
      return;
    }
    clearTimeout(waiter.timer);
    this.waiters.delete(requestId);
    this.submitting.delete(requestId);
  }

  dispose(): void {
    for (const waiter of this.waiters.values()) {
      clearTimeout(waiter.timer);
      waiter.resolve?.({ kind: "local" });
    }
    this.waiters.clear();
    this.submitting.clear();
  }

  hasPendingForSession(sessionId: string): boolean {
    return [...this.waiters.values()].some(
      (waiter) => waiter.sessionId === sessionId,
    );
  }

  async sendCards(
    session: SessionRecord,
    requestId: string,
    questions: UserInputQuestion[],
    recipients: Array<{ chatId: string }>,
    logLabel: string,
  ): Promise<boolean> {
    const deliveredQuestionIds = new Set<string>();
    for (const recipient of recipients) {
      const cards = buildUserInputCards(
        session,
        requestId,
        questions,
        recipient.chatId,
      );
      for (const [questionIndex, card] of cards.entries()) {
        const question = questions[questionIndex];
        if (!question) {
          continue;
        }
        try {
          const messageId = await this.dependencies.feishu.sendCard(
            recipient.chatId,
            card,
          );
          deliveredQuestionIds.add(question.id);
          this.waiters.get(requestId)?.messageCards.push({
            messageId,
            questionId: question.id,
            chatId: recipient.chatId,
          });
          await this.dependencies.store.addMessageRoute({
            messageId,
            sessionId: session.sessionId,
            requestId,
            chatId: recipient.chatId,
            kind: "input",
            createdAt: new Date().toISOString(),
          });
        } catch (error) {
          console.error(`[${logLabel}] Failed to send a Feishu question card:`, error);
        }
      }
    }
    return questions.every((question) => deliveredQuestionIds.has(question.id));
  }

  findOpenCodeInput(
    sessionId: string,
    opencodeRequestId: string,
  ): { requestId: string; waiter: UserInputWaiter } | undefined {
    for (const [requestId, waiter] of this.waiters) {
      if (
        waiter.source === "opencode" &&
        waiter.sessionId === sessionId &&
        waiter.opencodeRequestId === opencodeRequestId
      ) {
        return { requestId, waiter };
      }
    }
    return undefined;
  }

  async answer(
    requestId: string,
    answers: UserInputAnswers,
  ): Promise<boolean> {
    const waiter = this.waiters.get(requestId);
    if (!waiter || this.submitting.has(requestId)) {
      return false;
    }
    if (waiter.source === "hook") {
      return this.complete(requestId, { kind: "answered", answers });
    }
    if (!waiter.opencodeRequestId || !this.dependencies.opencode) {
      return false;
    }

    this.submitting.add(requestId);
    try {
      const orderedAnswers = waiter.questions.map(
        (question) => answers[question.id] ?? [],
      );
      await this.dependencies.opencode.replyQuestion(
        waiter.sessionId,
        waiter.opencodeRequestId,
        orderedAnswers,
      );
      const completed = await this.complete(requestId, {
        kind: "answered",
        answers,
      });
      this.dependencies.onOpenCodeQuestionAnswered?.(
        waiter.sessionId,
        waiter.opencodeRequestId,
      );
      return completed || !this.waiters.has(requestId);
    } catch (error) {
      console.error("[input] Failed to forward answer to opencode:", error);
      try {
        const pending = await this.dependencies.opencode.listQuestions(
          waiter.sessionId,
        );
        if (!pending.some((request) => request.id === waiter.opencodeRequestId)) {
          await this.complete(requestId, { kind: "local" });
        }
      } catch (probeError) {
        console.warn("[input] Could not confirm opencode question state:", probeError);
      }
      return false;
    } finally {
      this.submitting.delete(requestId);
    }
  }

  async toggleAnswer(
    requestId: string,
    questionId: string,
    answer: string,
    selectionKey: string,
  ): Promise<UserInputToggleResult> {
    const waiter = this.waiters.get(requestId);
    const question = waiter?.questions.find((item) => item.id === questionId);
    if (!waiter || !question || waiter.answers[questionId]) {
      return "stale";
    }
    const selections = waiter.selections[selectionKey] ?? {};
    const current = selections[questionId] ?? [];
    const selected = current.includes(answer);
    const next = selected
      ? current.filter((item) => item !== answer)
      : [...current, answer];
    if (next.length > 0) {
      selections[questionId] = next;
      waiter.selections[selectionKey] = selections;
    } else {
      delete selections[questionId];
      if (Object.keys(selections).length === 0) {
        delete waiter.selections[selectionKey];
      }
    }
    await this.patchQuestionCards(
      requestId,
      questionId,
      { selectedAnswers: next },
      selectionKey,
    );
    return selected ? "deselected" : "selected";
  }

  async submitQuestion(
    requestId: string,
    questionId: string,
    selectionKey: string,
  ): Promise<UserInputAnswerResult> {
    const waiter = this.waiters.get(requestId);
    const selected = waiter?.selections[selectionKey]?.[questionId] ?? [];
    if (selected.length === 0) {
      return waiter ? "empty" : "stale";
    }
    const result = await this.recordAnswer(requestId, questionId, selected);
    if (result === "recorded" || result === "submitted") {
      const current = this.waiters.get(requestId);
      if (current) {
        const selections = current.selections[selectionKey];
        if (selections) {
          delete selections[questionId];
          if (Object.keys(selections).length === 0) {
            delete current.selections[selectionKey];
          }
        }
      }
    }
    return result;
  }

  async recordAnswer(
    requestId: string,
    questionId: string,
    answers: string[],
  ): Promise<UserInputAnswerResult> {
    const waiter = this.waiters.get(requestId);
    const question = waiter?.questions.find((item) => item.id === questionId);
    if (!waiter || !question || waiter.answers[questionId]) {
      return "stale";
    }
    if (answers.length === 0) {
      return "empty";
    }

    waiter.answers[questionId] = [...answers];
    const allAnswered = waiter.questions.every((item) => waiter.answers[item.id]);
    if (!allAnswered) {
      const remainingQuestions = waiter.questions.filter(
        (item) => !waiter.answers[item.id],
      ).length;
      await this.patchQuestionCards(requestId, questionId, {
        selectedAnswers: answers,
        answered: true,
        remainingQuestions,
      });
      return "recorded";
    }

    const completed = await this.answer(requestId, waiter.answers);
    if (completed) {
      return "submitted";
    }
    const attemptedAnswers = Object.fromEntries(
      Object.entries(waiter.answers).map(([id, values]) => [id, [...values]]),
    ) as UserInputAnswers;
    for (const id of Object.keys(waiter.answers)) {
      delete waiter.answers[id];
    }
    waiter.selections = {};
    const chatIds = new Set(waiter.messageCards.map((item) => item.chatId));
    for (const chatId of chatIds) {
      const selections: UserInputAnswers = {};
      for (const item of waiter.questions) {
        const values = attemptedAnswers[item.id];
        if (item.multiple && values?.length) {
          selections[item.id] = [...values];
        }
      }
      if (Object.keys(selections).length > 0) {
        waiter.selections[chatId] = selections;
      }
    }
    await Promise.all(
      waiter.questions.map((item) =>
        this.patchQuestionCards(requestId, item.id, {
          selectedAnswers: attemptedAnswers[item.id] ?? [],
          answered: false,
        })
      ),
    );
    return "failed";
  }

  async complete(
    requestId: string,
    resolution: UserInputResolution,
  ): Promise<boolean> {
    const waiter = this.waiters.get(requestId);
    if (!waiter) {
      return false;
    }
    clearTimeout(waiter.timer);
    this.waiters.delete(requestId);
    this.submitting.delete(requestId);
    const status = waiter.source === "opencode"
      ? resolution.kind === "answered"
        ? "running"
        : resolution.kind === "rejected"
          ? "waiting"
          : "pending_input"
      : resolution.kind === "answered"
        ? "running"
        : "waiting";
    const session = await this.dependencies.store.upsertSession({
      sessionId: waiter.sessionId,
      cwd: waiter.cwd,
      turnId: waiter.turnId,
      status,
    });
    waiter.resolve?.(resolution);
    const questionCards = waiter.messageCards.map((message) => {
      const questionIndex = waiter.questions.findIndex(
        (question) => question.id === message.questionId,
      );
      const question = questionIndex >= 0
        ? waiter.questions[questionIndex]
        : undefined;
      if (!question) {
        return undefined;
      }
      return {
        messageId: message.messageId,
        card: buildResolvedUserInputQuestionCard(
          session,
          question,
          resolution.kind === "answered"
            ? resolution.answers[question.id]
            : undefined,
          resolution.kind,
          questionIndex,
          waiter.questions.length,
        ),
      };
    }).filter(
      (item): item is { messageId: string; card: Record<string, unknown> } =>
        Boolean(item),
    );
    void Promise.allSettled(
      questionCards.map((item) =>
        this.dependencies.feishu.patchCard(item.messageId, item.card)
      ),
    );
    console.log(`[input] ${resolution.kind} for session #${session.shortId}.`);
    return true;
  }

  async resolveForSession(
    sessionId: string,
    resolution: "local" | "timeout",
  ): Promise<void> {
    const requestIds = [...this.waiters]
      .filter(([, waiter]) => waiter.sessionId === sessionId)
      .map(([requestId]) => requestId);
    for (const requestId of requestIds) {
      await this.complete(requestId, { kind: resolution });
    }
  }

  async resolveAllForShutdown(): Promise<void> {
    for (const requestId of [...this.waiters.keys()]) {
      await this.complete(requestId, { kind: "local" });
    }
  }

  private register(
    requestId: string,
    registration: UserInputRegistration,
    resolve?: (resolution: UserInputResolution) => void,
  ): void {
    const { timeoutMs, ...waiterRegistration } = registration;
    const timer = setTimeout(() => {
      void this.complete(requestId, { kind: "timeout" });
    }, timeoutMs ?? this.dependencies.inputTimeoutMs);
    if (registration.source === "opencode") {
      timer.unref?.();
    }
    this.waiters.set(requestId, {
      ...waiterRegistration,
      questions: [...registration.questions],
      messageCards: [],
      answers: {},
      selections: {},
      timer,
      resolve,
    });
  }

  private async patchQuestionCards(
    requestId: string,
    questionId: string,
    state: {
      selectedAnswers?: readonly string[];
      answered?: boolean;
      remainingQuestions?: number;
    },
    selectionKey?: string,
  ): Promise<void> {
    const waiter = this.waiters.get(requestId);
    const questionIndex =
      waiter?.questions.findIndex((item) => item.id === questionId) ?? -1;
    const question = questionIndex >= 0
      ? waiter?.questions[questionIndex]
      : undefined;
    const session = waiter
      ? this.dependencies.store.getSession(waiter.sessionId)
      : undefined;
    if (!waiter || !question || !session) {
      return;
    }
    const targets = waiter.messageCards.filter(
      (item) =>
        item.questionId === questionId &&
        (selectionKey === undefined || item.chatId === selectionKey),
    );
    await Promise.allSettled(
      targets.map((target) => {
        const selectedAnswers = state.selectedAnswers ??
          waiter.selections[target.chatId]?.[questionId];
        const card = buildUserInputQuestionCard(
          session,
          requestId,
          question,
          questionIndex,
          waiter.questions.length,
          { ...state, selectedAnswers },
          target.chatId,
        );
        return this.dependencies.feishu.patchCard(target.messageId, card);
      }),
    );
  }
}

export function parseUserInputAnswers(
  text: string,
  questions: UserInputQuestion[],
): UserInputAnswers | undefined {
  const parts = questions.length === 1
    ? [text.trim()]
    : text.split(/[；;\n]+/).map((part) => part.trim()).filter(Boolean);
  if (parts.length !== questions.length) {
    return undefined;
  }
  const answers: UserInputAnswers = {};
  for (const [index, question] of questions.entries()) {
    const raw = parts[index]?.trim();
    if (!raw) return undefined;
    const parsed = parseUserInputAnswer(raw, question);
    if (!parsed) return undefined;
    answers[question.id] = parsed;
  }
  return answers;
}

export function inputAnswerUsage(questions: UserInputQuestion[]): string {
  const hasMultiple = questions.some((question) => question.multiple);
  const customAllowed = questions.some((question) => question.custom !== false);
  const detail = `${hasMultiple ? "多选题用逗号分隔选项" : "回复选项编号或文字"}${
    customAllowed ? "，也可填写自定义答案" : ""
  }`;
  return questions.length === 1
    ? `请引用问题卡片，${detail}。`
    : `需要按顺序提供 ${questions.length} 个答案，用中文分号“；”分隔；${detail}。`;
}

function parseUserInputAnswer(
  raw: string,
  question: UserInputQuestion,
): string[] | undefined {
  const exact = matchUserInputOption(raw, question);
  if (exact) {
    return [exact.label];
  }
  if (!question.multiple) {
    return question.custom === false ? undefined : [truncate(raw, 1_000)];
  }
  const tokens = raw.split(/[，,、+]+/u).map((item) => item.trim()).filter(Boolean);
  if (tokens.length === 0) {
    return undefined;
  }
  const selected: string[] = [];
  for (const token of tokens) {
    const option = matchUserInputOption(token, question);
    if (!option && question.custom === false) {
      return undefined;
    }
    const value = truncate(option?.label ?? token, 1_000);
    if (!selected.includes(value)) {
      selected.push(value);
    }
  }
  return selected;
}

function matchUserInputOption(
  raw: string,
  question: UserInputQuestion,
): UserInputQuestion["options"][number] | undefined {
  const numeric = Number.parseInt(raw, 10);
  return /^\d+$/.test(raw) && numeric >= 1
    ? question.options[numeric - 1]
    : question.options.find(
        (candidate) =>
          candidate.label.toLocaleLowerCase("zh-CN") ===
            raw.toLocaleLowerCase("zh-CN"),
      );
}
