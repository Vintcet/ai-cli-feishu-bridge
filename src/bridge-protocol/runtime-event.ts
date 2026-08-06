import type { RuntimeName } from "../domain.js";
import {
  isBridgeEnvelope,
  isNonEmptyString,
  isOptionalString,
  isRecord,
} from "./envelope-validation.js";
import type { BridgeProtocolVersion } from "./protocol-version.js";

export interface RuntimeEventPayloadMap {
  "session.started": { model?: string };
  "session.ended": { reason?: string };
  "turn.started": { turnId?: string };
  "turn.activity": { turnId?: string; summary: string };
  "turn.completed": { turnId?: string; message?: string };
  "turn.failed": { turnId?: string; error: string; code?: string };
  "approval.requested": {
    requestId: string;
    title: string;
    description?: string;
    expiresAt: string;
  };
  "approval.resolved_externally": {
    requestId: string;
    outcome: "allowed" | "denied" | "cancelled";
  };
  "input.requested": {
    requestId: string;
    questions: Array<{
      id: string;
      prompt: string;
      options?: string[];
      multiple?: boolean;
      allowsCustom?: boolean;
    }>;
    expiresAt: string;
  };
  "input.resolved_externally": { requestId: string };
  "runtime.connected": { endpoint?: string };
  "runtime.disconnected": { reason?: string };
}

export type RuntimeEventType = keyof RuntimeEventPayloadMap;

export const runtimeEventTypes: readonly RuntimeEventType[] = [
  "session.started",
  "session.ended",
  "turn.started",
  "turn.activity",
  "turn.completed",
  "turn.failed",
  "approval.requested",
  "approval.resolved_externally",
  "input.requested",
  "input.resolved_externally",
  "runtime.connected",
  "runtime.disconnected",
];

export interface RuntimeEventEnvelope<
  TType extends RuntimeEventType,
  TPayload extends RuntimeEventPayloadMap[TType] = RuntimeEventPayloadMap[TType],
> {
  protocolVersion: BridgeProtocolVersion;
  eventId: string;
  eventType: TType;
  occurredAt: string;
  runtime: RuntimeName;
  session: {
    externalId: string;
    cwd?: string;
  };
  traceId: string;
  correlationId?: string;
  payload: TPayload;
}

export type RuntimeEvent = {
  [TType in RuntimeEventType]: RuntimeEventEnvelope<TType>;
}[RuntimeEventType];

export function isRuntimeEvent(value: unknown): value is RuntimeEvent {
  if (
    !isBridgeEnvelope(value) ||
    !isNonEmptyString(value.eventId) ||
    !isNonEmptyString(value.occurredAt) ||
    !isNonEmptyString(value.eventType) ||
    !isRecord(value.payload)
  ) {
    return false;
  }
  const payload = value.payload;
  switch (value.eventType) {
    case "session.started":
      return isOptionalString(payload.model);
    case "session.ended":
    case "runtime.disconnected":
      return isOptionalString(payload.reason);
    case "turn.started":
      return isOptionalString(payload.turnId);
    case "turn.activity":
      return (
        isOptionalString(payload.turnId) &&
        isNonEmptyString(payload.summary)
      );
    case "turn.completed":
      return (
        isOptionalString(payload.turnId) && isOptionalString(payload.message)
      );
    case "turn.failed":
      return (
        isOptionalString(payload.turnId) &&
        isNonEmptyString(payload.error) &&
        isOptionalString(payload.code)
      );
    case "approval.requested":
      return (
        isNonEmptyString(payload.requestId) &&
        isNonEmptyString(payload.title) &&
        isOptionalString(payload.description) &&
        isTimestamp(payload.expiresAt)
      );
    case "approval.resolved_externally":
      return (
        isNonEmptyString(payload.requestId) &&
        (payload.outcome === "allowed" ||
          payload.outcome === "denied" ||
          payload.outcome === "cancelled")
      );
    case "input.requested":
      return (
        isNonEmptyString(payload.requestId) &&
        Array.isArray(payload.questions) &&
        payload.questions.every(isRuntimeQuestion) &&
        isTimestamp(payload.expiresAt)
      );
    case "input.resolved_externally":
      return isNonEmptyString(payload.requestId);
    case "runtime.connected":
      return isOptionalString(payload.endpoint);
    default:
      return false;
  }
}

function isRuntimeQuestion(value: unknown): boolean {
  if (
    !isRecord(value) ||
    !isNonEmptyString(value.id) ||
    !isNonEmptyString(value.prompt)
  ) {
    return false;
  }
  return (
    (value.options === undefined ||
      (Array.isArray(value.options) &&
        value.options.every((option) => typeof option === "string"))) &&
    (value.multiple === undefined || typeof value.multiple === "boolean") &&
    (value.allowsCustom === undefined || typeof value.allowsCustom === "boolean")
  );
}

function isTimestamp(value: unknown): boolean {
  return typeof value === "string" && Number.isFinite(Date.parse(value));
}
