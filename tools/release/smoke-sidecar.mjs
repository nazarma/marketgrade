// Starts a published backend the way the desktop app does and checks that it answers:
//   node tools/release/smoke-sidecar.mjs <backend executable> [timeout seconds]
// The engine stays off, so no exchange is contacted; settings (SQLite) and history (DuckDB) are still read,
// which loads both native libraries. Exits with 1 on any failure.
import { spawn } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { createInterface } from "node:readline";

const [executable, timeoutArg = "60"] = process.argv.slice(2);
if (!executable) {
  console.error("Usage: node tools/release/smoke-sidecar.mjs <backend executable> [timeout seconds]");
  process.exit(1);
}

const token = "smoke-test-token";
const dataDir = mkdtempSync(path.join(tmpdir(), "marketgrade-smoke-"));
const started = performance.now();
const backend = spawn(path.resolve(executable), [], {
  env: {
    ...process.env,
    MARKETGRADE_TOKEN: token,
    MARKETGRADE_DATA_DIR: dataDir,
    MARKETGRADE_ENGINE_DISABLED: "1",
  },
  stdio: ["ignore", "pipe", "inherit"],
});

function finish(code, message) {
  console.log(message);
  backend.kill();
  // Give the process a moment to release the data folder before it is removed.
  setTimeout(() => {
    rmSync(dataDir, { recursive: true, force: true });
    process.exit(code);
  }, 1_000);
}

const timer = setTimeout(
  () => finish(1, `FAIL: no ready line within ${timeoutArg} s`),
  Number(timeoutArg) * 1_000,
);
backend.on("error", (error) => finish(1, `FAIL: could not start: ${error.message}`));
backend.on("exit", (code) => finish(1, `FAIL: backend exited early with code ${code}`));

const ready = await new Promise((resolve) => {
  createInterface({ input: backend.stdout }).on("line", (line) => {
    const match = /^MARKETGRADE_READY port=(\d+)$/.exec(line.trim());
    if (match) {
      resolve(Number(match[1]));
    } else {
      console.log(`backend stdout: ${line}`);
    }
  });
});
clearTimeout(timer);
backend.removeAllListeners("exit");
console.log(`ready on port ${ready} after ${Math.round(performance.now() - started)} ms`);

const paths = ["/health", "/engine/status", "/settings/recording", "/history/delay?minutes=60"];
let failed = false;
for (const route of paths) {
  try {
    const response = await fetch(`http://127.0.0.1:${ready}${route}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    const body = await response.text();
    console.log(`${response.status} ${route} ${body.slice(0, 120)}`);
    failed ||= response.status !== 200;
  } catch (error) {
    console.log(`ERR ${route} ${error.message}`);
    failed = true;
  }
}
const unauthorized = await fetch(`http://127.0.0.1:${ready}/health`);
console.log(`${unauthorized.status} /health without token`);
failed ||= unauthorized.status !== 401;

finish(failed ? 1 : 0, failed ? "FAIL" : "PASS");
