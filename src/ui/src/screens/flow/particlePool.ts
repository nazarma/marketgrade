/**
 * A fixed set of slots, reused for ever: acquiring takes a free slot, releasing returns it. When every slot
 * is in use a new particle is skipped and counted, never allocated. Free slots are a stack of indexes, so
 * nothing is allocated after construction.
 */
export class ParticlePool {
  readonly size: number;
  private readonly free: Int32Array;
  private readonly inUse: Uint8Array;
  private freeCount: number;
  /** Particles that could not be drawn because every slot was in use. */
  skipped = 0;

  constructor(size: number) {
    this.size = size;
    this.free = new Int32Array(size);
    this.inUse = new Uint8Array(size);
    for (let i = 0; i < size; i++) {
      this.free[i] = size - 1 - i;
    }
    this.freeCount = size;
  }

  get active(): number {
    return this.size - this.freeCount;
  }

  /** A free slot's index, or -1 (counted as skipped) when there is none. */
  acquire(): number {
    if (this.freeCount === 0) {
      this.skipped++;
      return -1;
    }
    const index = this.free[--this.freeCount] as number;
    this.inUse[index] = 1;
    return index;
  }

  release(index: number): void {
    if (this.inUse[index] !== 1) {
      return;
    }
    this.inUse[index] = 0;
    this.free[this.freeCount++] = index;
  }

  isActive(index: number): boolean {
    return this.inUse[index] === 1;
  }
}

/** One event waiting for its moment to appear. */
export interface PendingSpawn {
  lane: number;
  at: number;
  trade: boolean;
  sell: boolean;
  price: number;
  amount: number;
}

/**
 * Events of the latest frames, each due at a time within the next frame interval, in a preallocated ring.
 * When it is full the oldest waiting events are dropped and counted.
 */
export class SpawnQueue {
  private readonly items: PendingSpawn[];
  private head = 0;
  private count = 0;
  dropped = 0;

  constructor(capacity: number) {
    this.items = Array.from({ length: capacity }, () => ({
      lane: 0,
      at: 0,
      trade: false,
      sell: false,
      price: 0,
      amount: 0,
    }));
  }

  get length(): number {
    return this.count;
  }

  push(
    lane: number,
    at: number,
    trade: boolean,
    sell: boolean,
    price: number,
    amount: number,
  ): void {
    if (this.count === this.items.length) {
      this.head = (this.head + 1) % this.items.length;
      this.count--;
      this.dropped++;
    }
    const item = this.items[(this.head + this.count) % this.items.length] as PendingSpawn;
    item.lane = lane;
    item.at = at;
    item.trade = trade;
    item.sell = sell;
    item.price = price;
    item.amount = amount;
    this.count++;
  }

  /** The oldest event if it is due at <paramref name="now"/>; the item is reused, so read it before the next push. */
  takeDue(now: number): PendingSpawn | null {
    if (this.count === 0) {
      return null;
    }
    const item = this.items[this.head] as PendingSpawn;
    if (item.at > now) {
      return null;
    }
    this.head = (this.head + 1) % this.items.length;
    this.count--;
    return item;
  }

  clear(): void {
    this.head = 0;
    this.count = 0;
  }
}
