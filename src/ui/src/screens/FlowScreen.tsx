import { useEffect, useRef, useState, type CSSProperties, type Ref } from "react";
import type { ExchangeFlowFrame } from "@/api";
import { toggleFullScreen } from "@/app/fullScreen";
import { FullScreenIcon, PauseIcon, PlayIcon } from "@/app/icons";
import { ScreenHeader } from "@/app/ScreenHeader";
import { useDocumentVisible } from "@/app/useDocumentVisible";
import { useMediaQuery } from "@/app/useMediaQuery";
import { setFlowSubscribed } from "@/live/hub";
import { useLiveStore, type LatestTrade } from "@/live/liveStore";
import { formatAmount, formatCount, formatPrice, formatRate, formatTime } from "@/lib/format";
import { SCALE_FRACTION, sampleRatio } from "@/screens/flow/flowMath";
import { FlowRenderer, type FlowRendererStats } from "@/screens/flow/FlowRenderer";
import { useFlowRates, type FlowRates } from "@/screens/flow/useFlowRates";

/** Price scale ticks, as fractions of the scale's half height above and below the mid. */
const TICKS = [-2 / 3, -1 / 3, 1 / 3, 2 / 3];

/** The mid price label's overlay as it pulses up (green) or down (red); only its opacity is animated. */
const PULSE_UP = {
  borderColor: "#3DDC97",
  background: "rgba(61,220,151,.2)",
  boxShadow: "0 0 14px rgba(61,220,151,.45)",
};
const PULSE_DOWN = {
  borderColor: "#FF6B78",
  background: "rgba(255,107,120,.2)",
  boxShadow: "0 0 14px rgba(255,107,120,.45)",
};

export function FlowScreen() {
  const visible = useDocumentVisible();
  const reducedMotion = useMediaQuery("(prefers-reduced-motion: reduce)");
  const [paused, setPaused] = useState(false);
  const [stats, setStats] = useState<FlowRendererStats>({ fps: 0, drawnPerSecond: 0, skipped: 0 });
  const rates = useFlowRates();

  // Flow pushes only while this screen is open and the window can be seen.
  useEffect(() => {
    setFlowSubscribed(visible);
    return () => setFlowSubscribed(false);
  }, [visible]);

  return (
    <>
      <ScreenHeader
        title="Live order flow"
        description="BTC/USD. Sell orders fall from the top, buy orders rise from the bottom, trades flash at the current price."
      >
        <button
          type="button"
          className="btn-secondary"
          aria-label={paused ? "Resume animation" : "Pause animation"}
          aria-pressed={paused}
          onClick={() => setPaused((p) => !p)}
          disabled={reducedMotion}
        >
          {paused ? <PlayIcon /> : <PauseIcon />}
        </button>
        <button type="button" className="btn-secondary" onClick={() => void toggleFullScreen()}>
          <FullScreenIcon />
          Full screen
        </button>
      </ScreenHeader>

      <FlowLanes
        running={visible && !paused}
        reducedMotion={reducedMotion}
        rates={rates}
        onStats={setStats}
      />

      <StatTiles rates={rates} stats={stats} paused={paused || reducedMotion} />
      <LatestTrades />
    </>
  );
}

function FlowLanes({
  running,
  reducedMotion,
  rates,
  onStats,
}: {
  running: boolean;
  reducedMotion: boolean;
  rates: FlowRates;
  onStats: (stats: FlowRendererStats) => void;
}) {
  const canvas = useRef<HTMLDivElement>(null);
  const renderer = useRef<FlowRenderer | null>(null);
  const flow = useLiveStore((state) => state.flow);
  const status = useLiveStore((state) => state.status);

  useEffect(() => {
    const element = canvas.current;
    if (!element) {
      return;
    }
    const instance = new FlowRenderer(element, onStats);
    renderer.current = instance;
    void instance.init();
    // Frames go straight to the renderer, not through React.
    const unsubscribe = useLiveStore.subscribe((state, previous) => {
      if (state.flow && state.flow !== previous.flow) {
        instance.setFrame(state.flow);
      }
    });
    return () => {
      unsubscribe();
      instance.destroy();
      renderer.current = null;
    };
  }, [onStats]);

  useEffect(() => renderer.current?.setRunning(running), [running]);
  useEffect(() => renderer.current?.setStatic(reducedMotion), [reducedMotion]);

  // Before the first frame, the lanes are named from the status.
  const lanes: { id: number; name: string; frame?: ExchangeFlowFrame }[] =
    flow?.exchanges.map((e) => ({ id: e.id, name: e.exchange, frame: e })) ??
    (status?.exchanges ?? []).map((e) => ({ id: e.id, name: e.name }));

  return (
    <section
      className="panel relative overflow-hidden"
      aria-label="Order flow visualisation"
      style={{ background: "var(--mg-canvas)", height: "clamp(360px, calc(100vh - 340px), 560px)" }}
    >
      <div className="absolute inset-0 grid grid-cols-3">
        {lanes.map((lane, i) => (
          <div
            key={lane.id}
            className="relative"
            style={{
              borderRight: i < lanes.length - 1 ? "1px solid #161B24" : undefined,
              background:
                "radial-gradient(ellipse at 50% 50%, rgba(92,200,255,.06), transparent 60%)",
            }}
          >
            <div
              className="absolute inset-x-0 top-1/2 h-px"
              style={{
                background:
                  "linear-gradient(90deg, transparent, #3A4456 20%, #3A4456 80%, transparent)",
              }}
            />
            {lane.frame?.mid != null &&
              TICKS.map((tick) => (
                <div
                  key={tick}
                  className="absolute inset-x-0 h-px"
                  style={{
                    top: `${50 + tick * 50}%`,
                    background:
                      "linear-gradient(90deg, transparent, rgba(139,149,165,.11) 12%, rgba(139,149,165,.11) 88%, transparent)",
                  }}
                />
              ))}
          </div>
        ))}
      </div>
      <div ref={canvas} className="absolute inset-0" />
      <div className="pointer-events-none absolute inset-0 grid grid-cols-3">
        {lanes.map((lane) => (
          <LaneLabels
            key={lane.id}
            name={lane.name}
            frame={lane.frame}
            rate={rates.byExchange.get(lane.id)}
            pulse={running && !reducedMotion}
          />
        ))}
      </div>
    </section>
  );
}

function LaneLabels({
  name,
  frame,
  rate,
  pulse,
}: {
  name: string;
  frame?: ExchangeFlowFrame;
  rate?: number;
  pulse: boolean;
}) {
  const mid = frame?.mid ?? null;
  const decimals = frame?.priceDecimals ?? 2;
  const pulseUp = useRef<HTMLSpanElement>(null);
  const pulseDown = useRef<HTMLSpanElement>(null);
  const previousMid = useRef<number | null>(null);

  // The mid price flashes green when it rises and red when it falls: an overlay fades out, which the
  // compositor animates without repainting.
  useEffect(() => {
    const before = previousMid.current;
    previousMid.current = mid;
    if (!pulse || before === null || mid === null || mid === before) {
      return;
    }
    (mid > before ? pulseUp : pulseDown).current?.animate([{ opacity: 1 }, { opacity: 0 }], {
      duration: 700,
      easing: "ease-out",
    });
  }, [mid, pulse]);
  return (
    <div className="relative">
      <div className="absolute top-3.5 left-4 flex flex-col gap-0.5">
        <span className="text-sm font-semibold">{name}</span>
        <span className="lbl mono">{rate === undefined ? "–" : formatCount(rate)} updates/s</span>
      </div>
      <div className="lbl absolute top-4 right-3.5">Sells ↓</div>
      <div className="lbl absolute right-3.5 bottom-3.5">Buys ↑</div>
      {mid === null ? (
        <div className="lbl absolute inset-x-0 top-1/2 mt-3 text-center">
          {frame ? "Waiting for a checked book" : "Connecting..."}
        </div>
      ) : (
        <>
          <div
            className="mono absolute top-1/2 left-4 -translate-y-1/2 rounded-md border border-strong px-2 py-1 text-[13px]"
            style={{ background: "var(--mg-panel)" }}
          >
            <PulseOverlay ref={pulseUp} style={PULSE_UP} />
            <PulseOverlay ref={pulseDown} style={PULSE_DOWN} />
            <span className="relative">{formatPrice(mid, decimals)}</span>
          </div>
          {TICKS.map((tick) => (
            <div
              key={tick}
              className="mono absolute left-3 -translate-y-1/2 px-1 text-[10px] text-muted-foreground"
              style={{ top: `${50 + tick * 50}%`, background: "var(--mg-canvas)", opacity: 0.8 }}
            >
              {formatPrice(mid * (1 - tick * SCALE_FRACTION), decimals)}
            </div>
          ))}
        </>
      )}
    </div>
  );
}

function PulseOverlay({ ref, style }: { ref: Ref<HTMLSpanElement>; style: CSSProperties }) {
  return (
    <span
      ref={ref}
      aria-hidden="true"
      className="absolute -inset-px rounded-md border opacity-0"
      style={{ ...style, willChange: "opacity" }}
    />
  );
}

function StatTiles({
  rates,
  stats,
  paused,
}: {
  rates: FlowRates;
  stats: FlowRendererStats;
  paused: boolean;
}) {
  const ratio = sampleRatio(rates.bookChanges, stats.drawnPerSecond);
  return (
    <div className="grid grid-cols-[repeat(auto-fit,minmax(200px,1fr))] gap-4">
      <Tile
        label="Updates received"
        value={formatRate(rates.bookChanges)}
        note="Book changes, all exchanges"
      />
      <Tile
        label="Updates drawn (sampled)"
        value={paused ? "–" : formatRate(stats.drawnPerSecond)}
        note={
          paused
            ? "Animation paused"
            : `${ratio ? `${ratio}, largest first` : "Every change drawn"}${stats.skipped > 0 ? `; ${formatCount(stats.skipped)} skipped, all sprites busy` : ""}`
        }
      />
      <Tile label="Trades" value={formatRate(rates.trades)} />
      <Tile label="Frame rate" value={paused ? "Paused" : `${Math.round(stats.fps)} fps`} />
    </div>
  );
}

function Tile({ label, value, note }: { label: string; value: string; note?: string }) {
  return (
    <div className="panel px-[18px] py-4">
      <div className="lbl">{label}</div>
      <div className="stat-value">{value}</div>
      {note && <div className="lbl mt-1">{note}</div>}
    </div>
  );
}

function LatestTrades() {
  const trades = useLiveStore((state) => state.trades);
  return (
    <section className="panel flex flex-col gap-2 px-2 pt-[18px] pb-2">
      <h2 className="h2 px-3">Latest trades</h2>
      {trades.length === 0 ? (
        <p className="lbl px-3 pb-2">Waiting for trades...</p>
      ) : (
        <div className="overflow-x-auto">
          <table className="tbl tbl-compact">
            <thead>
              <tr>
                <th scope="col">Time</th>
                <th scope="col">Exchange</th>
                <th scope="col">Side</th>
                <th scope="col" className="r">
                  Price
                </th>
                <th scope="col" className="r">
                  Amount BTC
                </th>
              </tr>
            </thead>
            <tbody>
              {trades.map((trade: LatestTrade, i) => (
                <tr key={`${trade.exchange}-${trade.timeMs}-${i}`}>
                  <td className="mono">{formatTime(trade.timeMs, true)}</td>
                  <td>{trade.exchange}</td>
                  <td className={trade.side === "Buy" ? "ok" : "bad"}>{trade.side}</td>
                  <td className="mono r">{formatPrice(trade.price, trade.priceDecimals)}</td>
                  <td className="mono r">{formatAmount(trade.amount)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p className="lbl mx-3 mt-1 mb-2">Side is the side of the order that took liquidity.</p>
    </section>
  );
}
