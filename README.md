# Marketgrade

Marketgrade is a free, open-source desktop app that measures how good a market is on each crypto
exchange. It tracks data health (delay, gaps, correctness), liquidity (spread, depth, cost of a
large order) and market maker performance against agreed targets. The first release covers
Kraken, Coinbase and Binance.

**Status: early development.** The engine connects to Kraken, Coinbase and Binance, reconnects when a
connection dies, and records every message to compressed hourly files. It keeps a live order book per
exchange, checks every update with the exchange's own method (checksum, sequence numbers or update
IDs), repairs a broken book by itself, and measures delay and processing time. The desktop window
shows each exchange's state, book, best prices, delay and gaps, and the Settings screen controls
recording.

## Prerequisites

- [.NET SDK 10](https://dotnet.microsoft.com/download)
- [Node.js 24 LTS](https://nodejs.org/) (see `.nvmrc`)
- [pnpm 12](https://pnpm.io/installation), for example `npm install -g pnpm@12`
- [Rust](https://rustup.rs/) (stable)
- The [Tauri system dependencies](https://v2.tauri.app/start/prerequisites/) for your platform

## Run in development

```sh
pnpm install
pnpm dev
```

`pnpm dev` builds the backend, starts the Vite dev server and opens the desktop window.

### In a browser

For automated screenshots the user interface can also run in a normal browser against a backend started by
hand. This only exists in the Vite dev server; production builds do not contain it.

```sh
dotnet build src/backend/Marketgrade.Backend
MARKETGRADE_TOKEN=dev-token MARKETGRADE_PORT=5299 MARKETGRADE_DATA_DIR=/tmp/marketgrade-dev \
  artifacts/bin/Marketgrade.Backend/debug/Marketgrade.Backend
```

Then, in a second terminal:

```sh
VITE_BACKEND_PORT=5299 VITE_BACKEND_TOKEN=dev-token pnpm --dir src/ui dev
```

and open <http://localhost:5173>. `MARKETGRADE_PORT` fixes the backend's port (it still listens on
`127.0.0.1` only and still requires the token); the desktop app never sets it. A separate data folder keeps
these runs out of the app's own settings, history and recordings.

## Checks

```sh
dotnet test --solution Marketgrade.slnx
dotnet format Marketgrade.slnx --verify-no-changes

pnpm --dir src/ui lint
pnpm --dir src/ui format:check
pnpm --dir src/ui test

cargo check --manifest-path src/desktop/Cargo.toml
```

Benchmarks: `dotnet run -c Release --project benchmarks/Marketgrade.Benchmarks`.

## Diagnostics

The probe tool runs the engine against the real exchanges without the desktop app, for example to
check which exchanges are reachable from your network:

```sh
dotnet run --project tools/Marketgrade.Probe -- --seconds 60 --interval 5
```

`--capture <dir>` saves real sample messages of each kind instead; the test fixtures in
`tests/Marketgrade.Engine.Tests/Fixtures` were captured this way. `--record <dir>` records everything
with the default recording settings.

Recordings are in the app data folder (`~/Library/Application Support/io.github.nazarma.marketgrade/recordings`
on macOS) unless changed in Settings. The reader tool prints, checks and summarizes them (format:
[docs/recording-format.md](docs/recording-format.md)):

```sh
dotnet run --project tools/Marketgrade.Reader -- cat <file> --limit 20 --payload
dotnet run --project tools/Marketgrade.Reader -- verify <folder>
dotnet run --project tools/Marketgrade.Reader -- stats <folder>
dotnet run --project tools/Marketgrade.Reader -- replay <file or folder>
dotnet run --project tools/Marketgrade.Reader -- trim <file> <out.mgrec.zst> --seconds 60
```

`replay` rebuilds every exchange's order book from the recordings with the same code the app uses and
reports, per exchange, the messages applied, the checksum, sequence and parse failures, the snapshots
applied, the most levels per side and the time range. Each failure is listed with whether the live
book failed at the same place (a `BookReset` record follows it); the command exits with code 1 if
any failure has no such match. `trim` copies the first seconds of a recording into a small compressed
file, as used for the test fixtures in `tests/Marketgrade.Engine.Tests/Fixtures/replay`.

Engine metrics while the app runs (install once with `dotnet tool install -g dotnet-counters`):

```sh
dotnet-counters monitor --name Marketgrade.Backend --counters Marketgrade.Engine
```

## Production backend

`pnpm publish:backend` publishes the backend as a self-contained executable into
`src/desktop/binaries/`, where Tauri picks it up as a sidecar. Release builds use
`src/desktop/tauri.release.conf.json`, which adds the sidecar. Building installers is not set up
yet; a release executable can be built with:

```sh
pnpm publish:backend
pnpm tauri build --no-bundle --config src/desktop/tauri.release.conf.json
```

## Repository layout

| Path                         | Contents                                         |
| ---------------------------- | ------------------------------------------------ |
| `src/engine`                 | Engine class library (no web dependencies)       |
| `src/backend`                | ASP.NET Core host for the local API              |
| `src/ui`                     | React + TypeScript user interface (Vite)         |
| `src/desktop`                | Tauri 2 desktop shell                            |
| `tests`                      | xUnit tests                                      |
| `benchmarks`                 | BenchmarkDotNet benchmarks                       |
| `tools`                      | Probe tool and recording reader                  |
| `docs/architecture.md`       | Architecture decisions                           |
| `docs/recording-format.md`   | Recording file format                            |

## License

[MIT](LICENSE)
