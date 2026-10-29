# Architecture

## Desktop shell and user interface

Marketgrade is a desktop application. The user interface is written in React and TypeScript and
runs inside a Tauri 2 window. Tauri keeps the installer small and uses the operating system's web
view, and its Rust side is responsible for starting and stopping the backend.

**Screens.** A 220 px sidebar (icons with tooltips below 1,200 px) leads to Overview, Live flow, Data
health and Settings; the window opens at 1440 x 900 and is at least 1200 x 760. Navigation uses React
Router in library mode with hash URLs, which work from the app's files and in a browser alike. REST
calls go through TanStack Query (history, settings); everything the backend pushes goes into one
Zustand store. Charts use uPlot; the Live flow animation uses PixiJS 8 with WebGL. The design tokens
(colours, the exchanges' colours, Geist and Geist Mono with tabular figures for every number) are CSS
variables; the app is dark only. Every status has text, every chart pairs colour with a dash pattern
or a label, and keyboard focus is visible.

**Live flow.** One canvas with three lanes. The screen draws the sample the backend sends (see Live
push), spread over each 100 ms as the events happened: sell book changes fall from the top and buy
changes rise from the bottom to their price on a scale of about 0.15 % around the mid price, sized by
notional value on a log scale (5 to 18 px) with a short trail while they move; removals fade where they
are; trades are rings at the mid line, sized by notional value on a log scale (24 to 120 px), each with
a brief bright core. Trades on a lane within 150 ms of a ring's first trade join that ring, which grows
to their combined value, and a lane shows at most 6 rings: a new one makes the oldest fade out over
250 ms. Particles, trails and trade flashes are drawn with additive blending, so they glow where they
overlap; a lane with more than 120 live particles dims them (to the square root of 120 over their
number) so the glow does not burn out, and the lane labels have a dark backdrop. The best 12 levels per
side are depth bars behind them, from the lane's right edge up to 70 % of its width (square root of the
amount), fading out towards the left and spread out from the mid where they would overlap. Faint lines
mark the price scale, and the mid price label flashes green when the mid rises and red when it falls
(an overlay whose opacity alone is animated, so the browser does not repaint). A fixed pool of 2,000
sprites (with 2,000 trails and 30 rings) is reused, with particle state in typed arrays, so the
animation loop allocates nothing; when every sprite is busy a new particle is skipped and counted. The
screen subscribes to the flow only while it is open and the window visible, and stops its render loop
otherwise; Pause freezes the animation while the data keeps coming, and with reduced motion it shows a
still view. PixiJS generates code with `new Function` unless `pixi.js/unsafe-eval` is loaded, which the
app's Content Security Policy requires. The screen and PixiJS load when Live flow is first opened.

**Browser mode.** For automated screenshots the user interface also runs in a normal browser
against a backend started by hand (see the README): only in the Vite dev server and only outside
Tauri, it takes the backend's port and token from `VITE_BACKEND_PORT` and `VITE_BACKEND_TOKEN`
instead of asking Tauri. The module that does this is imported behind `import.meta.env.DEV`, so a
production build does not contain it.

## Backend process

All market logic runs in a single .NET 10 process that Tauri starts as a child process. It
contains the engine (exchange connections, order books, recording) and an ASP.NET Core API that
serves REST endpoints and a SignalR hub (see Live push) to the user interface. The engine is a separate
class library without any ASP.NET Core dependency, so it stays independent of how its data is
served.

## Local API security

The API listens on `127.0.0.1` only, on a port chosen by the operating system, so it is never
reachable from another computer. On every start Tauri generates a random secret token and passes
it to the backend in an environment variable. Every request must carry it as a bearer token, so
other programs on the same computer cannot use the API either. Browsers cannot set headers on a
WebSocket, so for paths under `/hubs/live` only, the token may instead be the `access_token` query
parameter; anywhere else a query token is rejected. Nothing logs request URLs (ASP.NET Core logs
warnings only), and a test checks that the token never appears in the logs. CORS allows only the
Tauri origins and the Vite dev server. `MARKETGRADE_PORT` fixes the port for the browser mode; the
desktop shell never sets it.

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
`Utf8JsonReader` and never allocates; it sorts a message into book data, trade, heartbeat, control or
error. Connection, queues and consumers are shared code.

- Kraken: a `book` subscription with depth 1000 and a `trade` subscription without its snapshot of the
  last 50 trades. Kraken v2 has no heartbeat subscription; heartbeats arrive about once a second as
  soon as any channel is subscribed. Kraken does not restore subscriptions after a reconnect, so they
  are sent on every connection.
- Coinbase: three subscribe messages, because Coinbase accepts one channel per message: `level2`,
  `market_trades` and `heartbeats`. Without the heartbeats channel Coinbase closes quiet connections.
  The level2 snapshot is a single message of several megabytes.
- Binance: the depth stream is named in the URL, and `{"method":"SUBSCRIBE","params":["btcusdt@trade"],"id":1}`
  adds the trade stream after connecting (answered with `{"result":null,"id":1}`). A raw `/ws/`
  connection keeps payloads unwrapped after a subscribe, so both arrive as plain events. We use the
  market-data-only host `data-stream.binance.vision` (port 443) rather than `stream.binance.com:9443`:
  it serves the same public streams, has no account features, and port 443 passes restrictive
  networks. Binance sends a ping frame every 20 seconds; `ClientWebSocket` answers pings with pongs by
  itself.

**Buffers.** Each message is received into a `MessageBuffer` from a pool of 1,024 buffers of 16 KB
created at startup. A larger message continues in an array rented from `ArrayPool<byte>.Shared`,
which goes back to `ArrayPool` when the buffer returns to the pool. If the pool is empty, a new buffer
is created and counted as a pool miss. Every buffer is stamped with its arrival time (Unix
nanoseconds), a sequence number per exchange and the connection number, then written to the queue of
each consumer. Each consumer releases the buffer as its last action; the last release returns it to
the pool. After startup, receiving, stamping, queueing, classifying, recording and releasing allocate
nothing per message, which the benchmarks check.

**Queues.** Every consumer has its own bounded queue per exchange with one reader: the book worker,
which counts messages by kind and keeps the order book (see Order books), and the recorder (see
Recording). A full book queue (8,192 messages) makes the connection wait instead of dropping messages. The recorder's queue is never waited
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
publishes it by swapping a reference, so readers never lock. Messages per second counts book data
messages only (the user interface calls them book updates); trade messages, trades and trades per
second are counted separately. The backend serves it at `GET /engine/status`. The `Marketgrade.Engine` meter has the counters
`messages_received`, `bytes_received`, `reconnects` and `pool_misses` and the gauges `queue_length`
and `connection_state`, all tagged by exchange.

## Recording

Every message from every exchange is written, exactly as received, to binary files in the recordings
folder. The format is described in [recording-format.md](recording-format.md).

**Consumer.** Each exchange has one recorder, a second consumer next to the book worker, with its
own queue of 2,048 messages. Every queued message holds a 16 KB pool buffer, so a stalled disk costs at
most about 32 MB per exchange; at a busy Coinbase rate of about 100 messages per second that still
covers about 20 seconds of stall.
A larger queue would survive longer stalls but hold proportionally more memory.

**Drop policy.** Recording must never slow live monitoring. The connection waits on the book queue but
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

## Order books

Each exchange has a live in-memory order book, checked after every message with the exchange's own
method and repaired by itself when a check fails.

**Pair rules.** Books store prices and amounts as `long` values scaled by the pair's decimals, so
`80368.5` with 2 price decimals is `8036850`. The decimals come from each exchange's public REST API
when the backend starts: Kraken `AssetPairs` (`pair_decimals`, `lot_decimals`), the Coinbase public
product endpoint (decimals of `price_increment` and `base_increment`) and Binance `exchangeInfo`
(decimals of the `PRICE_FILTER` tick size and the `LOT_SIZE` step size). For BTC they are 1 and 8 on
Kraken, 2 and 8 on Coinbase, 2 and 5 on Binance. The fetch runs in the background, so the backend is
ready at once; every fetched set is saved in SQLite, and an exchange that cannot be reached starts
with its cached rules, so the books also work offline. An exchange with neither has state
`NoPairRules`: its connection and recording run, but it keeps no book.

**Parsing.** Messages are read with `Utf8JsonReader` straight into book changes, with no intermediate
objects. Decimal text (a JSON string, or a JSON number for Kraken) becomes a scaled `long`; more
decimals than the pair has are accepted only if they are zeros (Binance sends amounts with 8 decimals
for a step of 5). Signs, exponents and values that do not fit are parse errors, which make the book
invalid like a failed check. Exchange timestamps are parsed from ISO 8601 text (Kraken, Coinbase) or
milliseconds (Binance). Each handler relies on the property order the exchanges send (Kraken `type`
before `data`, Binance `U` and `u` before the levels); a message in another order is a parse error,
which fails safe and shows in the replay counts.

**Storage.** Each side is two `long[]` arrays (prices, amounts) and a count, sorted so that the best
level is the last element: almost every change is near the best price, so inserting or deleting by
shifting moves only the few levels above it. Ask prices are stored negated, which puts the lowest ask
last and lets both sides share one binary search. A full side doubles and logs a warning with its new
size. Starting capacities are 2,000 levels per side for Kraken (we keep 1,000; an update can add a few
before the cut), 50,000 for Coinbase and 20,000 for Binance (both in `EngineOptions`). The most levels
each side ever held is tracked and reported for tuning these defaults.

**Book worker.** The book worker replaces step 3's stats consumer: it still classifies and counts
every message, and it also parses, applies and checks it, records the statistics and releases the
buffer last. Each exchange has one, on its own background thread `book-<exchange>`. When its queue is
empty it waits on a `ManualResetEventSlim` that the connection sets after every write, through a
small `ChannelWriter` wrapper, and then drains everything queued. A manual reset event fits better
than a semaphore: setting an event that is already set is almost free, one wake drains the whole
queue (a semaphore needs a lock per message on both sides and its count grows), it spins briefly
before blocking, so it wakes quickly, and waiting allocates nothing. The wait times out every 250 ms
for housekeeping: statistics, connection checks, Binance snapshots and book snapshots. Parsing,
applying, checking, statistics and releasing allocate nothing per message.

**Checks and repair.**

| Exchange | Snapshot from | Check after every message | Repair |
| -------- | ------------- | ------------------------- | ------ |
| Kraken   | WebSocket `book` (depth 1000) | CRC32 of the top 10 levels per side | reconnect, new snapshot |
| Coinbase | WebSocket `level2` snapshot event | `sequence_num` is the previous one + 1 | reconnect, new snapshot |
| Binance  | REST `GET /api/v3/depth?limit=5000` | update ID chain (`U`, `u`) | new REST snapshot, same connection |

- Kraken: a snapshot replaces the book. After every message both sides are cut to 1,000 levels,
  because Kraken sends no removal for levels that fall out of range, and the checksum must match.
  The checksum text is each level's price and quantity at the pair's full precision without the
  decimal point and leading zeros, asks first, best first, and that is exactly the decimal digits of
  our scaled values. It is built in a `stackalloc` buffer and hashed with `System.IO.Hashing.Crc32`.
  Kraken's documented example (checksum 3310070434) is a unit test.
- Coinbase: `sequence_num` counts every message of a connection, whatever its channel. Measured on a
  step 3 recording: 1,497 messages on one connection (1,420 `level2`, 75 heartbeats, 2 subscriptions)
  numbered 0 to 1,496 with no break; `level2` messages alone jump 76 times. The Coinbase docs disagree
  on this (the level2 page says per connection, the overview says per product). So every message,
  heartbeats included, must carry the previous number plus one, and the first number of a connection
  is its start.
- Binance follows its documented algorithm. While a snapshot is fetched, diff events are held in their
  pooled buffers (at most 2,000; beyond that they are released and holding starts over). The snapshot
  replaces the book, held and new events with `u <= lastUpdateId` are dropped, and the first event
  applied must have `U <= lastUpdateId + 1 <= u`. After that an event with `u` at or below the book's
  ID is ignored, and one with `U` above the book's ID + 1 means events were missed. Snapshots come from
  the market-data host `data-api.binance.vision` at most once every 10 seconds, because one costs
  request weight 250. HTTP 429 and 418 are respected: the next fetch waits for `Retry-After`, and the
  wait is logged and shown as an event. Events are only held while a fetch is running, so a long
  back-off never ties up pool buffers. Every snapshot response is recorded as a `Snapshot` record,
  so a replay can rebuild Binance books.

**Gaps.** When a check fails the book becomes `Invalid` and a gap opens, with its start time, cause
(including the numbers involved, such as expected and received sequence numbers) and our sequence
number. The worker logs a warning, adds a "Book invalid" event and writes a `BookReset` record, then
asks for the repair: `ExchangeConnection.RequestReconnect` for Kraken and Coinbase (thread-safe; it
uses the normal reconnect delays, so a bug can never cause a reconnect storm) or a new snapshot for
Binance. Messages of the broken connection are skipped. Once a new snapshot is applied and checked
the book is `Valid` again, and the gap gets its end time and duration ("Book valid" event). A valid
book also becomes invalid when its connection is lost or replaced, because it missed whatever
happened in between; stopping the app is not a gap. Gaps are counted per UTC day. When the
recorder's queue is full, a `Snapshot` or `BookReset` record is dropped, counted with the dropped
messages and logged; the replay then reports the Binance range as missing a snapshot.

**Timings.** Every time is measured per message and per exchange:

- *Arrival time* is the wall-clock time (`DateTime.UtcNow`, Unix nanoseconds) at which the connection
  has received a message's last byte, before it is queued. A monotonic `Stopwatch` timestamp is taken
  at the same moment.
- *Exchange timestamp* is the time the exchange puts on the message: Kraken's `timestamp` in each
  `data` entry, Coinbase's envelope `timestamp` (when Coinbase sent the message) and Binance's `E`
  (event time, in milliseconds).
- *Delay* = arrival time − exchange timestamp − clock offset. It covers the way from the exchange to
  this computer, nothing after the arrival time. Catch-up messages are not part of it (below).
- *Catch-up*: a message whose arrival time is at most 100 ms after a snapshot's (the snapshot itself
  included) is catch-up. For Kraken and Coinbase the snapshot is the WebSocket message that rebuilt
  the book; for Binance the window starts when the REST snapshot is applied. Coinbase sends its
  4.5 MB snapshot as one message, and the updates sent while it travels queue behind it and arrive
  together right after it: their delay is the snapshot's transfer time (300 to 900 ms measured), not
  the exchange's. Catch-up messages are left out of the delay histogram and counted instead: how
  many since the last snapshot and their largest delay, both reset by the next snapshot.
- *Exchange hold time* (Coinbase only) = the message's envelope `timestamp` − the oldest `event_time`
  of its updates: how long Coinbase held the changes before sending them. Snapshot messages are left
  out, because their event times are the levels' history; an `event_time` that cannot be read only
  drops that sample. Kraken and Binance do not report the time of each change.
- *Queue wait* = from the monotonic timestamp at arrival to the book worker starting on the message.
- *Processing time* = from before parsing to after the check, with `Stopwatch.GetTimestamp()`.
- *Negative delays*: a delay below zero (an exchange timestamp later than the corrected arrival time)
  counts as zero in the histogram and is also counted apart, with how far below zero it went (its own
  histogram, of which the 99th percentile is reported). The status has both for the one-minute
  window, the history for each minute (`delay_negative`, `delay_ahead_ms`). When more than 10 % of an
  exchange's delays in the window, the minute or the Data health range are negative, its delays there
  are a lower bound: the cards, the Data health table and the chart's readout show them with "≤" and
  the note "Exchange timestamps are often ahead of internet time by about X ms, so this delay is a
  lower bound." Signed delays are not shown.

Signed delays measured over 10 minutes on 29 October (`Marketgrade.Reader delays`, below), with the
clock 2.2 s behind NTP and corrected for it: no exchange had a negative delay. Kraken's lowest was
7.6 ms (1st percentile 9.1 ms, median 20 ms), Coinbase's 60 ms (median 64 ms), Binance's 155 ms
(median 158 ms). The 0 ms Kraken median seen in step 5 came from the clock offset, not from Kraken's
clock: replaying that night's Kraken recording with each offset measured that night (−8.8, −14.7 and
−28.5 ms) gives 47 %, 22 % and 0 % negative delays. One SNTP answer can be off by up to half its round
trip (about ±10 ms here), and this clock gained about 0.45 ms a minute on NTP between measurements,
so the offset can be wrong by more than Kraken's 8 ms network delay.
`dotnet run --project tools/Marketgrade.Reader -- delays <folder> --offset-ms X` replays recordings
and prints each exchange's signed delays, with X the status's `clockOffsetMs` at the time.

Measured on a quiet October evening: Coinbase's delay is about 55 ms in steady state, and each connect
used to push its p99 to 600 to 900 ms for a minute, which is now catch-up; Coinbase holds changes for
34 ms at the median and about 300 ms at the 99th percentile before sending them. Queue wait is about
36 µs at the median and 100 µs at the 99th percentile, and 144 ms for the messages that wait while the
Coinbase snapshot is applied.

**Clock offset.** The backend measures the offset with a small SNTP client (UDP port 123,
`time.apple.com`, then `pool.ntp.org`) at start and every 10 minutes, and passes it to the engine; the
offset is the local clock minus NTP time. Without an answer the offset is unknown, delays are shown
uncorrected with a warning, and the next attempt is after a minute; an offset above 50 ms is shown
with a warning too. The development machine's clock drifted from 16 to 80 ms behind NTP within a
few days, and was 2.2 s behind a week later, which makes uncorrected delays meaningless.

**Statistics.** Delay, exchange hold time, queue wait and processing time each go into a log-linear
histogram per exchange: exact up to 63 ns, then 64 buckets per power of two, so a percentile is
within 1.6 % of the true value; the arrays are allocated once. A ring of six 10-second histograms
covers the last minute (50 to 60 seconds). Once per second the worker merges the ring and publishes
the median and 99th percentile. A negative value (for a delay, the clocks disagree) counts as 0.

**Status and API.** `GET /engine/status` adds per exchange: book state (`NoPairRules`, `Syncing`,
`Valid`, `Invalid`), best bid, best ask and spread (text with the pair's decimals), level counts and
the most seen, delay median and 99th percentile in milliseconds, processing median and 99th
percentile in nanoseconds, queue wait median and 99th percentile in nanoseconds, catch-up count
and largest catch-up delay, exchange hold time median and 99th percentile in milliseconds (Coinbase
only, null elsewhere), gaps today and the last gap; and the clock offset for the whole engine.
`GET /books/{exchange}?levels=20` returns up to 100 best levels per side as `[price, amount]` strings,
from an immutable copy the worker publishes by swapping a reference, so readers never lock. Copies
are published at most 10 times per second and only while someone has read one in the last
5 seconds, so nothing is allocated while the endpoint is not used; the first read after a quiet
period may get an older copy (it carries its time) or HTTP 503 before the first one.

**Replay.** `Marketgrade.Reader replay` feeds recorded records through the same handlers, single-
threaded: a `ConnectionOpened` record starts a new book, a `Snapshot` record rebuilds a Binance book,
and a recorded `Gap` waits for the next snapshot. It reports the checks per exchange and matches every
failure with the live book's `BookReset` records. A Binance replay that waits more than 20 seconds of
recorded time for a snapshot (longer than the 10-second limit plus the 10-second fetch timeout) reports
that range as missing a snapshot instead of counting failures. A recorded minute per exchange is
replayed in the tests.

**Trades.** Each book handler also reads its exchange's trades with the same parsers: time (the
exchange's time of the trade), price, amount and the side of the order that took liquidity. Kraken
reports the taker's side. Coinbase's `side` is documented as the maker's side, and live trades
confirm it (437 of 442 trades were at the price that side implies, against the best bid and ask just
before), so the taker is the other side. On Binance `m` says whether the buyer was the maker; if so
the taker sold. Trades an exchange sends as history right after subscribing (Kraken's snapshot,
Coinbase's `snapshot` event) are skipped. Trades never touch the book or its check, and a trade
message that cannot be read is counted and skipped. The exception is Coinbase: its `market_trades`
messages are part of the connection's `sequence_num` sequence, like heartbeats, so they keep the
sequence check going, and one that cannot be read before its `sequence_num` fails the book because
its place in the sequence is unknown. Trade messages do not add delay samples. Replay counts trades.

**Flow.** Each book worker keeps a preallocated ring of 4,096 flow events (`long` time, `byte` kind,
`byte` side, `long` price, `long` amount; 32 bytes each). It writes a trade event for every trade, and
for every update that changes a level of a valid book within 0.25 % of the mid price a book change
event with the signed change in amount at that level (`OrderBookSide.Set` returns the previous
amount); snapshots rebuild the book and write nothing. Every change to a valid book is also counted,
at any distance from the mid. The worker is the only writer: it stores the event, then the number of
events written with `Interlocked.Exchange`, a full barrier, so a reader that sees N knows that only
the slot of event N may be changing. A reader copies a range by sequence number, re-reads the count
and leaves out every event the writer may have overwritten meanwhile, reporting how many. The rings
are written only while the backend asks for the flow; the counts go on either way.

**Per-minute statistics.** Besides the one-minute window of the status, each worker fills a second
set of preallocated histograms (delay, processing, exchange hold time) and counters for the current
UTC minute. At each housekeeping pass (at least every 250 ms) it adds the time since the previous
pass to the connection and book state of that moment, so a minute knows how long the app ran and how
long the connection was live and the book valid or invalid; waiting for a snapshot is neither. A
pause of more than 5 seconds (the computer slept) or a clock moved back is not running time. When the
minute is over the worker publishes a `MinuteStats`: those seconds, book updates, trades, bytes, the
delay median, 99th percentile and exact maximum, the delays in 48 coarse buckets, processing and hold
time percentiles, catch-up messages, reconnects, gaps started and the closing best bid, ask and
spread. The minute the engine stops in is published too. Each completed gap is published once with
its kind, cause and repair action (a gap still open at stop ends there), and each live connection
that ends as a reconnect with its reason. All three go to bounded queues that keep the newest
records; the engine only ever calls `TryWrite`.

**Measured.**

On the development machine (Intel Core i7-9750H), with 500 real messages after the first valid
book of each exchange's recorded minute, BenchmarkDotNet medians per message, all with 0 bytes
allocated:

| Exchange | Parse, apply, check | Whole worker path |
| -------- | ------------------: | ----------------: |
| Kraken   |              4.0 µs |            2.4 µs |
| Coinbase |              6.9 µs |            7.0 µs |
| Binance  |              9.4 µs |            9.5 µs |

The whole worker path adds classifying, statistics and the release; Kraken's numbers vary between
runs (standard deviation about 1.4 µs). With the flow rings on, the worker path is 0.1 to 0.2 µs
slower; writing one flow event takes about 12 ns. Handling one trade message (classify, parse, write
to the ring) takes 0.5 to 0.6 µs. All of these allocate nothing. Parsing one decimal takes 27 ns, one ISO 8601 timestamp 35 ns,
and the Kraken checksum 400 ns. Receiving, queueing, waking the worker, classifying and releasing a
message (the hot path benchmark, without a book) stay at 530 to 600 ns, 680 to 820 ns with recording.

Live, during a 90-second probe run (October 2026, a quiet night), the medians were higher: 7 µs on
Kraken, 43 µs on Coinbase and 45 µs on Binance. Most of the difference is changes far from the best
price, which shift most of a 20,000-level side, and colder caches after the worker sleeps. The most
levels per side were 1,001 on Kraken (1,000 after the cut), 19,617 bids and 20,913 asks on Coinbase,
and 5,217 bids and 5,005 asks on Binance (its snapshot has 5,000 per side, and the book grows from
there), well within the starting capacities. The live books and their replays held exactly the same
number of levels.

## Storage

**Data folder.** Tauri passes the operating system's app data folder (for example
`~/Library/Application Support/io.github.nazarma.marketgrade` on macOS) to the backend in the
`MARKETGRADE_DATA_DIR` environment variable, in development and production alike. Without it, as in
the probe tool and tests, the backend uses `Marketgrade` in the local application data folder.

**Settings.** Settings live in SQLite at `<data folder>/marketgrade.db`, accessed with
`Microsoft.Data.Sqlite` (no Entity Framework): one table `settings(key, value, updated_at)` with one
row per section and its values as JSON, and `pair_rules(exchange_id, symbol, price_decimals,
amount_decimals, updated_at)` with the last pair rules fetched from each exchange (see Order books). The schema version is `PRAGMA user_version`, and migrations
are an ordered list applied at startup. `GET /settings/recording` returns the values and the defaults;
`PUT /settings/recording` validates, saves and applies them, or returns an error per field (HTTP 400).

**Recordings.** Binary append-only files, by default in `<data folder>/recordings` (see Recording).

**History.** Chart history lives in DuckDB at `<data folder>/history.duckdb` (DuckDB.NET), in three
tables: `minute_stats` (every `MinuteStats` field, the coarse delay histogram as a `BIGINT[]` list),
`gaps` and `reconnects`; a `schema` table holds the migrations applied. One background writer, never
an engine thread, reads the engine's queues: after the first row of a minute it waits a second, so all
exchanges' rows of that minute go in one transaction, and it adds gaps and reconnects as they arrive.
It is registered before the engine, so it stops after it and saves what the engine published while
stopping. One connection is used under a lock: a few rows a minute and rare reads never wait long.
At start and then daily it deletes history older than "Keep history for" (a History settings section,
7 to 3,650 days, default 90, stored like the recording settings); a change applies at once.

- A minute without a row is a minute the app was not running, and the user interface shows it as
  no data, never as down. A minute with two rows (the app restarted within it) is added up.
- *Uptime* over a range is the seconds with a valid book divided by the seconds the app was running;
  the tooltip says how many hours it ran.
- Delay percentiles over a range come from the minutes' coarse histograms added up: 48 buckets of
  equal ratio (about 1.257) from 1 ms to 60 s, with the geometric middle of a bucket as the value, so
  they are approximate, within about 12 %. Values under 1 ms count in the first bucket and show as
  about 1.1 ms. The worst delay is exact.

`GET /history/delay?minutes=60` returns the median and 99th percentile per exchange and minute;
`GET /history/health?hours=24` (up to 168) the running, valid and invalid seconds, reconnects and gaps
per minute as parallel arrays, the reconnects with their reasons and the range's aggregates; and
`GET /history/gaps?hours=24` the gaps newest first.

DuckDB's macOS library is a universal binary of 117 MB, which makes the published sidecar 221 MB
instead of about 104 MB; keeping only the target's architecture would save about 58 MB.

## Live push

The SignalR hub `/hubs/live` (JSON protocol, enums as strings) replaces polling. The user interface
reads `/engine/status` once for its first state, then connects with WebSockets only, without
negotiation, and the token in the query string; it reconnects after 0, 1, 2, 4, 8 and 15 seconds and
then shows the backend-unreachable screen.

| Message | To | When | Content |
| ------- | -- | ---- | ------- |
| `status` | every client | once per second | the full `EngineStatus` |
| `event` | every client | as it happens | a connection, book or recording event (for example "09:00 to 10:00 UTC file, 214 MB to 31 MB") |
| `flow` | clients that called `SubscribeFlow` | every 100 ms | per exchange: counts of all book changes and trades in the interval, a sample, events overwritten before sampling, the mid price and 12 levels per side |

The sample is every trade up to 50 and the 40 largest book changes by notional value, oldest first.
While at least one client is subscribed the backend turns on the engine's flow rings and samples them
every 100 ms; `UnsubscribeFlow` or a closed connection ends a subscription, and without one nothing is
sampled, sent or written to the rings. The hub messages sent are counted by type in the
`Marketgrade.Backend` meter (`hub_messages_sent`), and the user interface counts what it receives.

## Repository

Everything lives in one repository: the engine, backend, user interface, desktop shell, tests and
benchmarks. .NET Aspire is not used; the only process orchestration needed is Tauri starting the
backend.
