import { execFileSync } from "node:child_process";
import { extname } from "node:path";
import { existsSync, readFileSync } from "node:fs";

const textExtensions = new Set([
  ".cs",
  ".csproj",
  ".json",
  ".md",
  ".mjs",
  ".ps1",
  ".ts",
  ".vbs",
  ".xml",
  ".yaml",
  ".yml",
]);
const textNames = new Set([".env.example", ".gitignore", "LICENSE"]);

const files = execFileSync(
  "git",
  ["ls-files", "--cached", "--others", "--exclude-standard", "-z"],
  { encoding: "utf8" },
)
  .split("\0")
  .filter(Boolean)
  .filter((file) => textNames.has(file) || textExtensions.has(extname(file)))
  .filter(existsSync);

const failures = [];
for (const file of files) {
  const content = readFileSync(file);
  if (content.includes(0)) {
    continue;
  }
  const text = content.toString("utf8");
  const lines = text.split(/\r\n|\n|\r/u);
  for (let index = 0; index < lines.length; index += 1) {
    if (/[ \t]+$/u.test(lines[index])) {
      failures.push(`${file}:${index + 1}: trailing whitespace`);
    }
  }
  if (content.length > 0 && content[content.length - 1] !== 0x0a) {
    failures.push(`${file}: missing final newline`);
  }
}

if (failures.length > 0) {
  console.error("Formatting checks failed:");
  for (const failure of failures) {
    console.error(`  ${failure}`);
  }
  process.exitCode = 1;
} else {
  console.log(`Format check passed (${files.length} files).`);
}
