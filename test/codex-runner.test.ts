import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import { CodexRunner, type CodexExitResult } from "../src/codex-runner.js";
import type { SessionRecord } from "../src/domain.js";

test("reserves a session before the child process finishes spawning", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-runner-lock-"));
  const previousNoDeprecation = process.noDeprecation;
  process.noDeprecation = true;
  try {
    const now = new Date().toISOString();
    const session: SessionRecord = {
      sessionId: "019faef0-d0bb-7703-af82-17ee9b45397b",
      shortId: "9b45397b",
      cwd: directory,
      projectName: path.basename(directory),
      status: "waiting",
      openedAt: now,
      lastSeenAt: now,
    };
    const runner = new CodexRunner(process.execPath);
    let resolveExit: (result: CodexExitResult) => void = () => {};
    const exited = new Promise<CodexExitResult>((resolve) => {
      resolveExit = resolve;
    });

    const first = runner.resume(session, "first", resolveExit);
    const second = runner.resume(session, "second", resolveExit);
    await assert.rejects(second, /已经在通过飞书继续运行/);
    await first;
    assert.equal(runner.isRunning(session.sessionId), true);
    await exited;
    assert.equal(runner.isRunning(session.sessionId), false);
    await runner.close();
  } finally {
    process.noDeprecation = previousNoDeprecation;
    await rm(directory, { recursive: true, force: true });
  }
});
