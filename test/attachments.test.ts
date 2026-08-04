import assert from "node:assert/strict";
import { mkdtemp, mkdir, readdir, realpath, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import {
  appendAttachmentsToPrompt,
  LocalAttachmentStore,
  parseFeishuContent,
} from "../src/attachments.js";
import type { FeishuGateway } from "../src/feishu.js";
import {
  extractBridgeFileDirectives,
  validateBridgeFile,
} from "../src/file-transfer.js";

test("parses Feishu image, file, and post content", () => {
  assert.deepEqual(
    parseFeishuContent({
      message_type: "image",
      content: JSON.stringify({ image_key: "img_12345678" }),
    }),
    {
      text: "",
      attachments: [
        {
          fileKey: "img_12345678",
          fileName: "feishu-image-12345678.jpg",
          resourceType: "image",
        },
      ],
    },
  );
  assert.deepEqual(
    parseFeishuContent({
      message_type: "file",
      content: JSON.stringify({ file_key: "file_1", file_name: "report.pdf" }),
    }).attachments[0],
    { fileKey: "file_1", fileName: "report.pdf", resourceType: "file" },
  );
  const post = parseFeishuContent({
    message_type: "post",
    content: JSON.stringify({
      zh_cn: {
        title: "检查截图",
        content: [[
          { tag: "text", text: "找出问题" },
          { tag: "img", image_key: "img_post_1" },
        ]],
      },
    }),
  });
  assert.match(post.text, /检查截图/);
  assert.match(post.text, /找出问题/);
  assert.equal(post.attachments.length, 1);
});

test("removes the bot mention prefix from a group text message", () => {
  assert.deepEqual(
    parseFeishuContent({
      message_type: "text",
      content: JSON.stringify({ text: "@_user_1 请继续处理" }),
      mentions: [{ key: "@_user_1" }],
    }),
    { text: "请继续处理", attachments: [] },
  );
});

test("attachment paths are added to the Codex prompt", () => {
  const prompt = appendAttachmentsToPrompt("分析它", [
    { absolutePath: "K:\\project\\shot.png", fileName: "shot.png", size: 100 },
  ]);
  assert.match(prompt, /K:\\project\\shot\.png/);
  assert.match(prompt, /用户要求：分析它/);
});

test("attachment filenames remain Windows-safe after truncation", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-attachments-"));
  try {
    const store = new LocalAttachmentStore(
      directory,
      1024,
      4,
      60_000,
      10,
      10 * 1024,
    );
    const [saved] = await store.download(
      fakeAttachmentGateway(Buffer.from("test")),
      "message-1",
      [{
        fileKey: "file-1",
        fileName: `${"a".repeat(119)}.${"b".repeat(20)}`,
        resourceType: "file",
      }],
    );

    assert.ok(saved);
    assert.ok(saved.fileName.length <= 120);
    assert.doesNotMatch(saved.fileName, /[. ]$/u);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("attachment store enforces global file and byte limits", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-attachments-"));
  const monthDirectory = path.join(directory, new Date().toISOString().slice(0, 7));
  try {
    await mkdir(monthDirectory, { recursive: true });
    await writeFile(path.join(monthDirectory, "existing.bin"), "1234", "utf8");

    let countDownloads = 0;
    const countStore = new LocalAttachmentStore(directory, 1024, 4, 60_000, 1, 1024);
    await assert.rejects(
      countStore.download(
        fakeAttachmentGateway(Buffer.from("x"), () => {
          countDownloads += 1;
        }),
        "message-count",
        [{ fileKey: "count", fileName: "count.bin", resourceType: "file" }],
      ),
      /最多保留 1 个文件/u,
    );
    assert.equal(countDownloads, 0);

    const byteStore = new LocalAttachmentStore(directory, 1024, 4, 60_000, 10, 5);
    await assert.rejects(
      byteStore.download(
        fakeAttachmentGateway(Buffer.from("12")),
        "message-bytes",
        [{ fileKey: "bytes", fileName: "bytes.bin", resourceType: "file" }],
      ),
      /总容量不能超过 5 B/u,
    );
    assert.deepEqual(await readdir(monthDirectory), ["existing.bin"]);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test("file return directives are stripped and constrained to the project", async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "codex-feishu-files-"));
  const project = path.join(directory, "project");
  const outside = path.join(directory, "outside.txt");
  try {
    await mkdir(project);
    const report = path.join(project, "report.txt");
    await writeFile(report, "done", "utf8");
    await writeFile(outside, "secret", "utf8");

    const parsed = extractBridgeFileDirectives(
      `报告已生成。\nBRIDGE_SEND_FILE: "${report}"`,
    );
    assert.equal(parsed.displayMessage, "报告已生成。");
    assert.deepEqual(parsed.paths, [report]);
    assert.equal(
      (await validateBridgeFile(report, project, 1024)).path,
      await realpath(report),
    );
    await assert.rejects(
      validateBridgeFile(outside, project, 1024),
      /不在当前项目目录/,
    );
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

function fakeAttachmentGateway(
  data: Buffer,
  onDownload?: () => void,
): FeishuGateway {
  return {
    async downloadMessageResource(
      _messageId: string,
      _fileKey: string,
      _resourceType: "image" | "file",
      destinationPath: string,
    ): Promise<number> {
      onDownload?.();
      await writeFile(destinationPath, data);
      return data.length;
    },
  } as unknown as FeishuGateway;
}
