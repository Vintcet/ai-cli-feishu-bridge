import { isRuntimeName, type RuntimeName } from "../domain.js";
import {
  BRIDGE_PROTOCOL_VERSION,
  type BridgeProtocolVersion,
} from "./protocol-version.js";

export interface BridgeEnvelopeFields {
  protocolVersion: BridgeProtocolVersion;
  runtime: RuntimeName;
  session: {
    externalId: string;
    cwd?: string;
  };
  traceId: string;
  correlationId?: string;
}

export function isBridgeEnvelope(
  value: unknown,
): value is BridgeEnvelopeFields & Record<string, unknown> {
  if (!isRecord(value) || value.protocolVersion !== BRIDGE_PROTOCOL_VERSION) {
    return false;
  }
  if (!isRuntimeName(value.runtime) || !isNonEmptyString(value.traceId)) {
    return false;
  }
  if (
    value.correlationId !== undefined &&
    !isNonEmptyString(value.correlationId)
  ) {
    return false;
  }
  const session = value.session;
  return (
    isRecord(session) &&
    isNonEmptyString(session.externalId) &&
    (session.cwd === undefined || typeof session.cwd === "string")
  );
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function isNonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

export function isOptionalString(value: unknown): value is string | undefined {
  return value === undefined || typeof value === "string";
}
