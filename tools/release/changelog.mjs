// Prints one version's entry from CHANGELOG.md, for the release notes:
//   node tools/release/changelog.mjs <x.y.z>
// Exits with 1 when the changelog has no entry for that version.
import { readFileSync } from "node:fs";
import path from "node:path";

const version = process.argv[2];
const text = readFileSync(path.resolve(import.meta.dirname, "../../CHANGELOG.md"), "utf8");
const lines = text.split("\n");
const start = lines.findIndex((line) => line.startsWith(`## [${version}]`));
if (!version || start < 0) {
  console.error(`CHANGELOG.md has no entry for ${version ?? "(no version given)"}`);
  process.exit(1);
}
// The entry ends at the next version or at the link definitions.
const end = lines.findIndex((line, i) => i > start && (line.startsWith("## ") || /^\[[^\]]+\]: /.test(line)));
console.log(
  lines
    .slice(start + 1, end < 0 ? undefined : end)
    .join("\n")
    .trim(),
);
