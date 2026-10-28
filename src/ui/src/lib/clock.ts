export const CLOCK_WARNING_MS = 50;

/** A warning when delays cannot be taken as shown because of this computer's clock, or null. */
export function clockNote(offsetMs: number | null): string | null {
  if (offsetMs === null) {
    return "No time server answered, so delays are not corrected for this computer's clock.";
  }
  if (Math.abs(offsetMs) > CLOCK_WARNING_MS) {
    return `This computer's clock is ${Math.abs(offsetMs).toFixed(0)} ms ${offsetMs > 0 ? "ahead" : "behind"}; delays are corrected for it.`;
  }
  return null;
}
