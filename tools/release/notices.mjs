// Writes THIRD-PARTY-NOTICES.md: everything the installers ship that Marketgrade did not write, with its licence,
// the copyright lines found in each package, and one copy of each licence text (from the SPDX licence list).
//   node tools/release/notices.mjs
// Needs restored .NET packages (dotnet restore), installed npm packages (pnpm install), fetched crates
// (cargo fetch) and network access for the licence texts. Run it again whenever a dependency changes.
import { execFileSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import path from "node:path";

const repoDir = path.resolve(import.meta.dirname, "../..");
const desktopTargets = [
  "aarch64-apple-darwin",
  "x86_64-apple-darwin",
  "x86_64-pc-windows-msvc",
  "x86_64-unknown-linux-gnu",
];

const permissive = new Set([
  "MIT",
  "MIT-0",
  "Apache-2.0",
  "BSD-2-Clause",
  "BSD-3-Clause",
  "ISC",
  "Zlib",
  "0BSD",
  "Unlicense",
  "CC0-1.0",
  "BSL-1.0",
  "Unicode-3.0",
  "Unicode-DFS-2016",
  "blessing",
]);
// For "A OR B" the first of these that is offered is the one used.
const preference = ["MIT", "Apache-2.0", "BSD-3-Clause", "BSD-2-Clause", "ISC", "Zlib", "Unlicense", "0BSD", "CC0-1.0"];

/** The licences a package is used under: "A OR B" picks one, "A AND B" needs both. */
function chosenLicences(expression) {
  const normalised = expression.replaceAll("/", " OR ").replace(/\s+/g, " ").trim();
  const parts = splitTopLevel(stripParens(normalised), " AND ");
  return parts.flatMap((part) => {
    const options = splitTopLevel(stripParens(part), " OR ").map((o) => stripParens(o));
    if (options.length === 1) {
      return options[0].includes(" ") && !options[0].includes(" WITH ") ? chosenLicences(options[0]) : [options[0]];
    }
    const pick = preference.find((p) => options.includes(p)) ?? options[0];
    return chosenLicences(pick);
  });
}

function stripParens(text) {
  let t = text.trim();
  while (t.startsWith("(") && t.endsWith(")") && splitTopLevel(t.slice(1, -1), "\u0000").length === 1) {
    t = t.slice(1, -1).trim();
  }
  return t;
}

function splitTopLevel(text, separator) {
  const parts = [];
  let depth = 0;
  let start = 0;
  for (let i = 0; i < text.length; i++) {
    if (text[i] === "(") depth++;
    else if (text[i] === ")") depth--;
    else if (depth === 0 && text.startsWith(separator, i)) {
      parts.push(text.slice(start, i));
      start = i + separator.length;
      i += separator.length - 1;
    }
  }
  parts.push(text.slice(start));
  return parts.map((p) => p.trim()).filter(Boolean);
}

/** Copyright lines from a package's licence and notice files. */
function copyrights(folder) {
  if (!folder || !existsSync(folder)) {
    return [];
  }
  const lines = readdirSync(folder)
    .filter((f) => /^(licen[cs]e|copying|notice)/i.test(f))
    .flatMap((f) => readFileSync(path.join(folder, f), "utf8").split(/\r?\n/))
    .map((l) => l.trim().replace(/\s+/g, " "))
    .filter((l) => /^(\(c\)|©|copyright\b)/i.test(l))
    .filter((l) => !/\[(yyyy|year)\]|\{yyyy\}|<year>|copyright (notice|holders?|owner|license|and)\b|copyright \(c\) <|^copyright$/i.test(l));
  return [...new Set(lines)];
}

// .NET: packages with code that runs (runtime or native assets) in the published backend.
function dotnetPackages() {
  const assets = JSON.parse(
    readFileSync(path.join(repoDir, "artifacts/obj/Marketgrade.Backend/project.assets.json"), "utf8"),
  );
  const packagesFolder = path.join(homedir(), ".nuget", "packages");
  return Object.entries(assets.targets["net10.0"])
    .filter(([, entry]) => entry.type === "package" && (entry.runtime || entry.native || entry.runtimeTargets))
    .map(([key]) => {
      const [name, version] = key.split("/");
      const folder = path.join(packagesFolder, name.toLowerCase(), version);
      const nuspec = readFileSync(path.join(folder, `${name.toLowerCase()}.nuspec`), "utf8");
      const license = /<license type="expression">([^<]+)<\/license>/.exec(nuspec)?.[1] ?? "unknown";
      return { name, version, license, copyrights: copyrights(folder) };
    });
}

// npm: the user interface's production dependencies, all bundled into its build.
function npmPackages() {
  const listed = JSON.parse(
    execFileSync("pnpm", ["--dir", "src/ui", "licenses", "list", "--prod", "--json"], {
      cwd: repoDir,
      encoding: "utf8",
      maxBuffer: 64 * 1024 * 1024,
    }),
  );
  return Object.values(listed)
    .flat()
    .flatMap((p) =>
      p.versions.map((version, i) => ({
        name: p.name,
        version,
        license: p.license,
        copyrights: copyrights(p.paths[i]),
      })),
    );
}

// Rust: crates compiled into the desktop shell on any of the four targets; build scripts and procedural
// macros only run while building and are not shipped.
function rustCrates() {
  const found = new Map();
  for (const target of desktopTargets) {
    const metadata = JSON.parse(
      execFileSync(
        "cargo",
        ["metadata", "--format-version", "1", "--locked", "--filter-platform", target],
        { cwd: path.join(repoDir, "src/desktop"), encoding: "utf8", maxBuffer: 256 * 1024 * 1024 },
      ),
    );
    const packages = new Map(metadata.packages.map((p) => [p.id, p]));
    const nodes = new Map(metadata.resolve.nodes.map((n) => [n.id, n]));
    const isProcMacro = (id) => packages.get(id).targets.some((t) => t.kind.includes("proc-macro"));
    const queue = [metadata.resolve.root];
    const seen = new Set(queue);
    while (queue.length > 0) {
      const node = nodes.get(queue.shift());
      for (const dep of node.deps) {
        const normal = dep.dep_kinds.some((k) => k.kind === null);
        if (normal && !isProcMacro(dep.pkg) && !seen.has(dep.pkg)) {
          seen.add(dep.pkg);
          queue.push(dep.pkg);
        }
      }
    }
    seen.delete(metadata.resolve.root);
    for (const id of seen) {
      const p = packages.get(id);
      found.set(`${p.name}@${p.version}`, {
        name: p.name,
        version: p.version,
        license: p.license ?? (p.license_file ? `see ${p.license_file}` : "unknown"),
        copyrights: copyrights(path.dirname(p.manifest_path)),
      });
    }
  }
  return [...found.values()];
}

// Native libraries and runtimes that come inside packages or installers rather than as packages of their own.
const native = [
  {
    name: ".NET runtime and ASP.NET Core",
    where: "backend (self-contained)",
    license: "MIT",
    copyrights: ["Copyright (c) .NET Foundation and Contributors"],
    note: "The runtime's own third-party notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT",
  },
  {
    name: "DuckDB",
    where: "backend, from DuckDB.NET.Bindings.Full",
    license: "MIT",
    copyrights: ["Copyright 2018-2025 Stichting DuckDB Foundation"],
    note: "https://github.com/duckdb/duckdb/blob/main/LICENSE",
  },
  {
    name: "SQLite",
    where: "backend, from SQLitePCLRaw.lib.e_sqlite3",
    license: "blessing",
    copyrights: [],
    note: "Public domain: https://sqlite.org/copyright.html",
  },
  {
    name: "Geist and Geist Mono fonts",
    where: "user interface, from @fontsource-variable/geist and geist-mono",
    license: "OFL-1.1",
    copyrights: ["Copyright 2024 The Geist Project Authors (https://github.com/vercel/geist-font)"],
    note: "Embedded unchanged as web fonts; the font licence applies to the fonts only, not to Marketgrade.",
  },
  {
    name: "Microsoft Edge WebView2 bootstrapper",
    where: "Windows installer",
    license: "Microsoft software licence",
    copyrights: ["Copyright (C) Microsoft Corporation"],
    note: "Installs the WebView2 runtime when Windows lacks it: https://developer.microsoft.com/microsoft-edge/webview2/",
  },
  {
    name: "WebKitGTK, GTK 3, GLib, libsoup and the libraries they use",
    where: "Linux AppImage (the .deb uses the system's copies)",
    license: "LGPL-2.1-or-later",
    copyrights: ["The WebKitGTK, GTK, GLib and libsoup authors"],
    note:
      "Copied unchanged from Ubuntu 22.04 packages and loaded dynamically, so they can be replaced. Their sources " +
      "are available from https://packages.ubuntu.com/jammy/ and https://webkitgtk.org/.",
  },
];

const spdxTexts = new Map();
async function licenceText(id) {
  if (!spdxTexts.has(id)) {
    const [licence, exception] = id.split(" WITH ");
    const fetchText = async (name) => {
      const response = await fetch(
        `https://raw.githubusercontent.com/spdx/license-list-data/main/text/${encodeURIComponent(name)}.txt`,
      );
      return response.ok ? (await response.text()).trim() : null;
    };
    const texts = [await fetchText(licence), exception ? await fetchText(exception) : null].filter(Boolean);
    spdxTexts.set(id, texts.length > 0 ? texts.join("\n\n") : null);
  }
  return spdxTexts.get(id);
}

const groups = [
  { title: ".NET packages (backend)", packages: dotnetPackages() },
  { title: "npm packages (user interface)", packages: npmPackages() },
  { title: "Rust crates (desktop shell)", packages: rustCrates() },
];
for (const group of groups) {
  group.packages.sort((a, b) => a.name.localeCompare(b.name) || a.version.localeCompare(b.version));
}

// Every licence used, with the packages and copyright lines under it.
const byLicence = new Map();
const addUse = (licence, name, lines) => {
  const entry = byLicence.get(licence) ?? { packages: [], copyrights: new Set() };
  entry.packages.push(name);
  lines.forEach((l) => entry.copyrights.add(l));
  byLicence.set(licence, entry);
};
for (const group of groups) {
  for (const p of group.packages) {
    for (const licence of chosenLicences(p.license)) {
      addUse(licence, `${p.name} ${p.version}`, p.copyrights);
    }
  }
}
for (const n of native) {
  addUse(n.license, n.name, n.copyrights);
}

const isPermissive = (licence) =>
  licence.split(" WITH ").every((part, i) => (i === 0 ? permissive.has(part) : /exception/i.test(part)));
const escape = (text) => text.replaceAll("|", "\\|");
const licences = [...byLicence.keys()].sort((a, b) => byLicence.get(b).packages.length - byLicence.get(a).packages.length);

const out = [];
out.push("# Third-party notices", "");
out.push(
  "Marketgrade is released under the MIT licence (see [LICENSE](LICENSE)). Its installers also contain the " +
    "software listed here, each under its own licence. One copy of each licence text follows the lists, with " +
    "the copyright notices of the software that uses it.",
  "",
);
out.push(
  "This file is generated by `node tools/release/notices.mjs` from the lock files; do not edit it by hand. " +
    "Packages offered under a choice of licences (\"MIT OR Apache-2.0\") are used under the first permissive " +
    "one listed in the script.",
  "",
);
out.push("## Summary", "", "| Licence | Used by | Permissive |", "| ------- | ------: | ---------- |");
for (const licence of licences) {
  out.push(`| ${licence} | ${byLicence.get(licence).packages.length} | ${isPermissive(licence) ? "yes" : "**no**, see below"} |`);
}
out.push("");
const notPermissive = licences.filter((l) => !isPermissive(l));
if (notPermissive.length > 0) {
  out.push("Not permissive:", "");
  for (const licence of notPermissive) {
    out.push(`- **${licence}**: ${byLicence.get(licence).packages.join(", ")}.`);
  }
  out.push("");
}

out.push("## Native libraries, runtimes and fonts", "", "| Software | Where | Licence | Notes |", "| -------- | ----- | ------- | ----- |");
for (const n of native) {
  out.push(`| ${n.name} | ${n.where} | ${n.license} | ${escape(n.note)} |`);
}
out.push("");

for (const group of groups) {
  out.push(`## ${group.title}`, "", "| Package | Version | Licence |", "| ------- | ------- | ------- |");
  for (const p of group.packages) {
    out.push(`| ${escape(p.name)} | ${p.version} | ${escape(p.license)} |`);
  }
  out.push("");
}

out.push("## Licence texts", "");
for (const licence of licences) {
  const entry = byLicence.get(licence);
  const text = await licenceText(licence);
  out.push(`### ${licence}`, "");
  out.push(`Used by: ${entry.packages.join(", ")}.`, "");
  if (entry.copyrights.size > 0) {
    out.push("Copyright notices:", "", "```text", ...[...entry.copyrights].sort(), "```", "");
  }
  if (text) {
    out.push("```text", text, "```", "");
  } else {
    out.push("No standard text; see the notes in the tables above.", "");
  }
}

writeFileSync(path.join(repoDir, "THIRD-PARTY-NOTICES.md"), out.join("\n"));
console.log(`THIRD-PARTY-NOTICES.md: ${licences.length} licences`);
for (const licence of licences) {
  console.log(`${isPermissive(licence) ? "  " : "! "}${licence}: ${byLicence.get(licence).packages.length}`);
}
