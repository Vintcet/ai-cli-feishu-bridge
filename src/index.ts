import * as Lark from "@larksuiteoapi/node-sdk";

import { BridgeController } from "./bridge-controller.js";
import { CodexRunner } from "./codex-runner.js";
import { bridgeConfig } from "./config.js";
import { FeishuGateway } from "./feishu.js";
import { startHookHttpServer } from "./http-server.js";
import { ManagedTerminalRouter } from "./managed-terminal.js";
import { BridgeStore } from "./store.js";

type FeishuEvent = Record<string, any>;

if (!bridgeConfig.appId || !bridgeConfig.appSecret) {
  console.error(
    "Missing FEISHU_APP_ID or FEISHU_APP_SECRET. Fill codex-feishu-bridge/.env first.",
  );
  process.exit(1);
}

const store = new BridgeStore(bridgeConfig.dataDirectory);
await store.init();
const controlToken = await store.getOrCreateControlToken();

const feishu = new FeishuGateway(bridgeConfig.appId, bridgeConfig.appSecret);
const codex = new CodexRunner(bridgeConfig.codexCommand);
const managedTerminals = new ManagedTerminalRouter();
const controller = new BridgeController(store, feishu, codex, managedTerminals, {
  bindCommand: bridgeConfig.bindCommand,
  approvalTimeoutMs: bridgeConfig.approvalTimeoutMs,
  inputTimeoutMs: bridgeConfig.inputTimeoutMs,
  sessionActiveMs: bridgeConfig.sessionActiveMs,
  uploadsDirectory: bridgeConfig.uploadsDirectory,
  inboundFileMaxBytes: bridgeConfig.inboundFileMaxBytes,
  inboundAttachmentMaxCount: bridgeConfig.inboundAttachmentMaxCount,
  uploadTtlMs: bridgeConfig.uploadTtlMs,
  outboundFileMaxBytes: bridgeConfig.outboundFileMaxBytes,
});

const eventDispatcher = new Lark.EventDispatcher({}).register({
  "im.message.receive_v1": async (data: FeishuEvent) => {
    try {
      await controller.handleFeishuMessage(data);
    } catch (error) {
      console.error("Failed to handle a Feishu message:", error);
    }
  },
  "card.action.trigger": async (data: FeishuEvent) => {
    try {
      return await controller.handleCardAction(data);
    } catch (error) {
      console.error("Failed to handle a Feishu card action:", error);
      return {
        toast: { type: "error", content: "操作处理失败，请回到电脑端确认。" },
      };
    }
  },
});

const wsClient = new Lark.WSClient({
  appId: bridgeConfig.appId,
  appSecret: bridgeConfig.appSecret,
  loggerLevel: Lark.LoggerLevel.info,
  handshakeTimeoutMs: 15_000,
  onReady: () => console.log("[ws] Feishu connected."),
  onReconnecting: () => console.warn("[ws] Feishu connection lost; reconnecting."),
  onReconnected: () => console.log("[ws] Feishu reconnected."),
  onError: (error) => console.error("[ws] Feishu connection failed:", error),
});

const serviceStartedAt = new Date().toISOString();
const hookServer = startHookHttpServer(
  bridgeConfig.httpHost,
  bridgeConfig.httpPort,
  {
    health: () => ({
      ...controller.health(),
      version: "0.11.5",
      processId: process.pid,
      startedAt: serviceStartedAt,
      feishu: wsClient.getConnectionStatus(),
    }),
    managedTerminalRegister: (payload) =>
      controller.handleManagedTerminalRegistration(payload),
    managedTerminalUnregister: (payload) =>
      controller.handleManagedTerminalUnregistration(payload),
    sessionAlias: (payload) => controller.handleSessionAliasUpdate(payload),
    sessionHistoryHide: (payload) => controller.handleSessionHistoryHide(payload),
    localApproval: (payload) => controller.handleLocalApproval(payload),
    settingsUpdate: (payload) => controller.handleSettingsUpdate(payload),
    sessionStart: (payload) => controller.handleSessionStartHook(payload),
    sessionEnd: (payload) => controller.handleSessionEndHook(payload),
    permission: (payload) => controller.handlePermissionHook(payload),
    requestUserInput: (payload) => controller.handleRequestUserInputHook(payload),
    activity: (payload) => controller.handleActivityHook(payload),
    stop: (payload) => controller.handleStopHook(payload),
  },
  controlToken,
);

process.on("unhandledRejection", (error) => {
  console.error("Unhandled rejection:", error);
});

process.on("SIGINT", () => {
  wsClient.close({ force: true });
  hookServer.close(() => process.exit(0));
});

process.on("SIGTERM", () => {
  wsClient.close({ force: true });
  hookServer.close(() => process.exit(0));
});

console.log(`Starting Feishu long connection for app ${bridgeConfig.appId.slice(0, 8)}...`);
console.log(
  `Commands: “${bridgeConfig.bindCommand}”, “状态”, “会话”, “别名”, “排队”, “发文件”, “帮助”. Multiple Codex windows are routed by alias or session id.`,
);

void wsClient.start({ eventDispatcher });
