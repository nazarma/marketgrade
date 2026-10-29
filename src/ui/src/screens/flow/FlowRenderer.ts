// Pixi compiles shaders and uniform uploads with new Function unless this is loaded; the app's CSP forbids eval.
import "pixi.js/unsafe-eval";
import { Application, Particle, ParticleContainer, Sprite, Texture } from "pixi.js";
import type { FlowFrame } from "@/api";
import {
  ease,
  easeOutCubic,
  lcg,
  particleSize,
  priceToY,
  ringSize,
  spawnOffsets,
} from "@/screens/flow/flowMath";
import { ParticlePool, SpawnQueue } from "@/screens/flow/particlePool";
import { RINGS_PER_LANE, TradeRings } from "@/screens/flow/tradeRings";

export const PARTICLES = 2_000;
const LEVELS = 12;
const LANES = 3;
/** A lane starts at most one ring per merge window, so a few slots beyond its cap cover the rings fading out. */
const RINGS = LANES * (RINGS_PER_LANE + 4);
const FRAME_INTERVAL_MS = 100;

const BUY = 0x3ddc97;
const SELL = 0xff6b78;
/** At the right edge; bars fade out towards the left. */
const BAR_ALPHA = 0.5;
const BAR_HEIGHT = 4;
/** The largest level's bar, as a fraction of the lane width. */
const BAR_LENGTH = 0.7;
/** Levels closer than this on the price scale are spread out from the mid, keeping their order. */
const BAR_SPACING = 6;

/** How long things take, in milliseconds. */
const TRAVEL_MIN = 1_100;
const TRAVEL_SPREAD = 700;
const HOLD = 150;
const FADE = 450;
const REMOVAL_FADE = 800;
const RING_LIFE = 1_400;
/** How long a ring pushed out by a newer one takes to fade. */
const EVICT_FADE = 250;
const CORE_LIFE = 320;
/** A travelling particle's trail covers where it was this long ago, up to {@link TRAIL_MAX} pixels. */
const TRAIL = 70;
const TRAIL_MAX = 28;
/** The ring texture's diameter, in its 64 px. */
const RING_TEXTURE_SIZE = 58;
/** Up to this many particles in a lane shine at full brightness; more dim it, so the glow does not burn out. */
const FULL_EXPOSURE = 120;

/** Particle modes. */
const FREE = 0;
const TRAVEL = 1;
const REMOVAL = 2;
const STATIC = 3;

export interface FlowRendererStats {
  fps: number;
  /** Book changes that became particles, per second. */
  drawnPerSecond: number;
  /** Particles skipped because the pool was full, since the start. */
  skipped: number;
}

/**
 * Draws the live flow of three exchanges on one WebGL canvas, one lane each: a mid line, depth bars behind,
 * sell changes falling from the top and buy changes rising from the bottom to their price with a short
 * trail, removals fading where they are, and trades as rings at the mid, sized by their value, with a
 * bright core. Particles, trails and trade flashes add their light (additive blending). It draws a sample
 * of the events it is given, with a fixed pool of sprites; the animation loop allocates nothing.
 */
export class FlowRenderer {
  private readonly app = new Application();
  private readonly pool = new ParticlePool(PARTICLES);
  private readonly ringState = new TradeRings(RINGS, LANES);
  private readonly queue = new SpawnQueue(4_096);
  private readonly random = lcg(7);
  private readonly particles: Particle[] = [];
  private readonly trails: Particle[] = [];
  private readonly rings: Particle[] = [];
  private readonly cores: Particle[] = [];
  private readonly bars: Sprite[] = [];

  // Particle state in parallel arrays, by pool index.
  private readonly mode = new Uint8Array(PARTICLES);
  private readonly lane = new Uint8Array(PARTICLES);
  private readonly sell = new Uint8Array(PARTICLES);
  private readonly x = new Float32Array(PARTICLES);
  private readonly price = new Float64Array(PARTICLES);
  private readonly born = new Float64Array(PARTICLES);
  private readonly travel = new Float32Array(PARTICLES);
  private readonly scale = new Float32Array(PARTICLES);
  private readonly ringX = new Float32Array(RINGS);
  private readonly ringScale = new Float32Array(RINGS);

  // Per lane: the mid price shown (eased) and its target; depth bars' prices and widths.
  private readonly mid = new Float64Array(LANES);
  /** Brightness of each lane's particles, from how many were alive in the last frame. */
  private readonly exposure = new Float32Array(LANES).fill(1);
  private readonly alive = new Uint16Array(LANES);
  private readonly targetMid = new Float64Array(LANES);
  private readonly barPrice = new Float64Array(LANES * LEVELS * 2);
  private readonly barTarget = new Float32Array(LANES * LEVELS * 2);
  private readonly barWidth = new Float32Array(LANES * LEVELS * 2);

  private ready = false;
  private destroyed = false;
  private running = false;
  private staticView = false;
  private frames = 0;
  private drawn = 0;
  private windowStart = 0;
  private stats: FlowRendererStats = { fps: 0, drawnPerSecond: 0, skipped: 0 };

  private readonly container: HTMLElement;
  private readonly onStats: (stats: FlowRendererStats) => void;

  constructor(container: HTMLElement, onStats: (stats: FlowRendererStats) => void) {
    this.container = container;
    this.onStats = onStats;
  }

  async init(): Promise<void> {
    await this.app.init({
      resizeTo: this.container,
      backgroundAlpha: 0,
      antialias: true,
      autoDensity: true,
      resolution: Math.min(window.devicePixelRatio || 1, 2),
      preference: "webgl",
      autoStart: false,
      sharedTicker: false,
    });
    if (this.destroyed) {
      this.app.destroy(true);
      return;
    }
    this.app.canvas.style.display = "block";
    this.container.appendChild(this.app.canvas);

    const fade = fadeTexture();
    for (let i = 0; i < LANES * LEVELS * 2; i++) {
      const bar = new Sprite(fade);
      bar.alpha = BAR_ALPHA;
      bar.tint = i % (LEVELS * 2) < LEVELS ? SELL : BUY;
      bar.visible = false;
      this.bars.push(bar);
      this.app.stage.addChild(bar);
    }

    // A trail's anchor is its bright end, at the particle.
    this.addLayer(trailTexture(), PARTICLES, this.trails, 1);
    const glow = glowTexture();
    this.addLayer(glow, PARTICLES, this.particles, 0.5);
    this.addLayer(ringTexture(), RINGS, this.rings, 0.5);
    this.addLayer(glow, RINGS, this.cores, 0.5);

    this.app.ticker.add((ticker) => this.tick(ticker.deltaMS));
    this.ready = true;
    this.apply();
  }

  /** One additive layer of <paramref name="count"/> sprites of one texture, all hidden. */
  private addLayer(texture: Texture, count: number, into: Particle[], anchorY: number): void {
    const layer = new ParticleContainer({
      dynamicProperties: { position: true, vertex: true, color: true, rotation: false, uvs: false },
    });
    layer.blendMode = "add";
    for (let i = 0; i < count; i++) {
      const particle = new Particle({ texture, anchorX: 0.5, anchorY, alpha: 0 });
      into.push(particle);
      layer.addParticle(particle);
    }
    this.app.stage.addChild(layer);
  }

  /** Runs the animation while the screen is visible and not paused; stopped, it costs nothing. */
  setRunning(running: boolean): void {
    if (this.running === running) {
      return;
    }
    this.running = running;
    if (running) {
      // Whatever waited while stopped is old news.
      this.queue.clear();
    }
    this.apply();
  }

  /** Reduced motion: no animation, only where things are, redrawn with each frame. */
  setStatic(staticView: boolean): void {
    this.staticView = staticView;
    this.apply();
  }

  setFrame(frame: FlowFrame): void {
    if (!this.ready) {
      return;
    }
    const now = performance.now();
    frame.exchanges.slice(0, LANES).forEach((exchange, laneIndex) => {
      if (exchange.mid !== null) {
        const current = this.mid[laneIndex] ?? 0;
        this.targetMid[laneIndex] = exchange.mid;
        // A first price, or a jump the scale could not ease through: start there.
        if (current === 0 || Math.abs(exchange.mid - current) / exchange.mid > 0.003) {
          this.mid[laneIndex] = exchange.mid;
        }
      } else {
        this.targetMid[laneIndex] = 0;
        this.mid[laneIndex] = 0;
      }
      this.setBars(laneIndex, exchange.asks, exchange.bids);

      if (!this.running || this.staticView || exchange.mid === null) {
        return;
      }
      const offsets = spawnOffsets(
        exchange.events.map((e) => e.timeMs),
        FRAME_INTERVAL_MS,
      );
      exchange.events.forEach((e, i) => {
        this.queue.push(
          laneIndex,
          now + (offsets[i] ?? 0),
          e.kind === "Trade",
          e.side === "Sell",
          e.price,
          e.amount,
        );
      });
    });
    if (this.staticView) {
      this.drawStatic(frame);
    }
  }

  destroy(): void {
    this.destroyed = true;
    if (this.ready) {
      this.app.destroy(true, { children: true, texture: true });
    }
  }

  private apply(): void {
    if (!this.ready) {
      return;
    }
    if (this.running && !this.staticView) {
      this.app.ticker.start();
    } else {
      this.app.ticker.stop();
      this.app.render();
    }
  }

  private setBars(laneIndex: number, asks: [number, number][], bids: [number, number][]): void {
    let max = 0;
    for (const [, amount] of asks) {
      max = Math.max(max, amount);
    }
    for (const [, amount] of bids) {
      max = Math.max(max, amount);
    }
    const full = (this.app.screen.width / LANES) * BAR_LENGTH;
    for (let level = 0; level < LEVELS; level++) {
      const ask = asks[level];
      const bid = bids[level];
      const a = laneIndex * 2 * LEVELS + level;
      const b = a + LEVELS;
      this.barPrice[a] = ask?.[0] ?? 0;
      this.barTarget[a] = ask && max > 0 ? Math.sqrt(ask[1] / max) * full : 0;
      this.barPrice[b] = bid?.[0] ?? 0;
      this.barTarget[b] = bid && max > 0 ? Math.sqrt(bid[1] / max) * full : 0;
    }
  }

  private tick(dtMs: number): void {
    const now = performance.now();
    const { width, height } = this.app.screen;
    const laneWidth = width / LANES;

    for (let spawn = this.queue.takeDue(now); spawn; spawn = this.queue.takeDue(now)) {
      if (spawn.trade) {
        this.spawnRing(spawn.lane, spawn.price * spawn.amount, laneWidth, now);
      } else {
        this.spawnParticle(spawn.lane, spawn.sell, spawn.price, spawn.amount, laneWidth, now);
      }
    }

    for (let laneIndex = 0; laneIndex < LANES; laneIndex++) {
      this.mid[laneIndex] = ease(
        this.mid[laneIndex] ?? 0,
        this.targetMid[laneIndex] ?? 0,
        dtMs,
        300,
      );
    }
    this.drawBars(dtMs, laneWidth, height);
    this.drawParticles(now, height);
    this.drawRings(now, height);
    this.count(now);
  }

  private spawnParticle(
    laneIndex: number,
    sell: boolean,
    price: number,
    amount: number,
    laneWidth: number,
    now: number,
  ): void {
    const i = this.pool.acquire();
    if (i < 0) {
      return;
    }
    this.drawn++;
    this.lane[i] = laneIndex;
    this.sell[i] = sell ? 1 : 0;
    this.mode[i] = amount < 0 ? REMOVAL : TRAVEL;
    this.x[i] = laneIndex * laneWidth + laneWidth * (0.08 + this.random() * 0.78);
    this.price[i] = price;
    this.born[i] = now;
    this.travel[i] = TRAVEL_MIN + this.random() * TRAVEL_SPREAD;
    this.scale[i] = particleSize(Math.abs(amount) * price) / GLOW_CORE;
    const particle = this.particles[i] as Particle;
    particle.tint = sell ? SELL : BUY;
    (this.trails[i] as Particle).tint = particle.tint;
  }

  private spawnRing(laneIndex: number, notional: number, laneWidth: number, now: number): void {
    const i = this.ringState.add(laneIndex, notional, now);
    if (i < 0) {
      return;
    }
    const diameter = ringSize(this.ringState.notional[i] ?? 0);
    // Clear of the mid price label on the left and inside the lane on the right.
    const left = laneWidth * 0.34 + diameter / 4;
    const right = laneWidth - diameter / 2 - 6;
    const start = laneIndex * laneWidth;
    this.ringX[i] = this.ringState.merged
      ? start + Math.max(left, Math.min(right, (this.ringX[i] ?? 0) - start))
      : start + Math.max(left, Math.min(right, left + this.random() * (right - left)));
    this.ringScale[i] = diameter / RING_TEXTURE_SIZE;
  }

  /**
   * Bars at their price, best level first; the best levels are often a cent apart, so a level that would
   * overlap the one before moves away from the mid by a bar's spacing, which keeps them readable as a ladder.
   */
  private drawBars(dtMs: number, laneWidth: number, height: number): void {
    let previous = 0;
    for (let i = 0; i < this.bars.length; i++) {
      const laneIndex = Math.floor(i / (LEVELS * 2));
      const level = i % LEVELS;
      const ask = i % (LEVELS * 2) < LEVELS;
      const mid = this.mid[laneIndex] ?? 0;
      const target = mid > 0 && (this.barPrice[i] ?? 0) > 0 ? (this.barTarget[i] ?? 0) : 0;
      const width = ease(this.barWidth[i] ?? 0, target, dtMs, 150);
      this.barWidth[i] = width;
      const priceY = priceToY(this.barPrice[i] ?? 0, mid, height);
      const start = height / 2 + (ask ? -BAR_SPACING / 2 : BAR_SPACING / 2);
      const y = ask
        ? Math.min(priceY, level === 0 ? start : previous - BAR_SPACING)
        : Math.max(priceY, level === 0 ? start : previous + BAR_SPACING);
      previous = y;
      const bar = this.bars[i] as Sprite;
      bar.visible = width > 0.5;
      if (bar.visible) {
        bar.x = (laneIndex + 1) * laneWidth - width;
        bar.y = y - BAR_HEIGHT / 2;
        bar.width = width;
        bar.height = BAR_HEIGHT;
      }
    }
  }

  private drawParticles(now: number, height: number): void {
    for (let laneIndex = 0; laneIndex < LANES; laneIndex++) {
      this.exposure[laneIndex] = Math.min(
        1,
        Math.sqrt(FULL_EXPOSURE / Math.max(1, this.alive[laneIndex] ?? 0)),
      );
      this.alive[laneIndex] = 0;
    }
    for (let i = 0; i < PARTICLES; i++) {
      const mode = this.mode[i];
      if (mode === FREE || mode === STATIC) {
        continue;
      }
      const particle = this.particles[i] as Particle;
      const laneIndex = this.lane[i] ?? 0;
      const mid = this.mid[laneIndex] ?? 0;
      const age = now - (this.born[i] ?? 0);
      const scale = this.scale[i] ?? 0;
      const target = mid > 0 ? priceToY(this.price[i] ?? 0, mid, height) : height / 2;
      let alpha: number;
      let done: boolean;
      let y = target;
      let size = scale;
      let trail = 0;
      if (mode === REMOVAL) {
        const t = age / REMOVAL_FADE;
        done = t >= 1;
        alpha = 0.9 * (1 - t);
        size = scale * (1 - 0.4 * t);
      } else {
        const travel = this.travel[i] ?? TRAVEL_MIN;
        const start = this.sell[i] ? -12 : height + 12;
        if (age < travel) {
          y = start + (target - start) * easeOutCubic(age / travel);
          trail = y - (start + (target - start) * easeOutCubic(Math.max(0, age - TRAIL) / travel));
          alpha = Math.min(0.95, age / 200);
          done = false;
        } else {
          const fade = (age - travel - HOLD) / FADE;
          done = fade >= 1;
          alpha = fade <= 0 ? 0.95 : 0.95 * (1 - fade);
        }
      }
      // Ended by its time, not its alpha: a travelling particle is transparent on the frame it is born.
      if (done) {
        this.release(i);
        continue;
      }
      particle.x = this.x[i] ?? 0;
      particle.y = y;
      particle.scaleX = size;
      particle.scaleY = size;
      this.alive[laneIndex] = (this.alive[laneIndex] ?? 0) + 1;
      alpha *= this.exposure[laneIndex] ?? 1;
      particle.alpha = alpha;
      // Sells fall, so their trail is above them; buys rise, trailing below (the sprite flipped).
      const streak = this.trails[i] as Particle;
      const length = Math.min(TRAIL_MAX, Math.abs(trail));
      streak.alpha = length > 1.5 ? alpha * 0.7 : 0;
      if (length > 1.5) {
        streak.x = particle.x;
        streak.y = y;
        streak.scaleX = size;
        streak.scaleY = (trail > 0 ? length : -length) / TRAIL_TEXTURE_HEIGHT;
      }
    }
  }

  private drawRings(now: number, height: number): void {
    for (let i = 0; i < RINGS; i++) {
      const rings = this.ringState;
      if (!rings.isActive(i)) {
        continue;
      }
      const ring = this.rings[i] as Particle;
      const core = this.cores[i] as Particle;
      const age = now - (rings.born[i] ?? 0);
      const t = age / RING_LIFE;
      const evicted = rings.evicted[i] ?? 0;
      const left = evicted > 0 ? 1 - (now - evicted) / EVICT_FADE : 1;
      if (t >= 1 || left <= 0 || (this.mid[rings.lane[i] ?? 0] ?? 0) === 0) {
        ring.alpha = 0;
        core.alpha = 0;
        rings.release(i);
        continue;
      }
      const full = this.ringScale[i] ?? 0;
      ring.x = core.x = this.ringX[i] ?? 0;
      ring.y = core.y = height / 2;
      ring.scaleX = full * (0.2 + 0.8 * easeOutCubic(t));
      ring.scaleY = ring.scaleX;
      ring.alpha = 0.8 * (1 - t) * left;
      // The core flashes and is gone before the ring is half grown.
      const c = age / CORE_LIFE;
      core.alpha = c < 1 ? (1 - c * c) * left : 0;
      core.scaleX = ((full * RING_TEXTURE_SIZE * 0.3) / GLOW_CORE) * (1 - 0.4 * Math.min(1, c));
      core.scaleY = core.scaleX;
    }
  }

  private release(i: number): void {
    (this.particles[i] as Particle).alpha = 0;
    (this.trails[i] as Particle).alpha = 0;
    this.mode[i] = FREE;
    this.pool.release(i);
  }

  /** The latest frame's sample at its prices, without motion, for reduced motion. */
  private drawStatic(frame: FlowFrame): void {
    const { width, height } = this.app.screen;
    const laneWidth = width / LANES;
    for (let i = 0; i < PARTICLES; i++) {
      if (this.pool.isActive(i)) {
        this.release(i);
      }
    }
    frame.exchanges.slice(0, LANES).forEach((exchange, laneIndex) => {
      this.mid[laneIndex] = exchange.mid ?? 0;
      if (exchange.mid === null) {
        return;
      }
      for (const e of exchange.events) {
        if (e.kind === "Trade") {
          continue;
        }
        const i = this.pool.acquire();
        if (i < 0) {
          break;
        }
        this.mode[i] = STATIC;
        const particle = this.particles[i] as Particle;
        particle.tint = e.side === "Sell" ? SELL : BUY;
        particle.x = laneIndex * laneWidth + laneWidth * (0.08 + this.random() * 0.78);
        particle.y = priceToY(e.price, exchange.mid, height);
        particle.scaleX = particleSize(Math.abs(e.amount) * e.price) / GLOW_CORE;
        particle.scaleY = particle.scaleX;
        particle.alpha = 0.8;
      }
    });
    for (let i = 0; i < this.barWidth.length; i++) {
      this.barWidth[i] = this.barTarget[i] ?? 0;
    }
    this.drawBars(1_000_000, laneWidth, height);
    this.app.render();
  }

  private count(now: number): void {
    this.frames++;
    if (this.windowStart === 0) {
      this.windowStart = now;
    }
    const elapsed = now - this.windowStart;
    if (elapsed >= 1_000) {
      this.stats = {
        fps: (this.frames * 1_000) / elapsed,
        drawnPerSecond: (this.drawn * 1_000) / elapsed,
        skipped: this.pool.skipped,
      };
      this.frames = 0;
      this.drawn = 0;
      this.windowStart = now;
      this.onStats(this.stats);
    }
  }
}

/** The solid core's diameter in {@link glowTexture}; a particle's scale is its size over this. */
const GLOW_CORE = 16;

/** A round dot with a soft glow around it, white so it can be tinted: 64 px across, the core 16. */
function glowTexture(): Texture {
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = 64;
  const context = canvas.getContext("2d") as CanvasRenderingContext2D;
  const gradient = context.createRadialGradient(32, 32, 0, 32, 32, 32);
  gradient.addColorStop(0, "rgba(255,255,255,1)");
  gradient.addColorStop(0.22, "rgba(255,255,255,1)");
  gradient.addColorStop(0.26, "rgba(255,255,255,0.35)");
  gradient.addColorStop(0.45, "rgba(255,255,255,0.12)");
  gradient.addColorStop(0.7, "rgba(255,255,255,0.03)");
  gradient.addColorStop(1, "rgba(255,255,255,0)");
  context.fillStyle = gradient;
  context.fillRect(0, 0, 64, 64);
  return Texture.from(canvas);
}

const TRAIL_TEXTURE_HEIGHT = 64;

/** A streak 16 px wide, soft at the sides, clear at the top and bright at the bottom. */
function trailTexture(): Texture {
  const canvas = document.createElement("canvas");
  canvas.width = 16;
  canvas.height = TRAIL_TEXTURE_HEIGHT;
  const context = canvas.getContext("2d") as CanvasRenderingContext2D;
  const along = context.createLinearGradient(0, 0, 0, TRAIL_TEXTURE_HEIGHT);
  along.addColorStop(0, "rgba(255,255,255,0)");
  along.addColorStop(1, "rgba(255,255,255,0.8)");
  context.fillStyle = along;
  context.fillRect(0, 0, 16, TRAIL_TEXTURE_HEIGHT);
  const across = context.createLinearGradient(0, 0, 16, 0);
  across.addColorStop(0, "rgba(255,255,255,0)");
  across.addColorStop(0.5, "rgba(255,255,255,1)");
  across.addColorStop(1, "rgba(255,255,255,0)");
  context.globalCompositeOperation = "destination-in";
  context.fillStyle = across;
  context.fillRect(0, 0, 16, TRAIL_TEXTURE_HEIGHT);
  return Texture.from(canvas);
}

/** A bar that fades in from the left, white so it can be tinted: soft over most of its length, full at the right. */
function fadeTexture(): Texture {
  const canvas = document.createElement("canvas");
  canvas.width = 128;
  canvas.height = 4;
  const context = canvas.getContext("2d") as CanvasRenderingContext2D;
  const gradient = context.createLinearGradient(0, 0, 128, 0);
  gradient.addColorStop(0, "rgba(255,255,255,0)");
  gradient.addColorStop(0.3, "rgba(255,255,255,0.28)");
  gradient.addColorStop(0.7, "rgba(255,255,255,0.62)");
  gradient.addColorStop(1, "rgba(255,255,255,1)");
  context.fillStyle = gradient;
  context.fillRect(0, 0, 128, 4);
  return Texture.from(canvas);
}

/** A thin ring, 64 px across. */
function ringTexture(): Texture {
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = 64;
  const context = canvas.getContext("2d") as CanvasRenderingContext2D;
  context.strokeStyle = "rgba(232,235,240,1)";
  context.lineWidth = 2.5;
  context.beginPath();
  context.arc(32, 32, 29, 0, Math.PI * 2);
  context.stroke();
  return Texture.from(canvas);
}
