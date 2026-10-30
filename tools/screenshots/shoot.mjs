// Screenshots of the screens at one window size, as PNG:
//   node tools/screenshots/shoot.mjs [--size 1440x900] [--out tools/screenshots/out] [screen ...]
// Screens: overview, flow, health, settings (default: overview, flow, health, the README's three).
// Live flow is taken after 8 seconds of animation, the others after 4 seconds.
import { mkdir } from "node:fs/promises";
import path from "node:path";
import { parseArgs } from "node:util";
import { launch, openPage, showScreen } from "./browser.mjs";

const { values, positionals } = parseArgs({
  allowPositionals: true,
  options: {
    size: { type: "string", default: "1440x900" },
    out: { type: "string", default: path.join(import.meta.dirname, "out") },
  },
});
const [width, height] = values.size.split("x").map(Number);
const screens = positionals.length > 0 ? positionals : ["overview", "flow", "health"];

await mkdir(values.out, { recursive: true });
const browser = await launch();
const { page, errors } = await openPage(browser, width, height);
for (const screen of screens) {
  await showScreen(page, screen, screen === "flow" ? 8_000 : 4_000);
  const file = path.join(values.out, `${screen}-${width}x${height}.png`);
  await page.screenshot({ path: file });
  console.log(file);
}
await browser.close();
if (errors.length > 0) {
  console.error(`Console errors:\n${errors.join("\n")}`);
  process.exit(1);
}
