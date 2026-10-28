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
  spawnOffsets,
} from "@/screens/flow/flowMath";
import { ParticlePool, SpawnQueue } from "@/screens/flow/particlePool";

export const PARTICLES = 2_000;
const RINGS = 200;
const LEVELS = 12;
const LANES = 3;
const FRAME_INTERVAL_MS = 100;

const BUY = 0x3ddc97;
const SELL = 0xff6b78;
const BAR_ALPHA = 0.16;
const BAR_HEIGHT = 4;
/** Levels closer than this on the price scale are spread out from the mid, keeping their order. */
const BAR_SPACING = 6;

/** How long things take, in milliseconds. */
const TRAVEL_MIN = 1_100;
const TRAVEL_SPREAD = 700;
const HOLD = 150;
const FADE = 450;
const REMOVAL_FADE = 800;
const RING_LIFE = 1_400;

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
 * sell changes falling from the top and buy changes rising from the bottom to their price, removals fading
 * where they are, and trades as rings at the mid. It draws a sample of the events it is given, with a fixed
 * pool of sprites; the animation loop allocates nothing.
 */
export class FlowRenderer {
  private readonly app = new Application();
  private readonly pool = new ParticlePool(PARTICLES);
  private readonly ringPool = new ParticlePool(RINGS);
  private readonly queue = new SpawnQueue(4_096);
  private readonly random = lcg(7);
  private readonly particles: Particle[] = [];
  private readonly rings: Particle[] = [];
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
  private readonly ringLane = new Uint8Array(RINGS);
  private readonly ringX = new Float32Array(RINGS);
  private readonly ringBorn = new Float64Array(RINGS);

  // Per lane: the mid price shown (eased) and its target; depth bars' prices and widths.
  private readonly mid = new Float64Array(LANES);
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

    for (let i = 0; i < LANES * LEVELS * 2; i++) {
      const bar = new Sprite(Texture.WHITE);
      bar.alpha = BAR_ALPHA;
      bar.tint = i % (LEVELS * 2) < LEVELS ? SELL : BUY;
      bar.visible = false;
      this.bars.push(bar);
      this.app.stage.addChild(bar);
    }

    const dot = dotTexture();
    const layer = new ParticleContainer({
      dynamicProperties: { position: true, vertex: true, color: true, rotation: false, uvs: false },
    });
    for (let i = 0; i < PARTICLES; i++) {
      const particle = new Particle({ texture: dot, anchorX: 0.5, anchorY: 0.5, alpha: 0 });
      this.particles.push(particle);
      layer.addParticle(particle);
    }
    this.app.stage.addChild(layer);

    const ring = ringTexture();
    const ringLayer = new ParticleContainer({
      dynamicProperties: { position: true, vertex: true, color: true, rotation: false, uvs: false },
    });
    for (let i = 0; i < RINGS; i++) {
      const particle = new Particle({ texture: ring, anchorX: 0.5, anchorY: 0.5, alpha: 0 });
      this.rings.push(particle);
      ringLayer.addParticle(particle);
    }
    this.app.stage.addChild(ringLayer);

    this.app.ticker.add((ticker) => this.tick(ticker.deltaMS));
    this.ready = true;
    this.apply();
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
    const full = (this.app.screen.width / LANES) * 0.45;
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
        this.spawnRing(spawn.lane, laneWidth, now);
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
    this.scale[i] = particleSize(Math.abs(amount) * price) / 16;
    const particle = this.particles[i] as Particle;
    particle.tint = sell ? SELL : BUY;
  }

  private spawnRing(laneIndex: number, laneWidth: number, now: number): void {
    const i = this.ringPool.acquire();
    if (i < 0) {
      return;
    }
    this.ringLane[i] = laneIndex;
    this.ringX[i] = laneIndex * laneWidth + laneWidth * (0.25 + this.random() * 0.55);
    this.ringBorn[i] = now;
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
    for (let i = 0; i < PARTICLES; i++) {
      const mode = this.mode[i];
      if (mode === FREE || mode === STATIC) {
        continue;
      }
      const particle = this.particles[i] as Particle;
      const mid = this.mid[this.lane[i] ?? 0] ?? 0;
      const age = now - (this.born[i] ?? 0);
      const scale = this.scale[i] ?? 0;
      const target = mid > 0 ? priceToY(this.price[i] ?? 0, mid, height) : height / 2;
      let alpha: number;
      let y = target;
      let size = scale;
      if (mode === REMOVAL) {
        const t = age / REMOVAL_FADE;
        alpha = 0.9 * (1 - t);
        size = scale * (1 - 0.4 * t);
      } else {
        const travel = this.travel[i] ?? TRAVEL_MIN;
        const start = this.sell[i] ? -12 : height + 12;
        if (age < travel) {
          y = start + (target - start) * easeOutCubic(age / travel);
          alpha = Math.min(0.95, age / 200);
        } else {
          const fade = (age - travel - HOLD) / FADE;
          alpha = fade <= 0 ? 0.95 : 0.95 * (1 - fade);
        }
      }
      if (alpha <= 0) {
        this.release(i);
        continue;
      }
      particle.x = this.x[i] ?? 0;
      particle.y = y;
      particle.scaleX = size;
      particle.scaleY = size;
      particle.alpha = alpha;
    }
  }

  private drawRings(now: number, height: number): void {
    for (let i = 0; i < RINGS; i++) {
      if (!this.ringPool.isActive(i)) {
        continue;
      }
      const ring = this.rings[i] as Particle;
      const t = (now - (this.ringBorn[i] ?? 0)) / RING_LIFE;
      if (t >= 1 || (this.mid[this.ringLane[i] ?? 0] ?? 0) === 0) {
        ring.alpha = 0;
        this.ringPool.release(i);
        continue;
      }
      const eased = easeOutCubic(t);
      ring.x = this.ringX[i] ?? 0;
      ring.y = height / 2;
      ring.scaleX = 0.15 + eased * 0.7;
      ring.scaleY = ring.scaleX;
      ring.alpha = 0.75 * (1 - t);
    }
  }

  private release(i: number): void {
    (this.particles[i] as Particle).alpha = 0;
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
        particle.scaleX = particleSize(Math.abs(e.amount) * e.price) / 16;
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

/** A soft round dot: a solid core with a glow, white so it can be tinted; 32 px across, the core about 16. */
function dotTexture(): Texture {
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = 32;
  const context = canvas.getContext("2d") as CanvasRenderingContext2D;
  const gradient = context.createRadialGradient(16, 16, 0, 16, 16, 16);
  gradient.addColorStop(0, "rgba(255,255,255,1)");
  gradient.addColorStop(0.45, "rgba(255,255,255,1)");
  gradient.addColorStop(0.55, "rgba(255,255,255,0.45)");
  gradient.addColorStop(1, "rgba(255,255,255,0)");
  context.fillStyle = gradient;
  context.fillRect(0, 0, 32, 32);
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
