# Screenshots and recordings

Playwright scripts that take the README's screenshots and record Live flow from the production build, in
Chromium, against a real backend. They run the same files the installers ship: the built user interface and
the published backend.

## Once

```sh
pnpm install
pnpm --filter marketgrade-screenshots exec playwright install chromium
```

Recording also needs [ffmpeg](https://ffmpeg.org/), and the GIF needs [gifski](https://gif.ski/)
(`brew install ffmpeg gifski` on macOS). Optimise PNGs with [oxipng](https://github.com/shssoichiro/oxipng).

## Each time

Use a data folder of its own, with a neutral path, because Settings shows it. Copy a `history.duckdb` into it
first if the charts should show past hours.

```sh
pnpm --dir src/ui build
pnpm publish:backend

# Terminal 1: the backend, on the fixed port the scripts expect.
MARKETGRADE_TOKEN=screenshot-token MARKETGRADE_PORT=5299 MARKETGRADE_DATA_DIR=/tmp/marketgrade-data \
  src/desktop/binaries/marketgrade-backend-$(rustc --print host-tuple)

# Terminal 2: the built user interface with the app's Content Security Policy, on localhost:5173.
node tools/screenshots/serve.mjs

# Terminal 3:
node tools/screenshots/shoot.mjs                         # Overview, Live flow, Data health at 1440 x 900
node tools/screenshots/record.mjs --seconds 20           # Live flow, 1920 x 1080, 20 s MP4
node tools/screenshots/make-gif.mjs tools/screenshots/out/flow.mp4 docs/images/live-flow.gif \
  --start 6 --seconds 7 --crop 1656:648:242:16 --width 960 --fps 12
oxipng -o 4 --strip safe tools/screenshots/out/*.png
```

The crop is the Live flow title and canvas at 1920 x 1080. `record.mjs --frames <dir>` keeps the captured
frames and ffmpeg's list of them, to encode again (for example losslessly, as the GIF's source) without
recording again. Output goes to `tools/screenshots/out/` (ignored by git). The scripts read `MARKETGRADE_PORT` and
`MARKETGRADE_TOKEN` (defaults 5299 and `screenshot-token`) and `APP_URL` (default `http://localhost:5173`);
the backend accepts browser requests from that origin only. Chromium uses the GPU, as the app's web view does.
Record Live flow while the market is busy; the Trades figure under the lanes shows how busy it is.
