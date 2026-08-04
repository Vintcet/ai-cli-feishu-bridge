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

test("path aliases, quoted destructive commands, and incomplete inputs stay manual", () => {
  const cwd = path.resolve("approval-risk-project");
  const outside = path.resolve(cwd, "..", "outside", "result.txt");
  const cases: Array<{ toolName: string; toolInput: unknown }> = [
    { toolName: "Write", toolInput: { filePath: outside } },
    {
      toolName: "external_directory",
      toolInput: { resources: [outside] },
    },
    {
      toolName: "powershell",
      toolInput: {
        command: 'powershell -Command "Remove-Item -Recurse -Force build"',
      },
    },
    {
      toolName: "Bash",
      toolInput: { command: `${"x".repeat(64 * 1024)}rm -rf data` },
    },
    {
      toolName: "unknown_custom_tool",
      toolInput: { operation: "looks harmless" },
    },
    {
      toolName: "Bash",
      toolInput: { command: "python cleanup.py" },
    },
    {
      toolName: "powershell",
      toolInput: { command: "Move-Item build ../outside" },
    },
    {
      toolName: "shell_command",
      toolInput: { command: "echo result > ../outside.txt" },
    },
  ];

  for (const item of cases) {
    const result = assessApprovalRisk({ ...item, cwd });
    assert.equal(result.level, "high", JSON.stringify(item).slice(0, 500));
    assert.ok(result.reason.length > 0);
  }
});

test("OpenCode resource commands and camel-case project paths can remain low risk", () => {
  const cwd = path.resolve("approval-risk-project");
  assert.equal(
    assessApprovalRisk({
      toolName: "shell",
      toolInput: { resources: ["npm test"] },
      cwd,
    }).level,
    "low",
  );
  assert.equal(
    assessApprovalRisk({
      toolName: "shell_command",
      toolInput: { command: "git status --short" },
      cwd,
    }).level,
    "low",
  );
  assert.equal(
    assessApprovalRisk({
      toolName: "Write",
      toolInput: { filePath: path.join(cwd, "src", "safe.ts") },
      cwd,
    }).level,
    "low",
  );
});
