/** The lane's price scale: the mid price in the centre, this fraction of it above and below. */
export const SCALE_FRACTION = 0.0015;

/** Particle diameter in pixels from a notional value in quote currency, on a log scale: 5 px up to 100, 17 px at 1 million. */
export function particleSize(notional: number): number {
  if (!(notional > 0)) {
    return MIN_SIZE;
  }
  return Math.min(MAX_SIZE, Math.max(MIN_SIZE, 5 + 3 * Math.log10(notional / 100)));
}

export const MIN_SIZE = 5;
export const MAX_SIZE = 18;

/** A trade ring's full diameter in pixels from its notional value, on a log scale: 24 px up to 100, 96 px at 1 million. */
export function ringSize(notional: number): number {
  if (!(notional > 0)) {
    return MIN_RING;
  }
  return Math.min(MAX_RING, Math.max(MIN_RING, 24 + 18 * Math.log10(notional / 100)));
}

export const MIN_RING = 24;
export const MAX_RING = 120;

/** Vertical position of a price in a lane of <paramref name="height"/> pixels, clamped to the lane. */
export function priceToY(price: number, mid: number, height: number): number {
  const offset = (price - mid) / (mid * SCALE_FRACTION);
  return Math.min(height, Math.max(0, height / 2 - offset * (height / 2)));
}

/** Moves <paramref name="current"/> towards <paramref name="target"/>, with a time constant of <paramref name="tauMs"/>. */
export function ease(current: number, target: number, dtMs: number, tauMs: number): number {
  return current + (target - current) * (1 - Math.exp(-dtMs / tauMs));
}

export function easeOutCubic(t: number): number {
  const u = 1 - Math.min(1, Math.max(0, t));
  return 1 - u * u * u;
}

/**
 * When each event of a frame appears: spread over the next interval in the order and spacing they happened,
 * so a burst stays a burst and a quiet frame stays quiet.
 */
export function spawnOffsets(timesMs: readonly number[], intervalMs: number): number[] {
  if (timesMs.length === 0) {
    return [];
  }
  const first = Math.min(...timesMs);
  const last = Math.max(...timesMs);
  const span = last - first;
  return timesMs.map((t) => (span > 0 ? ((t - first) / span) * intervalMs : 0));
}

/** "1 in 12" for the drawn tile, or null when nothing was drawn or nearly everything was. */
export function sampleRatio(received: number, drawn: number): string | null {
  if (drawn <= 0 || received < drawn * 1.5) {
    return null;
  }
  return `1 in ${Math.round(received / drawn).toLocaleString("en-US")}`;
}

/** A small deterministic random number generator, so placing particles allocates nothing. */
export function lcg(seed: number): () => number {
  let state = seed >>> 0;
  return () => {
    state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
    return state / 4294967296;
  };
}
