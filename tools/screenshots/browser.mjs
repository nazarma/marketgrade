// Opens the production user interface in Chromium against a backend started by hand, as the desktop shell
// would: the page asks Tauri for the backend's port and token, and this stub answers.
import { chromium } from "playwright";

export const appUrl = process.env.APP_URL ?? "http://localhost:5173";
export const backendPort = Number(process.env.MARKETGRADE_PORT ?? 5299);
export const backendToken = process.env.MARKETGRADE_TOKEN ?? "screenshot-token";

/** Chromium with the real GPU, as the app's web view uses; plain headless Chromium draws WebGL in software. */
export function launch() {
  const gpu =
    process.platform === "darwin"
      ? ["--enable-gpu", "--use-angle=metal", "--ignore-gpu-blocklist"]
      : ["--enable-gpu", "--ignore-gpu-blocklist"];
  return chromium.launch({ args: gpu });
}

/** A page at the given size with the Tauri stub; console errors are collected in `errors`. */
export async function openPage(browser, width, height) {
  const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 1 });
  await page.addInitScript(
    ({ port, token }) => {
      window.__TAURI_INTERNALS__ = {
        invoke: async (command) => (command === "get_backend_info" ? { port, token } : null),
        transformCallback: () => 0,
      };
    },
    { port: backendPort, token: backendToken },
  );
  const errors = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  page.on("pageerror", (e) => errors.push(String(e)));
  return { page, errors };
}

/** Goes to a screen (overview, flow, health, settings) and waits for it to fill. */
export async function showScreen(page, screen, waitMs) {
  await page.goto(`${appUrl}/#/${screen}`);
  await page.waitForTimeout(waitMs);
}
