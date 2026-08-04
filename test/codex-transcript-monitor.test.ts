import assert from "node:assert/strict";
import { appendFile, mkdtemp, rename, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import {
  CodexTranscriptMonitor,
  type CodexTranscriptErrorEvent,
} from "../src/codex-transcript-monitor.js";

test("reports only newly appended structured Codex errors", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-transcript-monitor-"));
  const transcriptPath = path.join(directory, "rollout.jsonl");
  const events: CodexTranscriptErrorEvent[] = [];
  const monitor = new CodexTranscriptMonitor(async (event) => {
    events.push(event);
  }, 10);
  try {
    await writeFile(
      transcriptPath,
      `${taskCompleteError("old-turn")}\n`,
      "utf8",
    );
    assert.equal(await monitor.watch("session-1", transcriptPath), true);
    await monitor.checkNow();
    assert.equal(events.length, 0);

    const next = taskCompleteError("new-turn");
    const splitAt = Math.floor(next.length / 2);
    await appendFile(transcriptPath, next.slice(0, splitAt), "utf8");
    await monitor.checkNow();
    assert.equal(events.length, 0);

    await appendFile(transcriptPath, `${next.slice(splitAt)}\n`, "utf8");
    await monitor.checkNow();
    assert.equal(events.length, 1);
    assert.equal(events[0]?.turnId, "new-turn");
    assert.equal(events[0]?.errorCode, "internal_server_error");
    assert.match(events[0]?.error ?? "", /high demand/i);

    await monitor.checkNow();
    assert.equal(events.length, 1);
    await monitor.unwatch("session-1");
    await appendFile(transcriptPath, `${taskCompleteError("ignored-turn")}\n`, "utf8");
    await monitor.checkNow();
    assert.equal(events.length, 1);
  } finally {
    await monitor.close();
    await rm(directory, { recursive: true, force: true });
  }
});

test("preserves partial UTF-8 and scans once more when a session is unwatched", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-transcript-monitor-"));
  const transcriptPath = path.join(directory, "rollout.jsonl");
  const events: CodexTranscriptErrorEvent[] = [];
  const monitor = new CodexTranscriptMonitor(async (event) => {
    events.push(event);
  }, 10);
  try {
    await writeFile(transcriptPath, "", "utf8");
    await monitor.watch("session-2", transcriptPath);
    const entry = Buffer.from(
      `${taskCompleteError("utf8-turn", "服务繁忙，请稍后重试。")}\n`,
      "utf8",
    );
    const characterStart = entry.indexOf(Buffer.from("繁", "utf8"));
    assert.notEqual(characterStart, -1);
    await appendFile(transcriptPath, entry.subarray(0, characterStart + 1));
    await monitor.checkNow();
    assert.equal(events.length, 0);

    await appendFile(transcriptPath, entry.subarray(characterStart + 1));
    await monitor.unwatch("session-2");
    assert.equal(events.length, 1);
    assert.equal(events[0]?.turnId, "utf8-turn");
    assert.equal(events[0]?.error, "服务繁忙，请稍后重试。");
  } finally {
    await monitor.close();
    await rm(directory, { recursive: true, force: true });
  }
});

test("detects a transcript replaced by an equal-or-larger file", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-transcript-monitor-"));
  const transcriptPath = path.join(directory, "rollout.jsonl");
  const replacementPath = path.join(directory, "replacement.jsonl");
  const events: CodexTranscriptErrorEvent[] = [];
  const monitor = new CodexTranscriptMonitor(async (event) => {
    events.push(event);
  }, 60_000);
  try {
    await writeFile(transcriptPath, `${"x".repeat(80)}\n`, "utf8");
    await monitor.watch("session-replaced", transcriptPath);
    await writeFile(
      replacementPath,
      `${taskCompleteError("replacement-turn", "replacement failure")}\n`,
      "utf8",
    );
    await rm(transcriptPath);
    await rename(replacementPath, transcriptPath);

    await monitor.checkNow();
    assert.deepEqual(events.map((event) => event.turnId), ["replacement-turn"]);
  } finally {
    await monitor.close();
    await rm(directory, { recursive: true, force: true });
  }
});

test("unwatch and close perform a fresh scan after an in-flight scan", async (context) => {
  for (const action of ["unwatch", "close"] as const) {
    await context.test(action, async () => {
      const directory = await mkdtemp(path.join(os.tmpdir(), "codex-transcript-monitor-"));
      const transcriptPath = path.join(directory, "rollout.jsonl");
      const events: CodexTranscriptErrorEvent[] = [];
      let releaseFirst: (() => void) | undefined;
      const firstHandled = new Promise<void>((resolve) => {
        releaseFirst = resolve;
      });
      let notifyFirstStarted: (() => void) | undefined;
      const firstStarted = new Promise<void>((resolve) => {
        notifyFirstStarted = resolve;
      });
      const monitor = new CodexTranscriptMonitor(async (event) => {
        events.push(event);
        if (event.turnId === "first-turn") {
          notifyFirstStarted?.();
          await firstHandled;
        }
      }, 60_000);
      try {
        await writeFile(transcriptPath, "", "utf8");
        await monitor.watch(`session-${action}`, transcriptPath);
        await appendFile(transcriptPath, `${taskCompleteError("first-turn")}\n`, "utf8");
        const inFlightScan = monitor.checkNow();
        await firstStarted;

        const stopping = action === "unwatch"
          ? monitor.unwatch(`session-${action}`)
          : monitor.close();
        await appendFile(transcriptPath, `${taskCompleteError("final-turn")}\n`, "utf8");
        releaseFirst?.();
        await Promise.all([inFlightScan, stopping]);

        assert.deepEqual(
          events.map((event) => event.turnId),
          ["first-turn", "final-turn"],
        );
      } finally {
        releaseFirst?.();
        await monitor.close();
        await rm(directory, { recursive: true, force: true });
      }
    });
  }
});

function taskCompleteError(
  turnId: string,
  message = "We're currently experiencing high demand, which may cause temporary errors.",
): string {
  return JSON.stringify({
    type: "event_msg",
    payload: {
      type: "task_complete",
      turn_id: turnId,
      last_agent_message: null,
      error: {
        message,
        codex_error_info: "internal_server_error",
      },
    },
  });
}
