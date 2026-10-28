import type { MinuteColumns } from "@/api";

const MINUTE_MS = 60_000;

/**
 * A stretch of the timeline. Valid: the book was checked and valid. Invalid: there was a gap, the data was
 * not trusted. No data: the app was not running, or ran without a checked book (waiting for one, blocked).
 */
export type TimelineState = "valid" | "invalid" | "nodata";

export interface Bucket {
  startMs: number;
  endMs: number;
  state: TimelineState;
  /** Seconds the app ran in the bucket; 0 means it was not running at all. */
  running: number;
  valid: number;
  invalid: number;
  reconnects: number;
  gaps: number;
}

export interface Segment {
  startMs: number;
  endMs: number;
  state: TimelineState;
}

/** A gap wins over valid time; valid time wins over none. */
export function stateOf(valid: number, invalid: number, gaps: number): TimelineState {
  if (invalid > 0 || gaps > 0) {
    return "invalid";
  }
  return valid > 0 ? "valid" : "nodata";
}

/** One minute per bucket for a day; longer ranges need fewer, wider buckets to stay readable. */
export function bucketMinutes(hours: number): number {
  return hours <= 24 ? 1 : 10;
}

/** The minutes of the range in buckets of {@link minutes} minutes; minutes without a row add nothing. */
export function toBuckets(
  columns: MinuteColumns,
  fromMs: number,
  toMs: number,
  minutes: number,
): Bucket[] {
  const size = minutes * MINUTE_MS;
  const start = Math.floor(fromMs / MINUTE_MS) * MINUTE_MS;
  const count = Math.max(0, Math.ceil((toMs - start) / size));
  const buckets: Bucket[] = Array.from({ length: count }, (_, i) => ({
    startMs: start + i * size,
    endMs: Math.min(start + (i + 1) * size, toMs),
    state: "nodata",
    running: 0,
    valid: 0,
    invalid: 0,
    reconnects: 0,
    gaps: 0,
  }));
  columns.minuteMs.forEach((minute, i) => {
    const bucket = buckets[Math.floor((minute - start) / size)];
    if (!bucket) {
      return;
    }
    bucket.running += columns.running[i] ?? 0;
    bucket.valid += columns.valid[i] ?? 0;
    bucket.invalid += columns.invalid[i] ?? 0;
    bucket.reconnects += columns.reconnects[i] ?? 0;
    bucket.gaps += columns.gaps[i] ?? 0;
  });
  for (const bucket of buckets) {
    bucket.state = stateOf(bucket.valid, bucket.invalid, bucket.gaps);
  }
  return buckets;
}

/** Neighbouring buckets in the same state as one segment. */
export function mergeSegments(buckets: Bucket[]): Segment[] {
  const segments: Segment[] = [];
  for (const bucket of buckets) {
    const last = segments.at(-1);
    if (last && last.state === bucket.state && last.endMs === bucket.startMs) {
      last.endMs = bucket.endMs;
    } else {
      segments.push({ startMs: bucket.startMs, endMs: bucket.endMs, state: bucket.state });
    }
  }
  return segments;
}
