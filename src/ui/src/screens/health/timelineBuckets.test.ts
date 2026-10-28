import { describe, expect, it } from "vitest";
import type { MinuteColumns } from "@/api";
import { bucketMinutes, mergeSegments, stateOf, toBuckets } from "@/screens/health/timelineBuckets";

const t0 = Date.parse("2026-10-28T12:00:00Z");
const minute = (n: number) => t0 + n * 60_000;

function columns(
  rows: {
    at: number;
    valid?: number;
    invalid?: number;
    running?: number;
    reconnects?: number;
    gaps?: number;
  }[],
): MinuteColumns {
  return {
    minuteMs: rows.map((r) => minute(r.at)),
    running: rows.map((r) => r.running ?? 60),
    valid: rows.map((r) => r.valid ?? 60),
    invalid: rows.map((r) => r.invalid ?? 0),
    reconnects: rows.map((r) => r.reconnects ?? 0),
    gaps: rows.map((r) => r.gaps ?? 0),
  };
}

describe("timeline", () => {
  it("decides each stretch's state: a gap wins, then valid time, else no data", () => {
    expect(stateOf(58, 2, 0)).toBe("invalid");
    expect(stateOf(60, 0, 1)).toBe("invalid");
    expect(stateOf(60, 0, 0)).toBe("valid");
    expect(stateOf(0, 0, 0)).toBe("nodata");
  });

  it("leaves minutes without a row as no data and merges neighbours in the same state", () => {
    const data = columns([
      { at: 0 },
      { at: 1 },
      { at: 2, valid: 55, invalid: 5, gaps: 1, reconnects: 1 },
      { at: 3 },
      // 4 and 5: the app was not running.
      { at: 6 },
      // Running, but never a checked book (waiting for a snapshot).
      { at: 7, valid: 0 },
    ]);

    const buckets = toBuckets(data, minute(0), minute(8), 1);

    expect(buckets.map((b) => b.state)).toEqual([
      "valid",
      "valid",
      "invalid",
      "valid",
      "nodata",
      "nodata",
      "valid",
      "nodata",
    ]);
    expect(buckets[2]).toMatchObject({
      running: 60,
      valid: 55,
      invalid: 5,
      gaps: 1,
      reconnects: 1,
    });
    expect(buckets[4]?.running).toBe(0);
    expect(buckets[7]?.running).toBe(60);
    expect(mergeSegments(buckets)).toEqual([
      { startMs: minute(0), endMs: minute(2), state: "valid" },
      { startMs: minute(2), endMs: minute(3), state: "invalid" },
      { startMs: minute(3), endMs: minute(4), state: "valid" },
      { startMs: minute(4), endMs: minute(6), state: "nodata" },
      { startMs: minute(6), endMs: minute(7), state: "valid" },
      { startMs: minute(7), endMs: minute(8), state: "nodata" },
    ]);
  });

  it("adds minutes up into wider buckets for a week, where any gap marks its bucket", () => {
    const data = columns([
      { at: 0 },
      { at: 3, valid: 50, invalid: 10, gaps: 1 },
      { at: 12 },
      { at: 25, running: 30, valid: 30 },
    ]);

    const buckets = toBuckets(data, minute(0), minute(30), 10);

    expect(buckets.map((b) => b.state)).toEqual(["invalid", "valid", "valid"]);
    expect(buckets.map((b) => b.running)).toEqual([120, 60, 30]);
    expect(buckets.at(-1)?.endMs).toBe(minute(30));
    expect(bucketMinutes(24)).toBe(1);
    expect(bucketMinutes(168)).toBe(10);
  });

  it("ignores rows outside the range and cuts the last bucket at the end", () => {
    const data = columns([{ at: -5 }, { at: 1 }, { at: 99 }]);

    const buckets = toBuckets(data, minute(0), minute(2) + 30_000, 1);

    expect(buckets).toHaveLength(3);
    expect(buckets.map((b) => b.state)).toEqual(["nodata", "valid", "nodata"]);
    expect(buckets[2]?.endMs).toBe(minute(2) + 30_000);
  });
});
