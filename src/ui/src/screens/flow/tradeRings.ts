import { ParticlePool } from "@/screens/flow/particlePool";

/** A trade within this many milliseconds of a ring's first trade, on the same lane, joins that ring. */
export const MERGE_WINDOW = 150;
/** At most this many rings show per lane; a new one makes the oldest fade out early. */
export const RINGS_PER_LANE = 6;

/**
 * Which ring shows each trade, apart from drawing: trades close together on a lane share one ring with
 * their combined value, and each lane keeps at most {@link RINGS_PER_LANE} rings, the oldest fading first.
 * State is in fixed arrays by slot; nothing is allocated after construction.
 */
export class TradeRings {
  readonly lane: Uint8Array;
  /** When the ring's first trade arrived, in milliseconds. */
  readonly born: Float64Array;
  /** The value of the ring's trades together. */
  readonly notional: Float64Array;
  /** When the ring was pushed out by a newer one and began to fade, or 0. */
  readonly evicted: Float64Array;
  /** Whether the last {@link add} joined an existing ring. */
  merged = false;
  private readonly pool: ParticlePool;
  private readonly latest: Int32Array;

  constructor(size: number, lanes: number) {
    this.pool = new ParticlePool(size);
    this.lane = new Uint8Array(size);
    this.born = new Float64Array(size);
    this.notional = new Float64Array(size);
    this.evicted = new Float64Array(size);
    this.latest = new Int32Array(lanes).fill(-1);
  }

  get size(): number {
    return this.pool.size;
  }

  /** The slot of the ring that shows this trade, or -1 when every slot is in use. */
  add(lane: number, notional: number, now: number): number {
    const latest = this.latest[lane] ?? -1;
    if (
      latest >= 0 &&
      this.pool.isActive(latest) &&
      this.evicted[latest] === 0 &&
      now - (this.born[latest] ?? 0) <= MERGE_WINDOW
    ) {
      this.notional[latest] = (this.notional[latest] ?? 0) + notional;
      this.merged = true;
      return latest;
    }
    this.merged = false;
    let showing = 0;
    let oldest = -1;
    for (let i = 0; i < this.pool.size; i++) {
      if (this.pool.isActive(i) && this.lane[i] === lane && this.evicted[i] === 0) {
        showing++;
        if (oldest < 0 || (this.born[i] ?? 0) < (this.born[oldest] ?? 0)) {
          oldest = i;
        }
      }
    }
    if (showing >= RINGS_PER_LANE && oldest >= 0) {
      this.evicted[oldest] = now;
    }
    const i = this.pool.acquire();
    if (i < 0) {
      return -1;
    }
    this.lane[i] = lane;
    this.born[i] = now;
    this.notional[i] = notional;
    this.evicted[i] = 0;
    this.latest[lane] = i;
    return i;
  }

  isActive(index: number): boolean {
    return this.pool.isActive(index);
  }

  release(index: number): void {
    this.pool.release(index);
    if (this.latest[this.lane[index] ?? 0] === index) {
      this.latest[this.lane[index] ?? 0] = -1;
    }
  }
}
