import type { RuntimeName } from "../domain.js";
import {
  isBridgeEnvelope,
  isNonEmptyString,
  isOptionalString,
  isRecord,
} from "./envelope-validation.js";
import type { BridgeProtocolVersion } from "./protocol-version.js";

export type RuntimePromptMode = "steer" | "queue";
export type RuntimeApprovalDecision =
  | "allow_once"
  | "allow_session"
  | "deny";

export interface RuntimeCommandPayloadMap {
  "prompt.send": {
    prompt: string;
    mode: RuntimePromptMode;
  };
  "approval.resolve": {
    requestId: string;
    decision: RuntimeApprovalDecision;
  };
  "input.resolve": {
    requestId: string;
    answers: Record<string, string | string[]>;
  };
  "session.launch": {
    cwd: string;
    prompt?: string;
    elevated?: boolean;
  };
  "session.resume": {
    prompt?: string;
  };
  "session.stop": {
    reason?: string;
  };
}

export type RuntimeCommandType = keyof RuntimeCommandPayloadMap;

export const runtimeCommandTypes: readonly RuntimeCommandType[] = [
  "prompt.send",
  "approval.resolve",
  "input.resolve",
  "session.launch",
  "session.resume",
  "session.stop",
];

export interface RuntimeCommandEnvelope<
  TType extends RuntimeCommandType,
  TPayload extends RuntimeCommandPayloadMap[TType] =
    RuntimeCommandPayloadMap[TType],
> {
  protocolVersion: BridgeProtocolVersion;
  commandId: string;
  commandType: TType;
  createdAt: string;
  runtime: RuntimeName;
  session: {
    externalId: string;
    cwd?: string;
  };
  traceId: string;
  correlationId?: string;
  payload: TPayload;
}

export type RuntimeCommand = {
  [TType in RuntimeCommandType]: RuntimeCommandEnvelope<TType>;
}[RuntimeCommandType];

export function isRuntimeCommand(value: unknown): value is RuntimeCommand {
  if (
    !isBridgeEnvelope(value) ||
    !isNonEmptyString(value.commandId) ||
    !isNonEmptyString(value.createdAt) ||
    !isNonEmptyString(value.commandType) ||
    !isRecord(value.payload)
  ) {
    return false;
  }
  const payload = value.payload;
  switch (value.commandType) {
    case "prompt.send":
      return (
        isNonEmptyString(payload.prompt) &&
        (payload.mode === "steer" || payload.mode === "queue")
      );
    case "approval.resolve":
      return (
        isNonEmptyString(payload.requestId) &&
        (payload.decision === "allow_once" ||
          payload.decision === "allow_session" ||
          payload.decision === "deny")
      );
    case "input.resolve":
      return (
        isNonEmptyString(payload.requestId) &&
        isRecord(payload.answers) &&
        Object.values(payload.answers).every(
          (answer) =>
            typeof answer === "string" ||
            (Array.isArray(answer) &&
              answer.every((item) => typeof item === "string")),
        )
      );
    case "session.launch":
      return (
        isNonEmptyString(payload.cwd) &&
        isOptionalString(payload.prompt) &&
        (payload.elevated === undefined || typeof payload.elevated === "boolean")
      );
    case "session.resume":
      return isOptionalString(payload.prompt);
    case "session.stop":
      return isOptionalString(payload.reason);
    default:
      return false;
  }
}
