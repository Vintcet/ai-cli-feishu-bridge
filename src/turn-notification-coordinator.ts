import { createHash } from "node:crypto";

import type { MessageRouteKind, SessionRecord } from "./domain.js";
import { FeishuGateway } from "./feishu.js";
import type { NotificationRecipient } from "./session-group-coordinator.js";
import { BridgeStore } from "./store.js";

export interface TurnNotificationDelivery {
  sentCount: number;
  messageIds: string[];
}

interface TurnNotificationCoordinatorDependencies {
  store: BridgeStore;
  feishu: FeishuGateway;
  recipients: (session: SessionRecord) => Promise<NotificationRecipient[]>;
  addRoute: (
    messageId: string,
    sessionId: string,
    chatId: string,
    kind: MessageRouteKind,
  ) => Promise<void>;
}

export class TurnNotificationCoordinator {
  private readonly inFlight = new Set<string>();

  constructor(
    private readonly dependencies: TurnNotificationCoordinatorDependencies,
  ) {}

  async send(
    session: SessionRecord,
    turnId: string,
    kind: "stop" | "error",
    notificationMessage: string,
    cards: Record<string, unknown>[],
    failureMessage: string,
  ): Promise<TurnNotificationDelivery> {
    const notificationKey = `${session.sessionId}\u0000${turnId}`;
    if (this.inFlight.has(notificationKey)) {
      return { sentCount: 0, messageIds: [] };
    }
    this.inFlight.add(notificationKey);
    try {
      if (
        !(await this.dependencies.store.claimTurnNotification(
          session.sessionId,
          turnId,
          kind,
          notificationMessage,
        ))
      ) {
        return { sentCount: 0, messageIds: [] };
      }
      const recipients = await this.dependencies.recipients(session);
      if (recipients.length === 0) {
        await this.dependencies.store.releaseTurnNotification(
          session.sessionId,
          turnId,
        );
        return { sentCount: 0, messageIds: [] };
      }
      let sentCount = 0;
      const messageIds: string[] = [];
      for (const recipient of recipients) {
        try {
          for (const [cardIndex, card] of cards.entries()) {
            const messageId = await this.dependencies.feishu.sendCard(
              recipient.chatId,
              card,
              idempotencyKey(
                session.sessionId,
                turnId,
                kind,
                recipient.chatId,
                cardIndex,
              ),
            );
            sentCount += 1;
            messageIds.push(messageId);
            await this.dependencies.addRoute(
              messageId,
              session.sessionId,
              recipient.chatId,
              kind,
            );
          }
        } catch (error) {
          console.error(failureMessage, error);
        }
      }
      if (sentCount === recipients.length * cards.length) {
        await this.dependencies.store.completeTurnNotification(
          session.sessionId,
          turnId,
        );
      }
      return { sentCount, messageIds };
    } finally {
      this.inFlight.delete(notificationKey);
    }
  }
}

export function turnNotificationWasSent(
  session: SessionRecord | undefined,
  turnId: string,
): boolean {
  return session?.lastNotificationTurnId === turnId &&
    session.lastNotificationStatus !== "pending";
}

function idempotencyKey(
  sessionId: string,
  turnId: string,
  kind: "stop" | "error",
  chatId: string,
  cardIndex: number,
): string {
  return createHash("sha256")
    .update(
      `${sessionId}\u0000${turnId}\u0000${kind}\u0000${chatId}\u0000${cardIndex}`,
    )
    .digest("hex")
    .slice(0, 32);
}
