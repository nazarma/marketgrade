import type { DelayHistory, ExchangeStatus, GapEntry } from "@/api";
import { parseTime } from "@/lib/format";

const MINUTE_MS = 60_000;

export interface DelayChartData {
  /** Unix seconds: every minute of the range, then now. */
  x: number[];
  /** Per exchange, in the order of {@link exchanges}: median delay in ms, null where there is no data. */
  y: (number | null)[][];
  exchanges: { id: number; name: string }[];
}

export interface GapMarker {
  timeSec: number;
  label: string;
}

/**
 * One point per minute of the history (null for minutes without a row: the app was not running), and a
 * last point for now from the live status, so the chart reaches the present.
 */
export function delayChartData(
  history: DelayHistory | undefined,
  live: ExchangeStatus[],
  nowMs: number,
  minutes = 60,
): DelayChartData {
  const firstMinute = Math.floor((nowMs - minutes * MINUTE_MS) / MINUTE_MS) * MINUTE_MS;
  const lastMinute = Math.floor(nowMs / MINUTE_MS) * MINUTE_MS;
  const minuteTimes: number[] = [];
  for (let t = firstMinute; t < lastMinute; t += MINUTE_MS) {
    minuteTimes.push(t);
  }
  const exchanges =
    live.length > 0
      ? live.map((e) => ({ id: e.id, name: e.name }))
      : (history?.exchanges ?? []).map((e) => ({ id: e.id, name: e.exchange }));

  const y = exchanges.map(({ id }) => {
    const byMinute = new Map<number, number | null>();
    for (const point of history?.exchanges.find((e) => e.id === id)?.points ?? []) {
      byMinute.set(parseTime(point.minuteUtc), point.p50Ms);
    }
    const now = live.find((e) => e.id === id)?.delayP50Ms ?? null;
    return [...minuteTimes.map((t) => byMinute.get(t) ?? null), now];
  });
  return { x: [...minuteTimes, nowMs].map((t) => t / 1000), y, exchanges };
}

/** A marker per gap that started in the range, from the gap log and the live status (a gap still open). */
export function gapMarkers(
  gaps: GapEntry[] | undefined,
  live: ExchangeStatus[],
  fromMs: number,
): GapMarker[] {
  const markers = new Map<string, GapMarker>();
  const add = (exchange: string, startUtc: string) => {
    const start = parseTime(startUtc);
    if (start >= fromMs) {
      // The gap log and the status write the same time with different precision.
      markers.set(`${exchange} ${Math.round(start / 1000)}`, {
        timeSec: start / 1000,
        label: `${exchange} gap`,
      });
    }
  };
  for (const gap of gaps ?? []) {
    add(gap.exchange, gap.startUtc);
  }
  for (const exchange of live) {
    if (exchange.lastGap) {
      add(exchange.name, exchange.lastGap.startUtc);
    }
  }
  return [...markers.values()].sort((a, b) => a.timeSec - b.timeSec);
}

/** A round upper limit with some room above the highest value. */
export function niceMax(max: number | null): number {
  if (max === null || !Number.isFinite(max) || max <= 0) {
    return 100;
  }
  const step = 10 ** Math.floor(Math.log10(max));
  return Math.ceil((max * 1.15) / step) * step;
}
