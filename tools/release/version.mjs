// The app version lives in the root package.json; the other files that need it as text are kept equal.
//   node tools/release/version.mjs check            fails when any file differs from package.json
//   node tools/release/version.mjs set <x.y.z>      writes the version to every file
import { readFileSync, writeFileSync } from "node:fs";
import path from "node:path";

const repoDir = path.resolve(import.meta.dirname, "../..");

// Each file: how to read its version and how to write a new one. tauri.conf.json reads package.json itself.
const files = [
  {
    file: "package.json",
    pattern: /^(\s*"version":\s*")([^"]+)(")/m,
  },
  {
    file: "src/ui/package.json",
    pattern: /^(\s*"version":\s*")([^"]+)(")/m,
  },
  {
    file: "Directory.Build.props",
    pattern: /(<Version>)([^<]+)(<\/Version>)/,
  },
  {
    file: "src/desktop/Cargo.toml",
    pattern: /(\[package\][^[]*?\nversion = ")([^"]+)(")/,
  },
  {
    file: "src/desktop/Cargo.lock",
    pattern: /(\nname = "marketgrade-desktop"\nversion = ")([^"]+)(")/,
  },
  {
    file: "src/desktop/tauri.conf.json",
    pattern: /^(\s*"version":\s*")([^"]+)(")/m,
    fixed: "../../package.json",
  },
];

function read(entry) {
  const text = readFileSync(path.join(repoDir, entry.file), "utf8");
  const match = entry.pattern.exec(text);
  if (!match) {
    throw new Error(`${entry.file}: no version found`);
  }
  return { text, version: match[2] };
}

const [command, newVersion] = process.argv.slice(2);

if (command === "check") {
  const expected = read(files[0]).version;
  let failed = false;
  for (const entry of files) {
    const { version } = read(entry);
    const wanted = entry.fixed ?? expected;
    const ok = version === wanted;
    failed ||= !ok;
    console.log(`${ok ? "ok      " : "MISMATCH"} ${entry.file}: ${version}`);
  }
  if (failed) {
    console.error(`Versions differ from package.json (${expected}); run: node tools/release/version.mjs set ${expected}`);
    process.exit(1);
  }
  console.log(`All files at ${expected}`);
} else if (command === "set" && /^\d+\.\d+\.\d+$/.test(newVersion ?? "")) {
  for (const entry of files) {
    if (entry.fixed) {
      continue;
    }
    const { text } = read(entry);
    writeFileSync(path.join(repoDir, entry.file), text.replace(entry.pattern, `$1${newVersion}$3`));
    console.log(`${entry.file}: ${newVersion}`);
  }
} else {
  console.error("Usage: node tools/release/version.mjs check | set <major.minor.patch>");
  process.exit(1);
}
