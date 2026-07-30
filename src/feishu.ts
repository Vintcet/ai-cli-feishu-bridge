import * as Lark from "@larksuiteoapi/node-sdk";
import { createReadStream } from "node:fs";
import { open, rm, stat } from "node:fs/promises";
import path from "node:path";

type Card = Record<string, unknown>;

export class FeishuGateway {
  readonly client: Lark.Client;

  constructor(appId: string, appSecret: string) {
    this.client = new Lark.Client({ appId, appSecret });
  }

  async sendText(chatId: string, text: string): Promise<string> {
    const response = await this.client.im.v1.message.create({
      params: { receive_id_type: "chat_id" },
      data: {
        receive_id: chatId,
        msg_type: "text",
        content: JSON.stringify({ text }),
      },
    });
    return this.messageIdFromResponse(response, "send text");
  }

  async replyText(messageId: string, text: string): Promise<string> {
    const response = await this.client.im.v1.message.reply({
      path: { message_id: messageId },
      data: {
        msg_type: "text",
        content: JSON.stringify({ text }),
      },
    });
    return this.messageIdFromResponse(response, "reply text");
  }

  async sendCard(chatId: string, card: Card): Promise<string> {
    const response = await this.client.im.v1.message.create({
      params: { receive_id_type: "chat_id" },
      data: {
        receive_id: chatId,
        msg_type: "interactive",
        content: JSON.stringify(card),
      },
    });
    return this.messageIdFromResponse(response, "send card");
  }

  async patchCard(messageId: string, card: Card): Promise<void> {
    const response = await this.client.im.v1.message.patch({
      path: { message_id: messageId },
      data: { content: JSON.stringify(card) },
    });
    if (response.code !== 0) {
      throw new Error(`Feishu patch card failed: ${response.code} ${response.msg}`);
    }
  }

  async downloadMessageResource(
    messageId: string,
    fileKey: string,
    type: "image" | "file",
    destinationPath: string,
    maxBytes: number,
  ): Promise<number> {
    const resource = await this.client.im.v1.messageResource.get({
      params: { type },
      path: { message_id: messageId, file_key: fileKey },
    });
    const stream = resource.getReadableStream();
    const handle = await open(destinationPath, "wx");
    let size = 0;
    try {
      for await (const chunk of stream) {
        const buffer = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
        size += buffer.length;
        if (size > maxBytes) {
          throw new Error(`飞书附件超过本机限制（${formatBytes(maxBytes)}）。`);
        }
        await handle.write(buffer);
      }
      if (size === 0) {
        throw new Error("飞书附件为空。 ");
      }
      return size;
    } catch (error) {
      await handle.close().catch(() => undefined);
      await rm(destinationPath, { force: true }).catch(() => undefined);
      throw error;
    } finally {
      await handle.close().catch(() => undefined);
    }
  }

  async sendLocalFile(chatId: string, filePath: string): Promise<string> {
    const fileStat = await stat(filePath);
    const extension = path.extname(filePath).toLowerCase();
    const imageExtensions = new Set([
      ".jpg",
      ".jpeg",
      ".png",
      ".webp",
      ".gif",
      ".tif",
      ".tiff",
      ".bmp",
      ".ico",
    ]);
    if (imageExtensions.has(extension) && fileStat.size <= 10 * 1024 * 1024) {
      const upload = await this.client.im.v1.image.create({
        data: { image_type: "message", image: createReadStream(filePath) },
      });
      if (!upload?.image_key) {
        throw new Error("Feishu image upload returned no image key.");
      }
      return await this.sendResourceMessage(
        chatId,
        "image",
        { image_key: upload.image_key },
        "send image",
      );
    }

    const upload = await this.client.im.v1.file.create({
      data: {
        file_type: feishuFileType(extension),
        file_name: path.basename(filePath),
        file: createReadStream(filePath),
      },
    });
    if (!upload?.file_key) {
      throw new Error("Feishu file upload returned no file key.");
    }
    return await this.sendResourceMessage(
      chatId,
      "file",
      { file_key: upload.file_key },
      "send file",
    );
  }

  private async sendResourceMessage(
    chatId: string,
    msgType: "image" | "file",
    content: Record<string, string>,
    operation: string,
  ): Promise<string> {
    const response = await this.client.im.v1.message.create({
      params: { receive_id_type: "chat_id" },
      data: {
        receive_id: chatId,
        msg_type: msgType,
        content: JSON.stringify(content),
      },
    });
    return this.messageIdFromResponse(response, operation);
  }

  private messageIdFromResponse(
    response: { code?: number; msg?: string; data?: { message_id?: string } },
    operation: string,
  ): string {
    if (response.code !== 0 || !response.data?.message_id) {
      throw new Error(`Feishu ${operation} failed: ${response.code} ${response.msg}`);
    }
    return response.data.message_id;
  }
}

function feishuFileType(
  extension: string,
): "opus" | "mp4" | "pdf" | "doc" | "xls" | "ppt" | "stream" {
  if (extension === ".opus") return "opus";
  if (extension === ".mp4") return "mp4";
  if (extension === ".pdf") return "pdf";
  if ([".doc", ".docx"].includes(extension)) return "doc";
  if ([".xls", ".xlsx", ".csv"].includes(extension)) return "xls";
  if ([".ppt", ".pptx"].includes(extension)) return "ppt";
  return "stream";
}

function formatBytes(bytes: number): string {
  if (bytes >= 1024 * 1024) {
    return `${Math.round(bytes / (1024 * 1024))} MiB`;
  }
  return `${Math.round(bytes / 1024)} KiB`;
}
