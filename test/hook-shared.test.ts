import assert from "node:assert/strict";
import { createServer } from "node:http";
import test from "node:test";

import {
  addManagedTerminalMetadata,
  postHook,
} from "../src/hooks/shared.js";

test("managed hook metadata uses the AI CLI environment names", () => {
  const idName = "AI_CLI_FEISHU_MANAGED_TERMINAL_ID";
  const elevatedName = "AI_CLI_FEISHU_MANAGED_TERMINAL_ELEVATED";
  const previousId = process.env[idName];
  const previousElevated = process.env[elevatedName];
  try {
    process.env[idName] = "terminal-renamed";
    process.env[elevatedName] = "1";
    assert.deepEqual(addManagedTerminalMetadata({ session_id: "session-1" }), {
      session_id: "session-1",
      managed_terminal_id: "terminal-renamed",
      managed_terminal_elevated: true,
    });
  } finally {
    restoreEnvironment(idName, previousId);
    restoreEnvironment(elevatedName, previousElevated);
  }
});

test("hook requests use the renamed bridge URL, token, and header", async () => {
  const urlName = "AI_CLI_FEISHU_BRIDGE_URL";
  const tokenName = "AI_CLI_FEISHU_CONTROL_TOKEN";
  const previousUrl = process.env[urlName];
  const previousToken = process.env[tokenName];
  const token = "a".repeat(64);
  let receivedHeader: string | undefined;
  let receivedBody = "";
  const server = createServer((request, response) => {
    receivedHeader = request.headers["x-ai-cli-feishu-control-token"] as
      | string
      | undefined;
    request.setEncoding("utf8");
    request.on("data", (chunk: string) => {
      receivedBody += chunk;
    });
    request.on("end", () => {
      response.writeHead(200, { "content-type": "application/json" });
      response.end('{"ok":true}');
    });
  });
  try {
    await new Promise<void>((resolve, reject) => {
      server.once("listening", resolve);
      server.once("error", reject);
      server.listen(0, "127.0.0.1");
    });
    const address = server.address();
    assert.ok(address && typeof address === "object");
    process.env[urlName] = `http://127.0.0.1:${address.port}`;
    process.env[tokenName] = token;

    assert.deepEqual(await postHook("/probe", { value: 42 }, 1_000), {
      ok: true,
    });
    assert.equal(receivedHeader, token);
    assert.deepEqual(JSON.parse(receivedBody), { value: 42 });
  } finally {
    restoreEnvironment(urlName, previousUrl);
    restoreEnvironment(tokenName, previousToken);
    await new Promise<void>((resolve, reject) => {
      server.close((error) => error ? reject(error) : resolve());
    });
  }
});

function restoreEnvironment(name: string, value: string | undefined): void {
  if (value === undefined) {
    delete process.env[name];
  } else {
    process.env[name] = value;
  }
}
