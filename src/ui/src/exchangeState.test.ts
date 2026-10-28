import { describe, expect, it } from "vitest";
import type { BookGap, BookState, ConnectionState } from "./api";
import {
  bookBadge,
  clockOffset,
  formatCatchUp,
  formatDelay,
  formatGap,
  formatProcessing,
  formatSinceLastMessage,
  stateBadge,
} from "./exchangeState";

describe("stateBadge", () => {
  it.each<[ConnectionState, string, string]>([
    ["Live", "Live", "text-buy"],
    ["Connecting", "Connecting", "text-warning"],
    ["Reconnecting", "Reconnecting", "text-warning"],
    ["Blocked", "Blocked in your region", "text-warning"],
    ["Stopped", "Stopped", "text-sell"],
  ])("shows %s as text %s in %s", (state, label, colour) => {
    const badge = stateBadge(state);

    expect(badge.label).toBe(label);
    expect(badge.className).toContain(colour);
  });

  it("shows an unknown state as text in the sell colour", () => {
    const badge = stateBadge("Exploded" as ConnectionState);

    expect(badge).toEqual({ label: "Exploded", className: "bg-sell/15 text-sell" });
  });
});

describe("formatSinceLastMessage", () => {
  it("shows seconds with one decimal, or a dash before the first message", () => {
    expect(formatSinceLastMessage(1234)).toBe("1.2 s");
    expect(formatSinceLastMessage(null)).toBe("–");
  });
});

describe("bookBadge", () => {
  it.each<[BookState, string, string]>([
    ["Valid", "Valid", "text-buy"],
    ["Syncing", "Syncing", "text-warning"],
    ["Invalid", "Invalid", "text-sell"],
    ["NoPairRules", "No pair rules", "text-muted-foreground"],
  ])("shows %s as text %s in %s", (state, label, colour) => {
    const badge = bookBadge(state);

    expect(badge.label).toBe(label);
    expect(badge.className).toContain(colour);
  });
});

describe("formatDelay and formatProcessing", () => {
  it("show the median and the 99th percentile, or a dash without data", () => {
    expect(formatDelay(8.04, 692.16)).toBe("8.0 / 692.2 ms");
    expect(formatDelay(null, null)).toBe("–");
    expect(formatProcessing(7264, 43264.4)).toBe("7,264 / 43,264 ns");
    expect(formatProcessing(null, null)).toBe("–");
  });
});

describe("formatCatchUp", () => {
  it("shows the count and the largest delay, or nothing without catch-up", () => {
    expect(formatCatchUp(23, 941.04)).toBe("catch-up 23, max 941 ms");
    expect(formatCatchUp(0, null)).toBeNull();
  });
});

describe("formatGap", () => {
  const gap: BookGap = {
    startUtc: "2026-10-26T00:00:00Z",
    endUtc: "2026-10-26T00:00:03.2Z",
    cause: "Checksum mismatch",
    startSeq: 10,
    endSeq: 40,
    durationSeconds: 3.2,
  };

  it("names the cause and the duration, or says the gap is still open", () => {
    expect(formatGap(gap)).toBe("Last gap: Checksum mismatch (3.2 s)");
    expect(formatGap({ ...gap, endUtc: null, durationSeconds: null })).toBe(
      "Last gap: Checksum mismatch (still open)",
    );
    expect(formatGap(null)).toBeUndefined();
  });
});

describe("clockOffset", () => {
  it("shows a small offset without a warning", () => {
    expect(clockOffset(-30.33)).toEqual({ label: "Clock offset -30.3 ms", warning: null });
  });

  it("warns when the offset is over 50 ms or unknown", () => {
    expect(clockOffset(75).label).toBe("Clock offset +75.0 ms");
    expect(clockOffset(75).warning).toContain("more than 50 ms off");
    expect(clockOffset(-51).warning).not.toBeNull();
    expect(clockOffset(null)).toEqual({
      label: "Clock offset unknown",
      warning: "No time server answered, so delays are not corrected for this computer's clock.",
    });
  });
});
