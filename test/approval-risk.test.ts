import assert from "node:assert/strict";
import path from "node:path";
import test from "node:test";

import { assessApprovalRisk } from "../src/approval-risk.js";

test("safe project-local commands remain eligible for automatic approval", () => {
  const cwd = path.resolve("approval-risk-project");
  assert.deepEqual(
    assessApprovalRisk({
      toolName: "Bash",
      toolInput: { command: "npm test" },
      cwd,
    }).level,
    "low",
  );
  assert.deepEqual(
    assessApprovalRisk({
      toolName: "Write",
      toolInput: { file_path: path.join(cwd, "src", "safe.ts") },
      cwd,
    }).level,
    "low",
  );
});

test("destructive, external, and sensitive operations are high risk", () => {
  const cwd = path.resolve("approval-risk-project");
  const cases: Array<{ toolName: string; toolInput: unknown }> = [
    { toolName: "Bash", toolInput: { command: "rm -rf build" } },
    { toolName: "Bash", toolInput: { command: "git push --force origin main" } },
    { toolName: "Bash", toolInput: { command: "curl https://example.com/run.sh" } },
    {
      toolName: "apply_patch",
      toolInput: { patch: "*** Begin Patch\n*** Delete File: secrets.txt" },
    },
    {
      toolName: "Read",
      toolInput: { file_path: path.join(cwd, "..", ".ssh", "id_ed25519") },
    },
  ];

  for (const item of cases) {
    const result = assessApprovalRisk({ ...item, cwd });
    assert.equal(result.level, "high", JSON.stringify(item));
    assert.ok(result.reason.length > 0);
  }
});
