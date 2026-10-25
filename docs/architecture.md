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

## Exchange connections

The engine keeps one WebSocket connection per exchange: Kraken (`BTC/USD`, spot WebSocket v2),
Coinbase (`BTC-USD`, Advanced Trade) and Binance (`BTCUSDT`, spot diff depth stream). The pairs are
fixed for now.

**Adapters.** Everything that differs between exchanges lives in one adapter class per exchange: the
URL, the subscribe messages (UTF-8 bytes built once) and `Classify`, which sorts a raw message into
data, heartbeat, control or error. `Classify` reads only the top-level JSON properties with
`Utf8JsonReader` and never allocates. Connection, queues and consumers are shared code.

- Kraken: one `book` subscription with depth 1000. Kraken v2 has no heartbeat subscription; heartbeats
  arrive about once a second as soon as any channel is subscribed. Kraken does not restore
  subscriptions after a reconnect, so they are sent on every connection.
- Coinbase: two subscribe messages, because Coinbase accepts one channel per message: `level2` and
  `heartbeats`. Without the heartbeats channel Coinbase closes quiet connections. The level2 snapshot
  is a single message of several megabytes.
- Binance: the stream is named in the URL, so nothing is sent. We use the market-data-only host
  `data-stream.binance.vision` (port 443) rather than `stream.binance.com:9443`: it serves the same
  public streams, has no account features, and port 443 passes restrictive networks. Binance sends a
  ping frame every 20 seconds; `ClientWebSocket` answers pings with pongs by itself.

**Buffers.** Each message is received into a `MessageBuffer` from a pool of 1,024 buffers of 16 KB
created at startup. A larger message continues in an array rented from `ArrayPool<byte>.Shared`,
which goes back to `ArrayPool` when the buffer returns to the pool. If the pool is empty, a new buffer
is created and counted as a pool miss. Every buffer is stamped with its arrival time (Unix
nanoseconds), a sequence number per exchange and the connection number, then written to the queue of
each consumer. Each consumer releases the buffer as its last action; the last release returns it to
the pool. After startup, receiving, stamping, queueing, classifying and releasing allocate nothing per
message, which the benchmarks check.

**Queues.** Every consumer has its own bounded queue per exchange (8,192 messages, one reader, one
writer). A full queue makes the connection wait instead of dropping messages; a warning is logged when
a queue is more than 80% full. The only consumer so far counts messages by kind. More consumers, such
as the recorder, get their own queue without changes to the connection code.

**Dead connections and reconnects.** A watchdog checks every second and aborts a connection that has
received nothing for 10 seconds. After a failure the connection waits 1, 2, 4, 8, 16 and then 30
seconds between attempts; the delay starts again at 1 second once a connection has been live for 60
seconds. The last 200 connection events (connected, dead, closed by server, blocked) are kept in
memory.

**Binance specifics.** Binance refuses connections from restricted regions with HTTP 451 during the
handshake. The connection then shows `Blocked` and retries every 30 minutes instead of using the
normal delays. Binance also closes every connection after 24 hours. Such a close is a scheduled
reconnect: the connection reconnects at once without counting a failure. The `reconnects` metric
still records it, with reason `scheduled`.

**Status and metrics.** Once per second the engine builds an immutable `EngineStatus` (state, data
messages and bytes per second, time since the last message, reconnects, last error per exchange) and
publishes it by swapping a reference, so readers never lock. Messages per second counts data messages
only. The backend serves it at `GET /engine/status`. The `Marketgrade.Engine` meter has the counters
`messages_received`, `bytes_received`, `reconnects` and `pool_misses` and the gauges `queue_length`
and `connection_state`, all tagged by exchange.

## Storage

Later steps add three stores, each chosen for one kind of data: SQLite for settings, DuckDB for
chart history, and binary append-only files for raw exchange messages.

## Repository

Everything lives in one repository: the engine, backend, user interface, desktop shell, tests and
benchmarks. .NET Aspire is not used; the only process orchestration needed is Tauri starting the
backend.
