import { formatMs } from "@/lib/format";

/**
 * A delay, with "≤" when it is a lower bound: so many of its messages arrived before their exchange
 * timestamp by internet time (more than 10 %) that the engine counted them as zero delay.
 */
export function formatDelay(ms: number | null | undefined, lowerBound: boolean): string {
  const text = formatMs(ms);
  return lowerBound && ms !== null && ms !== undefined ? `≤ ${text}` : text;
}

/** The explanation shown with a lower-bound delay; <paramref name="aheadMs"/> is how far ahead the timestamps were. */
export function lowerBoundNote(aheadMs: number | null | undefined): string {
  const ahead =
    aheadMs === null || aheadMs === undefined ? "a few ms" : formatMs(Math.max(1, aheadMs));
  return `Exchange timestamps are often ahead of internet time by about ${ahead}, so this delay is a lower bound.`;
}
