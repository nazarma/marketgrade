import { describe, expect, it } from "vitest";
import type { ConnectionState } from "./api";
import { formatSinceLastMessage, stateBadge } from "./exchangeState";

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
