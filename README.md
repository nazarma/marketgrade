# Marketgrade

Marketgrade is a free, open-source desktop app that measures how good a market is on each crypto
exchange. It tracks data health (delay, gaps, correctness), liquidity (spread, depth, cost of a
large order) and market maker performance against agreed targets. The first release covers
Kraken, Coinbase and Binance.

**Status: early development.** The engine connects to Kraken, Coinbase and Binance, reconnects when a
connection dies, and the desktop window shows each exchange's state and messages per second.

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
`tests/Marketgrade.Engine.Tests/Fixtures` were captured this way.

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
| `tools`                      | Probe tool for live exchange connections         |
| `docs/architecture.md`       | Architecture decisions                           |

## License

[MIT](LICENSE)
