// Publishes the backend as a self-contained executable and places it where
// Tauri expects the "marketgrade-backend" sidecar: binaries/marketgrade-backend-<target triple>.
import { execFileSync } from "node:child_process";
import { copyFileSync, mkdirSync, rmSync } from "node:fs";
import path from "node:path";

const desktopDir = path.resolve(import.meta.dirname, "..");
const repoDir = path.resolve(desktopDir, "../..");

const runtimeIds = {
  "aarch64-apple-darwin": "osx-arm64",
  "x86_64-apple-darwin": "osx-x64",
  "x86_64-pc-windows-msvc": "win-x64",
  "aarch64-pc-windows-msvc": "win-arm64",
  "x86_64-unknown-linux-gnu": "linux-x64",
  "aarch64-unknown-linux-gnu": "linux-arm64",
};

const targetTriple =
  process.argv[2] ??
  execFileSync("rustc", ["--print", "host-tuple"], { encoding: "utf8" }).trim();
const runtimeId = runtimeIds[targetTriple];
if (!runtimeId) {
  console.error(`No .NET runtime identifier known for target ${targetTriple}`);
  process.exit(1);
}

const publishDir = path.join(repoDir, "artifacts", "sidecar", runtimeId);
rmSync(publishDir, { recursive: true, force: true });

execFileSync(
  "dotnet",
  [
    "publish",
    path.join(repoDir, "src", "backend", "Marketgrade.Backend"),
    "--configuration", "Release",
    "--runtime", runtimeId,
    "--self-contained",
    "-p:PublishSingleFile=true",
    // SQLite's and DuckDB's native libraries go inside the single file too.
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    // Compressing halves the installed size at no measurable start-up cost. The DMG, the .deb and the AppImage
    // compress with zlib or gzip and come out the same size either way; the Windows installer compresses with
    // LZMA, which shrinks the uncompressed file much further (43 MB instead of 58 MB), so Windows stays uncompressed.
    `-p:EnableCompressionInSingleFile=${!runtimeId.startsWith("win-")}`,
    "--output", publishDir,
  ],
  { stdio: "inherit" },
);

const extension = runtimeId.startsWith("win-") ? ".exe" : "";
const binariesDir = path.join(desktopDir, "binaries");
mkdirSync(binariesDir, { recursive: true });
const target = path.join(binariesDir, `marketgrade-backend-${targetTriple}${extension}`);
// macOS kills a signed executable that was overwritten in place, so the old one is removed first.
rmSync(target, { force: true });
copyFileSync(path.join(publishDir, `Marketgrade.Backend${extension}`), target);
console.log(`Sidecar written to ${path.relative(repoDir, target)}`);
