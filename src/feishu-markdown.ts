type CardElement = Record<string, unknown>;

type MarkdownBlock =
  | { kind: "heading"; text: string }
  | { kind: "paragraph"; text: string }
  | { kind: "unordered-list"; items: string[] }
  | { kind: "ordered-list"; items: Array<{ number: string; text: string }> }
  | { kind: "quote"; text: string }
  | { kind: "code"; language: string; text: string }
  | { kind: "table"; rows: string[][] }
  | { kind: "divider" };

interface MarkdownCardOptions {
  maxCharacters?: number;
  maxElements?: number;
  truncate?: boolean;
}

const headingPattern = /^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$/;
const unorderedListPattern = /^\s*[-+*]\s+(.+)$/;
const orderedListPattern = /^\s*(\d+)[.)]\s+(.+)$/;
const quotePattern = /^\s*>\s?(.*)$/;
const fencePattern = /^\s{0,3}(`{3,}|~{3,})\s*([^\s]*)?.*$/;
const dividerPattern = /^\s*(?:(?:-\s*){3,}|(?:_\s*){3,}|(?:\*\s*){3,})$/;

export function markdownToFeishuCardElements(
  value: string,
  options: MarkdownCardOptions = {},
): CardElement[] {
  const maxCharacters = Math.max(200, options.maxCharacters ?? 3_200);
  const maxElements = Math.max(1, options.maxElements ?? 20);
  const shouldTruncate = options.truncate !== false;
  const normalized = normalizeMarkdown(value);
  if (!normalized) {
    return [];
  }

  const inputTruncated = shouldTruncate && normalized.length > maxCharacters;
  const source = inputTruncated
    ? `${normalized.slice(0, maxCharacters).trimEnd()}\n\n…`
    : normalized;
  const elements = parseMarkdownBlocks(source).flatMap(blockToElements);
  const needsElementTruncation = shouldTruncate && elements.length > maxElements;
  if (!inputTruncated && !needsElementTruncation) {
    return elements;
  }

  const note = truncationNote();
  if (maxElements === 1) {
    return [note];
  }
  return [...elements.slice(0, maxElements - 1), note];
}

export function splitTextForFeishu(value: string, maxCharacters = 2_800): string[] {
  const limit = Math.max(200, maxCharacters);
  const normalized = normalizeMarkdown(value);
  if (!normalized) {
    return [];
  }
  const chunks: string[] = [];
  let remaining = normalized;
  while (remaining.length > limit) {
    const splitAt = preferredSplitIndex(remaining, limit);
    chunks.push(remaining.slice(0, splitAt));
    remaining = remaining.slice(splitAt);
  }
  if (remaining) {
    chunks.push(remaining);
  }
  return chunks;
}

function preferredSplitIndex(value: string, limit: number): number {
  const minimum = Math.floor(limit * 0.55);
  for (const separator of ["\n\n", "\n", "。", "！", "？", ". ", "; ", "，", ", ", " "]) {
    const index = value.lastIndexOf(separator, limit);
    if (index >= minimum) {
      return index + separator.length;
    }
  }
  return limit;
}

function parseMarkdownBlocks(value: string): MarkdownBlock[] {
  const lines = value.split("\n");
  const blocks: MarkdownBlock[] = [];
  let index = 0;

  while (index < lines.length) {
    const line = lines[index]!;
    if (!line.trim()) {
      index += 1;
      continue;
    }

    const fence = line.match(fencePattern);
    if (fence) {
      const marker = fence[1]!;
      const language = (fence[2] ?? "").trim();
      const code: string[] = [];
      index += 1;
      while (index < lines.length && !closesFence(lines[index]!, marker)) {
        code.push(lines[index]!);
        index += 1;
      }
      if (index < lines.length) {
        index += 1;
      }
      blocks.push({ kind: "code", language, text: code.join("\n").trimEnd() });
      continue;
    }

    const heading = line.match(headingPattern);
    if (heading) {
      blocks.push({ kind: "heading", text: heading[2]! });
      index += 1;
      continue;
    }

    if (dividerPattern.test(line)) {
      blocks.push({ kind: "divider" });
      index += 1;
      continue;
    }

    if (isTableStart(lines, index)) {
      const rows = [parseTableRow(line)];
      index += 2;
      while (index < lines.length && isTableRow(lines[index]!)) {
        rows.push(parseTableRow(lines[index]!));
        index += 1;
      }
      blocks.push({ kind: "table", rows });
      continue;
    }

    const quote = line.match(quotePattern);
    if (quote) {
      const quoteLines: string[] = [];
      while (index < lines.length) {
        const match = lines[index]!.match(quotePattern);
        if (!match) break;
        quoteLines.push(match[1]!);
        index += 1;
      }
      blocks.push({ kind: "quote", text: quoteLines.join("\n").trim() });
      continue;
    }

    const unordered = line.match(unorderedListPattern);
    if (unordered) {
      const items: string[] = [];
      while (index < lines.length) {
        const match = lines[index]!.match(unorderedListPattern);
        if (!match || dividerPattern.test(lines[index]!)) break;
        items.push(match[1]!);
        index += 1;
      }
      blocks.push({ kind: "unordered-list", items });
      continue;
    }

    const ordered = line.match(orderedListPattern);
    if (ordered) {
      const items: Array<{ number: string; text: string }> = [];
      while (index < lines.length) {
        const match = lines[index]!.match(orderedListPattern);
        if (!match) break;
        items.push({ number: match[1]!, text: match[2]! });
        index += 1;
      }
      blocks.push({ kind: "ordered-list", items });
      continue;
    }

    const paragraph: string[] = [line.trim()];
    index += 1;
    while (
      index < lines.length &&
      lines[index]!.trim() &&
      !startsBlock(lines, index)
    ) {
      paragraph.push(lines[index]!.trim());
      index += 1;
    }
    blocks.push({ kind: "paragraph", text: paragraph.join("\n") });
  }

  return blocks;
}

function blockToElements(block: MarkdownBlock): CardElement[] {
  switch (block.kind) {
    case "heading":
      return [markdownDiv(`**${normalizeInline(block.text)}**`)];
    case "paragraph":
      return splitLongText(normalizeInline(block.text)).map(markdownDiv);
    case "unordered-list":
      return splitLongText(
        block.items.map((item) => `• ${normalizeListItem(item)}`).join("\n"),
      ).map(markdownDiv);
    case "ordered-list":
      return splitLongText(
        block.items
          .map((item) => `${item.number}．${normalizeListItem(item.text)}`)
          .join("\n"),
      ).map(markdownDiv);
    case "quote":
      return [noteElement(toPlainText(block.text))];
    case "code": {
      const label = block.language ? `代码 · ${toPlainText(block.language)}` : "代码";
      return splitLongText(block.text || "（空代码块）").map(
        (code, index) => plainDiv(`${label}${index > 0 ? "（续）" : ""}\n${code}`),
      );
    }
    case "table": {
      const rows = block.rows.map((row, rowIndex) => {
        const content = row.map(normalizeInline).join("　｜　");
        return rowIndex === 0 ? `**${content}**` : content;
      });
      return splitLongText(rows.join("\n")).map(markdownDiv);
    }
    case "divider":
      return [{ tag: "hr" }];
  }
}

function startsBlock(lines: string[], index: number): boolean {
  const line = lines[index]!;
  return Boolean(
    line.match(fencePattern) ||
      line.match(headingPattern) ||
      line.match(quotePattern) ||
      line.match(unorderedListPattern) ||
      line.match(orderedListPattern) ||
      dividerPattern.test(line) ||
      isTableStart(lines, index)
  );
}

function closesFence(line: string, marker: string): boolean {
  const value = line.trim();
  return value.length >= marker.length &&
    [...value].every((character) => character === marker[0]);
}

function isTableStart(lines: string[], index: number): boolean {
  return index + 1 < lines.length &&
    isTableRow(lines[index]!) &&
    isTableSeparator(lines[index + 1]!);
}

function isTableRow(line: string): boolean {
  return line.includes("|") && parseTableRow(line).length >= 2;
}

function isTableSeparator(line: string): boolean {
  const cells = parseTableRow(line);
  return cells.length >= 2 && cells.every((cell) => /^:?-{3,}:?$/.test(cell));
}

function parseTableRow(line: string): string[] {
  const trimmed = line.trim().replace(/^\|/, "").replace(/\|$/, "");
  const cells: string[] = [];
  let current = "";
  let escaped = false;
  for (const character of trimmed) {
    if (escaped) {
      current += character;
      escaped = false;
    } else if (character === "\\") {
      escaped = true;
    } else if (character === "|") {
      cells.push(current.trim());
      current = "";
    } else {
      current += character;
    }
  }
  if (escaped) current += "\\";
  cells.push(current.trim());
  return cells;
}

function normalizeMarkdown(value: string): string {
  return value
    .replace(/\u001b\[[0-?]*[ -/]*[@-~]/g, "")
    .replace(/\r\n?/g, "\n")
    .replace(/[ \t]+$/gm, "")
    .replace(/\n{4,}/g, "\n\n\n")
    .trim();
}

function isLocalLink(value: string): boolean {
  const target = value.trim();
  return /^[a-zA-Z]:[\\/]/.test(target) ||
    target.startsWith("/") ||
    target.toLowerCase().startsWith("file:");
}

function normalizeInline(value: string): string {
  return value
    .replace(/<br\s*\/?\s*>/gi, "\n")
    .replace(/<at\b[^>]*>[\s\S]*?<\/at>/gi, "@提及")
    .replace(/<[^>\n]+>/g, "")
    .replace(/(?<!!)\[([^\]]+)]\(([^)]+)\)/g, (match, label: string, target: string) =>
      isLocalLink(target) ? label + "（" + target + "）" : match
    )
    .replace(/!\[([^\]]*)\]\(([^)]+)\)/g, (_match, label: string, url: string) =>
      `[图片：${label || "未命名"}](${url})`
    )
    .trim();
}

function normalizeListItem(value: string): string {
  const task = value.match(/^\[([ xX])\]\s*(.*)$/);
  if (!task) return normalizeInline(value);
  return `${task[1]!.toLowerCase() === "x" ? "☑" : "☐"} ${normalizeInline(task[2]!)}`;
}

function toPlainText(value: string): string {
  return normalizeInline(value)
    .replace(/\[([^\]]+)]\(([^)]+)\)/g, "$1（$2）")
    .replace(/\*\*([^*]+)\*\*/g, "$1")
    .replace(/__([^_]+)__/g, "$1")
    .replace(/~~([^~]+)~~/g, "$1")
    .replace(/`([^`]+)`/g, "$1")
    .replace(/\*([^*]+)\*/g, "$1")
    .replace(/_([^_]+)_/g, "$1")
    .trim();
}

function splitLongText(value: string, limit = 1_500): string[] {
  if (value.length <= limit) return [value];
  const chunks: string[] = [];
  let remaining = value;
  while (remaining.length > limit) {
    const newline = remaining.lastIndexOf("\n", limit);
    const splitAt = newline >= Math.floor(limit * 0.6) ? newline : limit;
    chunks.push(remaining.slice(0, splitAt));
    remaining = remaining.slice(splitAt);
  }
  if (remaining) chunks.push(remaining);
  return chunks;
}

function markdownDiv(content: string): CardElement {
  return { tag: "div", text: { tag: "lark_md", content } };
}

function plainDiv(content: string): CardElement {
  return { tag: "div", text: { tag: "plain_text", content } };
}

function noteElement(content: string): CardElement {
  return { tag: "note", elements: [{ tag: "plain_text", content }] };
}

function truncationNote(): CardElement {
  return noteElement("内容较长，已截断显示。完整内容仍保留在本机 Codex 窗口中。");
}
