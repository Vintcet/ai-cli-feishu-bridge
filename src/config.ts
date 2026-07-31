import "dotenv/config";

import path from "node:path";
import { fileURLToPath } from "node:url";

const moduleDirectory = path.dirname(fileURLToPath(import.meta.url));
export const projectRoot = path.resolve(moduleDirectory, "..");

function positiveInteger(value: string | undefined, fallback: number): number {
  const parsed = Number.parseInt(value ?? "", 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

export const bridgeConfig = {
  appId: process.env.FEISHU_APP_ID?.trim() ?? "",
  appSecret: process.env.FEISHU_APP_SECRET?.trim() ?? "",
  bindCommand: process.env.FEISHU_BIND_COMMAND?.trim() || "绑定",
  httpHost: "127.0.0.1",
  httpPort: positiveInteger(process.env.BRIDGE_HTTP_PORT, 8765),
  approvalTimeoutMs: positiveInteger(
    process.env.CODEX_APPROVAL_TIMEOUT_MS,
    20 * 60 * 1000,
  ),
  inputTimeoutMs: positiveInteger(
    process.env.CODEX_INPUT_TIMEOUT_MS,
    20 * 60 * 1000,
  ),
  sessionActiveMs: positiveInteger(
    process.env.CODEX_SESSION_ACTIVE_MS,
    24 * 60 * 60 * 1000,
  ),
  codexCommand: process.env.CODEX_COMMAND?.trim() || "codex",
  dataDirectory: path.join(projectRoot, "data"),
  uploadsDirectory: path.join(projectRoot, "data", "uploads"),
  inboundFileMaxBytes: positiveInteger(
    process.env.FEISHU_INBOUND_FILE_MAX_BYTES,
    25 * 1024 * 1024,
  ),
  inboundAttachmentMaxCount: positiveInteger(
    process.env.FEISHU_INBOUND_ATTACHMENT_MAX_COUNT,
    4,
  ),
  uploadTtlMs: positiveInteger(
    process.env.FEISHU_UPLOAD_TTL_MS,
    7 * 24 * 60 * 60 * 1000,
  ),
  outboundFileMaxBytes: positiveInteger(
    process.env.FEISHU_OUTBOUND_FILE_MAX_BYTES,
    30 * 1024 * 1024,
  ),
  opencodeAutoDiscover: process.env.OPENCODE_AUTO_DISCOVER !== "0",
  opencodeAutoDiscoverIntervalMs: positiveInteger(
    process.env.OPENCODE_AUTO_DISCOVER_INTERVAL_MS,
    20_000,
  ),
};
