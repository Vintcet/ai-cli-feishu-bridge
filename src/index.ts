import * as Lark from "@larksuiteoapi/node-sdk";

import { BridgeController } from "./bridge-controller.js";
import { CodexRunner } from "./codex-runner.js";
import { bridgeConfig } from "./config.js";
import { FeishuGateway } from "./feishu.js";
import { startHookHttpServer } from "./http-server.js";
import { ManagedTerminalRouter } from "./managed-terminal.js";
import { OpenCodeManager } from "./opencode-manager.js";
import { BridgeStore } from "./store.js";
import { bridgeVersion } from "./version.js";

type FeishuEvent = Record<string, any>;

if (!bridgeConfig.appId || !bridgeConfig.appSecret) {
  console.error(
    "Missing FEISHU_APP_ID or FEISHU_APP_SECRET. Fill codex-feishu-bridge/.env first.",
  );
  process.exit(1);
}

const store = new BridgeStore(bridgeConfig.dataDirectory, {
  defaultWorkspaceRoot: bridgeConfig.defaultWorkspaceRoot,
});
await store.init();
const controlToken = await store.getOrCreateControlToken();

const feishu = new FeishuGateway(bridgeConfig.appId, bridgeConfig.appSecret);
const codex = new CodexRunner(bridgeConfig.codexCommand);
const managedTerminals = new ManagedTerminalRouter();
const opencode = new OpenCodeManager(
  {
    onInstanceConnected: (port, cwd) => {
      console.log(`[opencode] Connected to instance on port ${port} (${cwd}).`);
    },
    onInstanceDisconnected: (port) => {
      console.warn(`[opencode] Instance on port ${port} disconnected.`);
      void controller.handleOpenCodeInstanceDisconnected(port).catch((error) => {
        console.error("[opencode] Could not finalize disconnected instance:", error);
      });
    },
    eventHandlers: {
      onSessionCreated: (session) => {
        void controller.handleOpenCodeSessionCreated(session).catch((error) => {
          console.error("[opencode] Could not register session:", error);
        });
      },
      onSessionUpdated: (session) => {
        void controller.handleOpenCodeSessionCreated(session).catch((error) => {
          console.error("[opencode] Could not update session:", error);
        });
      },
      onSessionIdle: (sessionId) => {
        void controller.handleOpenCodeSessionIdle(sessionId).catch((error) => {
          console.error("[opencode] Could not handle session idle:", error);
        });
      },
      onSessionError: (sessionId, error) => {
        void controller.handleOpenCodeSessionError(sessionId, error).catch((failure) => {
          console.error("[opencode] Could not handle session error:", failure);
        });
      },
      onSessionDeleted: (sessionId) => {
        void controller.handleOpenCodeSessionDeleted(sessionId).catch((error) => {
          console.error("[opencode] Could not handle session deletion:", error);
        });
      },
      onSessionStatus: (sessionId, status) => {
        void controller.handleOpenCodeSessionStatus(sessionId, status).catch((error) => {
          console.error("[opencode] Could not handle session status:", error);
        });
      },
      onSessionCompacted: (sessionId) => {
        void controller.handleOpenCodeSessionCompacted(sessionId).catch((error) => {
          console.error("[opencode] Could not handle session compaction:", error);
        });
      },
      onPermissionAsked: (permission) => {
        void controller.handleOpenCodePermissionUpdated(permission).catch((error) => {
          console.error("[opencode] Could not handle permission request:", error);
        });
      },
      onPermissionUpdated: (permission) => {
        void controller.handleOpenCodePermissionUpdated(permission).catch((error) => {
          console.error("[opencode] Could not handle legacy permission update:", error);
        });
      },
      onPermissionReplied: (reply) => {
        void controller.handleOpenCodePermissionReplied(reply).catch((error) => {
          console.error("[opencode] Could not handle permission reply:", error);
        });
      },
      onQuestionAsked: (request) => {
        void controller.handleOpenCodeQuestionAsked(request).catch((error) => {
          console.error("[opencode] Could not handle question request:", error);
        });
      },
      onQuestionReplied: (reply) => {
        void controller.handleOpenCodeQuestionReplied(reply).catch((error) => {
          console.error("[opencode] Could not handle question reply:", error);
        });
      },
      onQuestionRejected: (rejection) => {
        void controller.handleOpenCodeQuestionRejected(rejection).catch((error) => {
          console.error("[opencode] Could not handle question rejection:", error);
        });
      },
      onMessagePartUpdated: (properties) => {
        void controller.handleOpenCodeMessagePartUpdated(properties).catch((error) => {
          console.error("[opencode] Could not handle message part update:", error);
        });
      },
      onMessageUpdated: (message) => {
        void controller.handleOpenCodeMessageUpdated(message).catch((error) => {
          console.error("[opencode] Could not handle message update:", error);
        });
      },
      onDisconnected: () => {
        // A per-instance disconnect is delivered by the manager's
        // onInstanceDisconnected callback above.
      },
    },
  },
  {
    autoDiscover: bridgeConfig.opencodeAutoDiscover,
    scanIntervalMs: bridgeConfig.opencodeAutoDiscoverIntervalMs,
  },
);
const controller = new BridgeController(store, feishu, codex, managedTerminals, opencode, {
  bindCommand: bridgeConfig.bindCommand,
  approvalTimeoutMs: bridgeConfig.approvalTimeoutMs,
  inputTimeoutMs: bridgeConfig.inputTimeoutMs,
  sessionActiveMs: bridgeConfig.sessionActiveMs,
  sessionGroupInactiveMs: bridgeConfig.sessionGroupInactiveMs,
  runtimeLaunchTimeoutMs: bridgeConfig.runtimeLaunchTimeoutMs,
  uploadsDirectory: bridgeConfig.uploadsDirectory,
  inboundFileMaxBytes: bridgeConfig.inboundFileMaxBytes,
  inboundAttachmentMaxCount: bridgeConfig.inboundAttachmentMaxCount,
  uploadMaxFiles: bridgeConfig.uploadMaxFiles,
  uploadMaxBytes: bridgeConfig.uploadMaxBytes,
  uploadTtlMs: bridgeConfig.uploadTtlMs,
  outboundFileMaxBytes: bridgeConfig.outboundFileMaxBytes,
  transcriptPollIntervalMs: bridgeConfig.transcriptPollIntervalMs,
  approvalLogPath: bridgeConfig.approvalLogPath,
  approvalLogMaxBytes: bridgeConfig.approvalLogMaxBytes,
  approvalLogMaxBackups: bridgeConfig.approvalLogMaxBackups,
});

opencode.startAutoDiscovery();

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
    health: (includeLocalSecrets) => ({
      ...controller.health(includeLocalSecrets),
      version: bridgeVersion,
      processId: process.pid,
      startedAt: serviceStartedAt,
      feishu: wsClient.getConnectionStatus(),
    }),
    shutdown: () => shutdown(),
    managedTerminalRegister: (payload) =>
      controller.handleManagedTerminalRegistration(payload),
    managedTerminalUnregister: (payload) =>
      controller.handleManagedTerminalUnregistration(payload),
    sessionAlias: (payload) => controller.handleSessionAliasUpdate(payload),
    sessionGroupRetry: (payload) => controller.handleSessionGroupRetry(payload),
    sessionHistoryHide: (payload) => controller.handleSessionHistoryHide(payload),
    runtimeLaunchClaim: () => controller.handleRuntimeLaunchClaim(),
    runtimeLaunchComplete: (payload) => controller.handleRuntimeLaunchComplete(payload),
    localApproval: (payload) => controller.handleLocalApproval(payload),
    settingsUpdate: (payload) => controller.handleSettingsUpdate(payload),
    sessionStart: (payload) => controller.handleSessionStartHook(payload),
    sessionEnd: (payload) => controller.handleSessionEndHook(payload),
    permission: (payload) => controller.handlePermissionHook(payload),
    requestUserInput: (payload) => controller.handleRequestUserInputHook(payload),
    activity: (payload) => controller.handleActivityHook(payload),
    stop: (payload) => controller.handleStopHook(payload),
    opencodeLaunch: (payload) => controller.handleOpenCodeLaunch(payload),
    opencodeRegister: (payload) => controller.handleOpenCodeRegister(payload),
    opencodeUnregister: (payload) => controller.handleOpenCodeUnregister(payload),
  },
  controlToken,
);

process.on("unhandledRejection", (error) => {
  console.error("Unhandled rejection:", error);
});

console.log(`Starting Feishu long connection for app ${bridgeConfig.appId.slice(0, 8)}...`);
console.log(
  `Commands: “${bridgeConfig.bindCommand}”, “新建 codex 项目名”, “工作区”, “状态”, “会话”, “别名”, “排队”, “发文件”, “帮助”. Multiple assistant windows are routed by alias or session id.`,
);

void controller.initialize()
  .then(() => controller.cleanupInactiveSessionGroups())
  .catch((error) => {
    console.warn("[feishu] Could not initialize or clean up session groups:", error);
  });

const sessionGroupCleanupTimer = setInterval(() => {
  void controller.cleanupInactiveSessionGroups().catch((error) => {
    console.warn("[feishu] Could not clean up inactive session groups:", error);
  });
}, bridgeConfig.sessionGroupCleanupIntervalMs);
sessionGroupCleanupTimer.unref?.();

let shuttingDown = false;
const shutdown = (): void => {
  if (shuttingDown) return;
  shuttingDown = true;
  wsClient.close({ force: true });
  opencode.stopAutoDiscovery();
  clearInterval(sessionGroupCleanupTimer);
  hookServer.close(() => {
    void Promise.allSettled([controller.close(), codex.close(), store.close()])
      .finally(() => process.exit(0));
  });
};

process.once("SIGINT", shutdown);
process.once("SIGTERM", shutdown);

void wsClient.start({ eventDispatcher });
