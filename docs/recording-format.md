# Recording file format

Marketgrade records every message from every exchange, exactly as received, in binary append-only
files. This document describes version 1 of the format. All numbers are little-endian.

## Folder layout

```
<recordings folder>/<exchange>/<yyyy-MM-dd>/<HH>.mgrec
<recordings folder>/<exchange>/<yyyy-MM-dd>/<HH>.mgrec.zst
```

- `<exchange>` is the exchange name in lower case: `kraken`, `coinbase`, `binance`.
- `<yyyy-MM-dd>` and `<HH>` are the UTC date and hour of the message's arrival time, not the time it
  was written. UTC avoids duplicate or missing hours when daylight saving time starts or ends.
- `.mgrec` is a file being written, or one not compressed yet. `.mgrec.zst` is the same file
  compressed with Zstandard (one zstd frame of the complete `.mgrec` file).
- In rare cases an hour has a second part, `<HH>-2.mgrec` (then `-3`, ...): when the hour's file was
  already compressed and messages for that hour arrive again, for example after recording was turned
  off and on, or after the system clock moved back.
- `.mgrec.zst.tmp` is a compression in progress. A leftover one is deleted on startup.

Marketgrade only ever reads, compresses or deletes files that match this layout exactly.

## File header

| Field             | Type                    | Value                                |
| ----------------- | ----------------------- | ------------------------------------ |
| Magic             | 6 bytes                 | `MGREC\0` (`4D 47 52 45 43 00`)      |
| Format version    | u16                     | 1                                    |
| Exchange id       | u16                     | 1 Kraken, 2 Coinbase, 3 Binance      |
| Exchange name     | u8 length + UTF-8 bytes | for example `Kraken`                 |
| File created      | i64                     | Unix time in nanoseconds             |

The records follow the header directly, with no padding.

## Record

Each record is a fixed 40-byte header followed by its payload.

| Offset | Field              | Type    | Notes                                         |
| -----: | ------------------ | ------- | --------------------------------------------- |
|      0 | Record type        | u8      | see below                                     |
|      1 | Reserved           | 3 bytes | zero                                          |
|      4 | Payload length     | u32     | bytes after the header                        |
|      8 | CRC32 of payload   | u32     | CRC-32 (IEEE), as `System.IO.Hashing.Crc32`   |
|     12 | Reserved           | u32     | zero                                          |
|     16 | Sequence number    | i64     | our sequence number, see below                |
|     24 | Arrival time       | i64     | Unix time in nanoseconds                      |
|     32 | Connection id      | i64     | 1 for the first connection after start, +1 for each reconnect |

### Record types

| Type | Name               | Payload                                                     |
| ---: | ------------------ | ----------------------------------------------------------- |
|    1 | `Message`          | the raw message bytes, exactly as received                  |
|    2 | `ConnectionOpened` | UTF-8 URL of the connection                                 |
|    3 | `ConnectionClosed` | UTF-8 reason, for example `Closed by server: 1000 bye`      |
|    4 | `Gap`              | first seq (i64), last seq (i64), count (i64) of messages not recorded |

### Sequence numbers

Every message of an exchange gets the next sequence number, starting at 1 when the app starts and
continuing across reconnects. `Message` records therefore have contiguous sequence numbers unless a
`Gap` record says otherwise. `ConnectionOpened` and `ConnectionClosed` carry the sequence number of
the last message before them; they do not use up a number. A `Gap` record carries the sequence
number of the last message recorded before the gap. Sequence numbers start at 1 again after the app
restarts.

### Gaps

A `Gap` record is written right before the first record that follows the missing messages. Messages
go missing when:

- the recorder's queue was full (the disk could not keep up), so the connection dropped the message
  for the recorder only; live statistics still saw it;
- recording was paused because the disk was almost full, was in an error state, or was turned off.

## Crash safety

Records are only ever appended. A crash can leave at most one incomplete record at the end of the
file. On startup Marketgrade scans every `.mgrec` file, checks each record's length and CRC, and
truncates the file at the first broken record.

## Reading files

`tools/Marketgrade.Reader` prints, verifies and summarizes `.mgrec` and `.mgrec.zst` files:

```sh
dotnet run --project tools/Marketgrade.Reader -- cat <file> [--limit N] [--payload]
dotnet run --project tools/Marketgrade.Reader -- verify <file or folder>
dotnet run --project tools/Marketgrade.Reader -- stats <recordings folder>
```

`verify` checks every record's length and CRC and exits with code 1 if any file is broken. The file
currently being written can end in the middle of a write; verify it after recording has stopped.
