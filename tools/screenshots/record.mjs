// Records a screen as an H.264 MP4 from Chromium's screencast (needs ffmpeg):
//   node tools/screenshots/record.mjs [--screen flow] [--size 1920x1080] [--seconds 20] [--warmup 8] [--out file.mp4]
//                                     [--frames <dir>]
// The screen runs for --warmup seconds first, so Live flow is full when recording starts. Frames arrive as the
// page draws them; their timestamps keep the video's timing true, at a constant 30 frames per second.
// --frames keeps the frames and ffmpeg's list of them in that folder, to encode again with other settings.
import { execFileSync } from "node:child_process";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { parseArgs } from "node:util";
import { launch, openPage, showScreen } from "./browser.mjs";

const { values } = parseArgs({
  options: {
    screen: { type: "string", default: "flow" },
    size: { type: "string", default: "1920x1080" },
    seconds: { type: "string", default: "20" },
    warmup: { type: "string", default: "8" },
    out: { type: "string", default: path.join(import.meta.dirname, "out", "flow.mp4") },
    frames: { type: "string" },
  },
});
const [width, height] = values.size.split("x").map(Number);
const frameDir = values.frames
  ? path.resolve(values.frames)
  : await mkdtemp(path.join(tmpdir(), "marketgrade-frames-"));
await mkdir(frameDir, { recursive: true });

const browser = await launch();
const { page, errors } = await openPage(browser, width, height);
await showScreen(page, values.screen, Number(values.warmup) * 1_000);

const frames = [];
const cdp = await page.context().newCDPSession(page);
cdp.on("Page.screencastFrame", async ({ data, metadata, sessionId }) => {
  const file = path.join(frameDir, `${String(frames.length).padStart(5, "0")}.jpg`);
  frames.push({ file, time: metadata.timestamp });
  await writeFile(file, Buffer.from(data, "base64"));
  await cdp.send("Page.screencastFrameAck", { sessionId }).catch(() => {});
});
await cdp.send("Page.startScreencast", { format: "jpeg", quality: 95, maxWidth: width, maxHeight: height });
await page.waitForTimeout(Number(values.seconds) * 1_000);
await cdp.send("Page.stopScreencast");
await browser.close();

// Each frame shows until the next one; ffmpeg's concat list carries those durations.
const list = frames
  .map((frame, i) => {
    const next = frames[i + 1]?.time ?? frame.time + 1 / 30;
    return `file '${frame.file}'\nduration ${(next - frame.time).toFixed(4)}`;
  })
  .join("\n");
await writeFile(path.join(frameDir, "frames.txt"), `${list}\nfile '${frames.at(-1).file}'\n`);
const seconds = frames.at(-1).time - frames[0].time;
console.log(`${frames.length} frames over ${seconds.toFixed(1)} s (${(frames.length / seconds).toFixed(1)} per second)`);

const ffmpeg = [
  "-y", "-loglevel", "error",
  "-f", "concat", "-safe", "0", "-i", path.join(frameDir, "frames.txt"),
  "-vf", `fps=30,scale=${width}:${height}:flags=lanczos,format=yuv420p`,
  "-c:v", "libx264", "-preset", "slow", "-crf", "18",
  "-movflags", "+faststart",
  path.resolve(values.out),
];
try {
  await mkdir(path.dirname(path.resolve(values.out)), { recursive: true });
  execFileSync("ffmpeg", ffmpeg, { stdio: "inherit" });
  console.log(values.out);
} catch (error) {
  if (error.code !== "ENOENT" || !values.frames) {
    throw error;
  }
  console.error(`ffmpeg not found; the frames are in ${frameDir}. Encode them later with:\nffmpeg ${ffmpeg.join(" ")}`);
}
if (!values.frames) {
  await rm(frameDir, { recursive: true, force: true });
}
if (errors.length > 0) {
  console.error(`Console errors:\n${errors.join("\n")}`);
  process.exit(1);
}
