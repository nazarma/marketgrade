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
the pool. After startup, receiving, stamping, queueing, classifying, recording and releasing allocate
nothing per message, which the benchmarks check.

**Queues.** Every consumer has its own bounded queue per exchange (one reader, one writer): the stats
consumer, which counts messages by kind, and the recorder (see Recording). A full stats queue (8,192
messages) makes the connection wait instead of dropping messages. The recorder's queue is never waited
on. A warning is logged when a queue is more than 80% full.

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

## Recording

Every message from every exchange is written, exactly as received, to binary files in the recordings
folder. The format is described in [recording-format.md](recording-format.md).

**Consumer.** Each exchange has one recorder, a second consumer next to the stats consumer, with its
own queue of 2,048 messages. Every queued message holds a 16 KB pool buffer, so a stalled disk costs at
most about 32 MB per exchange; at a busy Coinbase rate of about 100 messages per second that still
covers about 20 seconds of stall.
A larger queue would survive longer stalls but hold proportionally more memory.

**Drop policy.** Recording must never slow live monitoring. The connection waits on the stats queue but
only calls `TryWrite` on the recorder's queue. If that fails, the connection releases the buffer once
on the recorder's behalf (so it still returns to the pool), counts it in `recording_dropped`, and adds
its sequence number to a gap it keeps itself. The gap is stamped onto the next record that fits in the
queue, and the recorder writes it as a `Gap` record right before that record, so gaps are always in
the right place in the file. `BoundedChannelFullMode.DropWrite` is not used: a message dropped by the
channel would never be released and the pool would leak. The connection also sends a
`ConnectionOpened` record (the URL) after each connect and a `ConnectionClosed` record (the reason)
after each disconnect, to the recorder only.

**Files and rotation.** Each exchange gets one file per UTC hour, chosen by the message's arrival
time, not the time it is written: `<folder>/<exchange>/<yyyy-MM-dd>/<HH>.mgrec`. UTC avoids duplicate
or missing hours at daylight saving changes. The recorder opens the next hour's file when a record
from a later hour arrives; it never goes back to an earlier hour, so records stay in the current file
if the system clock moves back. Reopening an hour (after a restart, or after recording was turned off
and on) appends to its file. If that hour was already compressed, the recorder writes a second part,
`<HH>-2.mgrec`.

**Batching.** Records are written into a write buffer (1 MB by default). The buffer goes to the
operating system when it is full or when the write interval (1 second) has passed, whichever comes
first; a timer covers quiet periods. A message larger than the buffer, such as Coinbase's snapshot, is
written straight to the file. Files are written with `RandomAccess.Write` at an offset the recorder
tracks itself: there is no second buffer as in `FileStream`, nothing is allocated per write, and the
offset is always the end of the last complete batch. The data reaches the operating system on every
flush, which survives the app being killed. Closing a file (new hour, folder change, recording off,
stop) calls `RandomAccess.FlushToDisk` once, so finished hours also survive a power loss.

**Crash recovery.** When a recorder starts, and when the folder changes, it scans every uncompressed
file of its exchange, checks each record's length and CRC, and truncates the file at the first broken
record; the number of bytes cut is logged and shown as an event. Leftover `*.mgrec.zst.tmp` files from
an interrupted compression are deleted, and files of finished hours are queued for compression. The
scan runs on the recorder's task, so a large file never delays startup; messages arriving meanwhile
wait in the queue, and anything beyond it becomes a gap.

**Compression.** One background worker, never a recorder, compresses finished hours with Zstandard
(ZstdSharp, pure .NET, level 3 by default): compress to `<HH>.mgrec.zst.tmp`, decompress it again and
check every record's CRC and the record count and length against the original, rename it to
`<HH>.mgrec.zst`, then delete the `.mgrec`. A file that fails is left uncompressed and logged. Every
10 minutes the worker also compresses hours that were missed, for example after recording was turned
off during an hour.

**Retention.** Every 10 minutes and after each compression, the worker deletes recordings older than
"Keep for" (7 days), then the oldest recordings until the total is under "Maximum disk space"
(20 GB). It only ever deletes files that match the folder layout exactly, never anything else in the
folder, never the file being written and never a file of the current hour, and it removes empty date
folders. Retention manages only the current recordings folder.

**Low disk.** Every 5 seconds the worker measures the recordings and the free space on their drive.
Below 1 GB free, recording pauses (state `PausedLowDisk`) and an event is logged; above 2 GB it resumes
by itself. The messages missed while paused, while recording was off or after a write error become one
`Gap` record when writing resumes. A write error (for example a removed drive) puts the recorder in
state `Error`; it retries every 5 seconds.

**Settings.** All recording parameters are user settings: on/off, folder, keep for, maximum disk
space, write buffer size, write interval, compression on/off and level. The engine applies new
settings by swapping one immutable settings object; recorders pick it up between batches, so nothing
restarts. After a folder change each recorder finishes its current file and starts the next one in
the new folder; old recordings are not moved. Turning recording off closes the files; the queues are
still drained so every buffer returns to the pool.

**Status and metrics.** `GET /engine/status` includes a `recording` object: state (`Recording`, `Off`,
`PausedLowDisk`, `Error`), folder, size of today's (UTC) recordings, total size, free space, dropped
messages and the last error. The metrics `recording_bytes_written`, `recording_dropped` and
`recording_files_compressed` are tagged by exchange.

**Measured.** A 15-minute probe recording (`--record`, default settings, a quiet Sunday evening,
October 2026) crossed an hour boundary, so its first hour was compressed. Per message in that hour:

| Exchange | Messages | On disk uncompressed | Compressed (level 3) | Ratio  |
| -------- | -------: | -------------------: | -------------------: | -----: |
| Kraken   |   89,507 |                234 B |               30.4 B |  7.7:1 |
| Coinbase |   11,716 |              1,742 B |              159.0 B | 11.0:1 |
| Binance  |    6,213 |                885 B |              144.6 B |  6.1:1 |

Uncompressed sizes include the 40-byte record header; Coinbase's includes its multi-megabyte level2
snapshot at connect. On the development machine (Intel Core i7-9750H), the recorder's write path
takes 170 to 220 ns per message with 0 bytes allocated, about 0.7 GB/s of Kraken messages and
1.0 to 1.1 GB/s of Coinbase and Binance messages into the operating system's file cache. The whole
hot path with recording stays at 0 bytes allocated per message.

## Storage

**Data folder.** Tauri passes the operating system's app data folder (for example
`~/Library/Application Support/io.github.nazarma.marketgrade` on macOS) to the backend in the
`MARKETGRADE_DATA_DIR` environment variable, in development and production alike. Without it, as in
the probe tool and tests, the backend uses `Marketgrade` in the local application data folder.

**Settings.** Settings live in SQLite at `<data folder>/marketgrade.db`, accessed with
`Microsoft.Data.Sqlite` (no Entity Framework): one table `settings(key, value, updated_at)` with one
row per section and its values as JSON. The schema version is `PRAGMA user_version`, and migrations
are an ordered list applied at startup. `GET /settings/recording` returns the values and the defaults;
`PUT /settings/recording` validates, saves and applies them, or returns an error per field (HTTP 400).

**Recordings.** Binary append-only files, by default in `<data folder>/recordings` (see Recording).

A later step adds DuckDB for chart history.

## Repository

Everything lives in one repository: the engine, backend, user interface, desktop shell, tests and
benchmarks. .NET Aspire is not used; the only process orchestration needed is Tauri starting the
backend.
