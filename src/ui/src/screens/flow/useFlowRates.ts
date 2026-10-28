import { useEffect, useState } from "react";
import type { FlowFrame } from "@/api";
import { useLiveStore } from "@/live/liveStore";

const WINDOW = 10;

export interface FlowRates {
  /** Book changes per second received over the last second, all exchanges. */
  bookChanges: number;
  trades: number;
  /** Per exchange id. */
  byExchange: Map<number, number>;
}

const EMPTY: FlowRates = { bookChanges: 0, trades: 0, byExchange: new Map() };

/** Rates from the last ten flow frames (about a second), updated twice a second. */
export function useFlowRates(): FlowRates {
  const [rates, setRates] = useState<FlowRates>(EMPTY);
  useEffect(() => {
    const frames: FlowFrame[] = [];
    const unsubscribe = useLiveStore.subscribe((state, previous) => {
      if (state.flow && state.flow !== previous.flow) {
        frames.push(state.flow);
        if (frames.length > WINDOW) {
          frames.shift();
        }
      }
    });
    const timer = setInterval(() => {
      const seconds = frames.reduce((sum, f) => sum + f.intervalSeconds, 0);
      if (seconds <= 0) {
        setRates(EMPTY);
        return;
      }
      const byExchange = new Map<number, number>();
      let bookChanges = 0;
      let trades = 0;
      for (const frame of frames) {
        for (const exchange of frame.exchanges) {
          byExchange.set(
            exchange.id,
            (byExchange.get(exchange.id) ?? 0) + exchange.bookChanges / seconds,
          );
          bookChanges += exchange.bookChanges;
          trades += exchange.trades;
        }
      }
      setRates({ bookChanges: bookChanges / seconds, trades: trades / seconds, byExchange });
    }, 500);
    return () => {
      unsubscribe();
      clearInterval(timer);
    };
  }, []);
  return rates;
}
