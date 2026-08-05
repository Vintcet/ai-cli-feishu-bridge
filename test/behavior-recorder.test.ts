import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import {
  isBehaviorRecord,
  type BehaviorRecord,
} from "../src/migration/behavior-record.js";
import { BehaviorRecorder } from "../src/migration/behavior-recorder.js";
import { BridgeStore } from "../src/store.js";

async function temporaryDirectory(): Promise<string> {
  return await mkdtemp(path.join(os.tmpdir(), "ai-cli-feishu-behavior-"));
}

async function readRecords(filePath: string): Promise<BehaviorRecord[]> {
  return (await readFile(filePath, "utf8"))
    .trim()
    .split("\n")
    .map((line) => JSON.parse(line) as BehaviorRecord);
}

test("disabled behavior recording creates no file", async () => {
  const directory = await temporaryDirectory();
  try {
    const filePath = path.join(directory, "recordings", "behavior.jsonl");
    const recorder = new BehaviorRecorder({ enabled: false, filePath });

    recorder.record("core.decision", "disabled", { decision: "ignored" });
    const result = await recorder.capture(
      "ingress.hook",
      "activity",
      { prompt: "private text" },
      () => 42,
    );
    await recorder.close();

    assert.equal(result, 42);
    await assert.rejects(readFile(filePath), { code: "ENOENT" });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("behavior recording removes secrets, text, paths, and identifiers", async () => {
  const directory = await temporaryDirectory();
  try {
    const filePath = path.join(directory, "behavior.jsonl");
    const recorder = new BehaviorRecorder({
      enabled: true,
      filePath,
      recordId: () => "record-safe",
      now: () => new Date("2026-08-06T00:00:00.000Z"),
    });
    recorder.record(
      "ingress.hook",
      "permission",
      {
        authorization: "Bearer raw-secret",
        prompt: "raw private message",
        cwd: "K:\\private\\workspace",
        session_id: "raw-session-id",
        chatId: "raw-chat-id",
        messageId: "raw-message-id",
        ownerOpenId: "raw-owner-open-id",
        requestId: "raw-request-id",
        tool_name: "shell_command",
      },
      {
        runtime: "codex",
        sessionId: "raw-session-id",
        traceId: "raw-trace-id",
      },
    );
    await recorder.close();

    const text = await readFile(filePath, "utf8");
    for (const secret of [
      "raw-secret",
      "raw private message",
      "K:\\private\\workspace",
      "raw-session-id",
      "raw-trace-id",
      "raw-chat-id",
      "raw-message-id",
      "raw-owner-open-id",
      "raw-request-id",
    ]) {
      assert.equal(text.includes(secret), false, secret);
    }
    const [record] = await readRecords(filePath);
    assert.ok(record);
    assert.equal(isBehaviorRecord(record), true);
    assert.equal(record.runtime, "codex");
    assert.match(record.traceId, /^trace:[a-f0-9]{16}$/u);
    assert.match(record.sessionRef ?? "", /^session:[a-f0-9]{16}$/u);
    const observed = record.observed as Record<string, unknown>;
    for (const key of ["chatId", "messageId", "ownerOpenId", "requestId"]) {
      assert.match(String(observed[key]), /^id:[a-f0-9]{16}$/u, key);
    }
    assert.equal(
      observed.authorization,
      "[redacted]",
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("nested records share a trace and capture both success and failure", async () => {
  const directory = await temporaryDirectory();
  try {
    const filePath = path.join(directory, "behavior.jsonl");
    let nextId = 0;
    const recorder = new BehaviorRecorder({
      enabled: true,
      filePath,
      recordId: () => `record-${nextId += 1}`,
    });

    const result = await recorder.capture(
      "ingress.feishu",
      "message.receive",
      { message_id: "message-1" },
      () => {
        recorder.record("core.decision", "prompt.route", { decision: "steer" });
        return "ok";
      },
      { runtime: "claudecode", sessionId: "session-1" },
    );
    await assert.rejects(
      recorder.capture(
        "egress.runtime_command",
        "prompt.send",
        { prompt: "secret prompt" },
        () => {
          throw new Error("private failure detail");
        },
        { runtime: "claudecode", sessionId: "session-1" },
      ),
      /private failure detail/u,
    );
    await recorder.close();

    assert.equal(result, "ok");
    const records = await readRecords(filePath);
    assert.equal(records.length, 3);
    assert.equal(records[0]?.traceId, records[1]?.traceId);
    assert.deepEqual(records.map((record) => record.outcome), [
      "observed",
      "succeeded",
      "failed",
    ]);
    const persisted = await readFile(filePath, "utf8");
    assert.equal(persisted.includes("secret prompt"), false);
    assert.equal(persisted.includes("private failure detail"), false);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("recording write failure never changes bridge operation results", async () => {
  const directory = await temporaryDirectory();
  const originalConsoleError = console.error;
  const reported: unknown[][] = [];
  try {
    const blockedParent = path.join(directory, "not-a-directory");
    await writeFile(blockedParent, "block", "utf8");
    const recorder = new BehaviorRecorder({
      enabled: true,
      filePath: path.join(blockedParent, "behavior.jsonl"),
    });
    console.error = (...values: unknown[]) => {
      reported.push(values);
    };

    const result = await recorder.capture(
      "core.decision",
      "write.failure",
      {},
      () => 42,
    );
    recorder.record("core.decision", "write.failure.again", {});
    await recorder.close();

    assert.equal(result, 42);
    assert.equal(reported.length, 1);
  } finally {
    console.error = originalConsoleError;
    await rm(directory, { recursive: true, force: true });
  }
});

test("recording serialization failure is also isolated from bridge work", async () => {
  const directory = await temporaryDirectory();
  const originalConsoleError = console.error;
  const reported: unknown[][] = [];
  try {
    const recorder = new BehaviorRecorder({
      enabled: true,
      filePath: path.join(directory, "behavior.jsonl"),
      recordId: () => {
        throw new Error("record id generator failed");
      },
    });
    console.error = (...values: unknown[]) => {
      reported.push(values);
    };

    const result = await recorder.capture(
      "core.decision",
      "serialization.failure",
      {},
      () => 84,
    );
    await recorder.close();

    assert.equal(result, 84);
    assert.equal(reported.length, 1);
  } finally {
    console.error = originalConsoleError;
    await rm(directory, { recursive: true, force: true });
  }
});

test("store commit recording contains only structural counts", async () => {
  const directory = await temporaryDirectory();
  try {
    const recordingPath = path.join(directory, "recordings", "behavior.jsonl");
    await mkdir(path.dirname(recordingPath), { recursive: true });
    const recorder = new BehaviorRecorder({ enabled: true, filePath: recordingPath });
    const store = new BridgeStore(path.join(directory, "data"), {
      behaviorRecorder: recorder,
      persistDebounceMs: 1,
    });
    await store.init();
    await store.upsertSession({
      sessionId: "private-session-id",
      cwd: "K:\\private\\project",
      status: "ready",
      runtime: "codex",
    });
    await store.close();
    await recorder.close();

    const text = await readFile(recordingPath, "utf8");
    assert.equal(text.includes("private-session-id"), false);
    assert.equal(text.includes("K:\\private\\project"), false);
    const commits = (await readRecords(recordingPath)).filter(
      (record) => record.stage === "core.state_committed",
    );
    assert.ok(commits.length > 0);
    for (const commit of commits) {
      assert.deepEqual(Object.keys(commit.observed as object).sort(), [
        "entryCounts",
        "topLevelKeys",
      ]);
    }
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("shared migration golden examples are valid Node behavior records", async () => {
  const examples = ["approval", "input", "prompt", "retry", "launch"];
  for (const name of examples) {
    const text = await readFile(
      path.join(process.cwd(), "protocol", "migration", "v1", "examples", `${name}.jsonl`),
      "utf8",
    );
    const value: unknown = JSON.parse(text);
    assert.equal(isBehaviorRecord(value), true, name);
  }
});
