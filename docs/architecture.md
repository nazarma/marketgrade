# Architecture

## Desktop shell and user interface

Marketgrade is a desktop application. The user interface is written in React and TypeScript and
runs inside a Tauri 2 window. Tauri keeps the installer small and uses the operating system's web
view, and its Rust side is responsible for starting and stopping the backend.

## Backend process

All market logic runs in a single .NET 10 process that Tauri starts as a child process. It
contains the engine (exchange connections, order books, recording) and an ASP.NET Core API that
serves REST endpoints and, later, SignalR streams to the user interface. The engine is a separate
class library without any ASP.NET Core dependency, so it stays independent of how its data is
served.

## Local API security

The API listens on `127.0.0.1` only, on a port chosen by the operating system, so it is never
reachable from another computer. On every start Tauri generates a random secret token and passes
it to the backend in an environment variable. Every request must carry it as a bearer token, so
other programs on the same computer cannot use the API either. CORS allows only the Tauri origins
and the Vite dev server.

## Start-up handshake

The backend prints exactly one line to standard output, `MARKETGRADE_READY port=<port>`, once it is
listening; all logging goes to standard error. Tauri waits up to 20 seconds for that line and then
hands the port and token to the user interface through the `get_backend_info` command. Tauri stops
the backend when the app exits, and the backend also watches Tauri's process ID and shuts itself
down if Tauri disappears. Development and production use the same start-up code; only the
executable differs (`dotnet` with the built assembly in development, a self-contained sidecar
executable in production).

## Storage

Later steps add three stores, each chosen for one kind of data: SQLite for settings, DuckDB for
chart history, and binary append-only files for raw exchange messages.

## Repository

Everything lives in one repository: the engine, backend, user interface, desktop shell, tests and
benchmarks. .NET Aspire is not used; the only process orchestration needed is Tauri starting the
backend.
