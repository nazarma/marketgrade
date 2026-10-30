// Cuts an animated GIF from a recording (needs ffmpeg and gifski):
//   node tools/screenshots/make-gif.mjs <in.mp4> <out.gif> [--start 2] [--seconds 7] [--crop w:h:x:y]
//                                       [--width 960] [--fps 12] [--quality 80]
// --crop takes ffmpeg's crop arguments in the recording's pixels, before scaling to --width.
import { execFileSync } from "node:child_process";
import { mkdtemp, readdir, rm, stat } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { parseArgs } from "node:util";

const { values, positionals } = parseArgs({
  allowPositionals: true,
  options: {
    start: { type: "string", default: "2" },
    seconds: { type: "string", default: "7" },
    crop: { type: "string" },
    width: { type: "string", default: "960" },
    fps: { type: "string", default: "12" },
    quality: { type: "string", default: "80" },
  },
});
const [input, output] = positionals;
if (!input || !output) {
  console.error("Usage: node tools/screenshots/make-gif.mjs <in.mp4> <out.gif> [options]");
  process.exit(1);
}

const frameDir = await mkdtemp(path.join(tmpdir(), "marketgrade-gif-"));
const filters = [
  values.crop && `crop=${values.crop}`,
  `fps=${values.fps}`,
  `scale=${values.width}:-2:flags=lanczos`,
].filter(Boolean);
execFileSync(
  "ffmpeg",
  [
    "-y", "-loglevel", "error",
    "-ss", values.start, "-t", values.seconds, "-i", input,
    "-vf", filters.join(","),
    path.join(frameDir, "%05d.png"),
  ],
  { stdio: "inherit" },
);
const frames = (await readdir(frameDir)).sort().map((f) => path.join(frameDir, f));
execFileSync(
  "gifski",
  ["--fps", values.fps, "--quality", values.quality, "--width", values.width, "-o", output, ...frames],
  { stdio: "inherit" },
);
await rm(frameDir, { recursive: true, force: true });
console.log(`${output}: ${frames.length} frames, ${((await stat(output)).size / 1e6).toFixed(1)} MB`);
