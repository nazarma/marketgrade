import { describe, expect, it } from "vitest";
import { formatDelay, lowerBoundNote } from "@/lib/delay";

describe("delays", () => {
  it("marks a lower bound, but not a missing value", () => {
    expect(formatDelay(12.3, true)).toBe("≤ 12 ms");
    expect(formatDelay(12.3, false)).toBe("12 ms");
    expect(formatDelay(null, true)).toBe("–");
  });

  it("explains a lower bound with how far ahead the timestamps were", () => {
    expect(lowerBoundNote(6.54)).toBe(
      "Exchange timestamps are often ahead of internet time by about 6.5 ms, so this delay is a lower bound.",
    );
    expect(lowerBoundNote(0.2)).toContain("about 1.0 ms");
    expect(lowerBoundNote(null)).toContain("about a few ms");
  });
});
