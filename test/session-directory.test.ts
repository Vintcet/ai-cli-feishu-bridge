import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import { ManagedTerminalRouter } from "../src/managed-terminal.js";
import { SessionDirectory } from "../src/session-directory.js";
import type { SessionGroupCoordinator } from "../src/session-group-coordinator.js";
import { BridgeStore } from "../src/store.js";

test("forced refresh waits for a fresh external process snapshot", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-refresh-"));
  const store = new BridgeStore(directory);
  try {
    await store.init();
    await store.upsertSession({
      sessionId: "external-refresh-session",
      cwd: directory,
      status: "waiting",
      runtime: "codex",
      clientProcessId: 424_242,
      historyEligible: true,
    });
    let forcedScans = 0;
    const sessions = new SessionDirectory({
      store,
      managedTerminals: new ManagedTerminalRouter(),
      sessionActiveMs: 24 * 60 * 60 * 1_000,
      sessionGroups: {} as SessionGroupCoordinator,
      liveClientProcessIds: () => new Set([424_242]),
      refreshLiveClientProcessIds: async () => {
        forcedScans += 1;
        return new Set();
      },
      queuedPromptCount: () => 0,
      respond: async () => undefined,
    });

    assert.deepEqual(
      sessions.listActive().map((session) => session.sessionId),
      ["external-refresh-session"],
    );
    assert.deepEqual(await sessions.refreshActive(), []);
    assert.equal(forcedScans, 1);
    assert.deepEqual(
      store.listHistorySessions().map((session) => session.sessionId),
      ["external-refresh-session"],
    );
  } finally {
    await store.close();
    await rm(directory, { recursive: true, force: true });
  }
});
