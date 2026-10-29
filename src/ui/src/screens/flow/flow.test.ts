import { describe, expect, it } from "vitest";
import {
  ease,
  lcg,
  MAX_RING,
  MAX_SIZE,
  MIN_RING,
  MIN_SIZE,
  particleSize,
  priceToY,
  ringSize,
  sampleRatio,
  spawnOffsets,
} from "@/screens/flow/flowMath";
import { ParticlePool, SpawnQueue } from "@/screens/flow/particlePool";
import { RINGS_PER_LANE, TradeRings } from "@/screens/flow/tradeRings";

describe("particle pool", () => {
  it("hands out every slot once, then skips and counts", () => {
    const pool = new ParticlePool(3);

    const taken = [pool.acquire(), pool.acquire(), pool.acquire()];

    expect(new Set(taken)).toEqual(new Set([0, 1, 2]));
    expect(pool.acquire()).toBe(-1);
    expect(pool.acquire()).toBe(-1);
    expect(pool.skipped).toBe(2);
    expect(pool.active).toBe(3);
  });

  it("reuses released slots and ignores a double release", () => {
    const pool = new ParticlePool(2);
    const a = pool.acquire();
    pool.acquire();

    pool.release(a);
    pool.release(a);

    expect(pool.active).toBe(1);
    expect(pool.acquire()).toBe(a);
    expect(pool.acquire()).toBe(-1);
    expect(pool.isActive(a)).toBe(true);
  });
});

describe("spawn queue", () => {
  it("gives events out when they are due, oldest first", () => {
    const queue = new SpawnQueue(4);
    queue.push(0, 10, false, true, 81_000, 0.5);
    queue.push(1, 20, true, false, 81_001, 0.1);

    expect(queue.takeDue(5)).toBeNull();
    expect(queue.takeDue(15)).toMatchObject({ lane: 0, sell: true, price: 81_000 });
    expect(queue.takeDue(15)).toBeNull();
    expect(queue.takeDue(25)).toMatchObject({ lane: 1, trade: true });
    expect(queue.length).toBe(0);
  });

  it("drops the oldest waiting events when full", () => {
    const queue = new SpawnQueue(2);
    queue.push(0, 1, false, false, 1, 1);
    queue.push(0, 2, false, false, 2, 1);
    queue.push(0, 3, false, false, 3, 1);

    expect(queue.dropped).toBe(1);
    expect(queue.takeDue(10)?.price).toBe(2);
    expect(queue.takeDue(10)?.price).toBe(3);
  });
});

describe("flow math", () => {
  it("sizes particles by notional value on a log scale, clamped", () => {
    expect(particleSize(100)).toBe(5);
    expect(particleSize(10_000)).toBe(11);
    expect(particleSize(1e9)).toBe(MAX_SIZE);
    expect(particleSize(1)).toBe(MIN_SIZE);
    expect(particleSize(0)).toBe(MIN_SIZE);
    expect(MIN_SIZE).toBe(5);
  });

  it("sizes trade rings by notional value on a log scale, clamped", () => {
    expect(ringSize(100)).toBe(MIN_RING);
    expect(ringSize(10_000)).toBe(60);
    expect(ringSize(1_000_000)).toBe(96);
    expect(ringSize(1e12)).toBe(MAX_RING);
    expect(ringSize(Number.NaN)).toBe(MIN_RING);
  });

  it("puts the mid in the centre and 0.15 % away at the edges", () => {
    expect(priceToY(80_000, 80_000, 500)).toBe(250);
    expect(priceToY(80_120, 80_000, 500)).toBeCloseTo(0, 6);
    expect(priceToY(79_880, 80_000, 500)).toBeCloseTo(500, 6);
    expect(priceToY(90_000, 80_000, 500)).toBe(0);
  });

  it("eases towards a target", () => {
    expect(ease(0, 100, 0, 150)).toBe(0);
    expect(ease(0, 100, 150, 150)).toBeCloseTo(63.2, 1);
    expect(ease(0, 100, 10_000, 150)).toBeCloseTo(100, 6);
  });

  it("spreads a frame's events over the interval as they happened", () => {
    expect(spawnOffsets([1_000, 1_050, 1_100], 100)).toEqual([0, 50, 100]);
    expect(spawnOffsets([5, 5], 100)).toEqual([0, 0]);
    expect(spawnOffsets([], 100)).toEqual([]);
  });

  it("states the sampling ratio", () => {
    expect(sampleRatio(6_218, 520)).toBe("1 in 12");
    expect(sampleRatio(10, 10)).toBeNull();
    expect(sampleRatio(159, 156)).toBeNull();
    expect(sampleRatio(10, 0)).toBeNull();
  });

  it("generates repeatable numbers between 0 and 1", () => {
    const a = lcg(7);
    const b = lcg(7);
    const values = Array.from({ length: 100 }, a);
    expect(values).toEqual(Array.from({ length: 100 }, b));
    expect(values.every((v) => v >= 0 && v < 1)).toBe(true);
  });
});

describe("trade rings", () => {
  it("merges trades on a lane within 150 ms into one ring with their combined value", () => {
    const rings = new TradeRings(30, 3);
    const first = rings.add(0, 1_000, 0);
    expect(rings.merged).toBe(false);

    expect(rings.add(0, 500, 150)).toBe(first);
    expect(rings.merged).toBe(true);
    expect(rings.notional[first]).toBe(1_500);

    expect(rings.add(1, 200, 160)).not.toBe(first);
    const next = rings.add(0, 300, 151);
    expect(next).not.toBe(first);
    expect(rings.merged).toBe(false);
    expect(rings.notional[first]).toBe(1_500);
  });

  it("keeps at most 6 rings per lane, the oldest fading first", () => {
    const rings = new TradeRings(30, 3);
    const lane0 = Array.from({ length: RINGS_PER_LANE }, (_, i) => rings.add(0, 100, i * 200));
    const other = rings.add(1, 100, 50);
    expect(lane0.every((i) => rings.evicted[i] === 0)).toBe(true);

    rings.add(0, 100, 1_300);
    expect(rings.evicted[lane0[0] as number]).toBe(1_300);
    expect(lane0.slice(1).every((i) => rings.evicted[i] === 0)).toBe(true);
    expect(rings.evicted[other]).toBe(0);

    rings.add(0, 100, 1_500);
    expect(rings.evicted[lane0[1] as number]).toBe(1_500);
  });

  it("does not merge into a ring that is gone", () => {
    const rings = new TradeRings(30, 3);
    const first = rings.add(2, 100, 0);
    rings.release(first);
    rings.add(2, 100, 50);
    expect(rings.merged).toBe(false);
  });
});
