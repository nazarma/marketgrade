import { describe, expect, it } from "vitest";
import type { ConnectionEvent, ExchangeFlowFrame, FlowFrame } from "@/api";
import {
  initialLiveState,
  KEPT_EVENTS,
  LATEST_TRADES,
  seeded,
  withEvent,
  withFlow,
  withoutFlow,
  withStatus,
} from "@/live/liveStore";

const status = {
  timeUtc: "2026-10-28T22:30:00Z",
  exchanges: [],
  recording: null,
  clockOffsetMs: 1.5,
};
const event = (n: number): ConnectionEvent => ({
  timeUtc: `2026-10-28T22:30:${String(n % 60).padStart(2, "0")}Z`,
  exchange: "Kraken",
  event: `Event ${n}`,
  detail: null,
});

function frame(exchange: string, trades: { timeMs: number; price: number }[]): FlowFrame {
  const exchangeFrame: ExchangeFlowFrame = {
    id: 1,
    exchange,
    pair: "BTC/USD",
    priceDecimals: 1,
    bookChanges: 100,
    trades: trades.length,
    lost: 0,
    mid: 81_000,
    bids: [],
    asks: [],
    events: [
      { timeMs: 1, kind: "BookChange", side: "Buy", price: 80_999, amount: 0.5 },
      ...trades.map((t) => ({ ...t, kind: "Trade" as const, side: "Sell" as const, amount: 0.01 })),
    ],
  };
  return { timeUtc: "2026-10-28T22:30:00Z", intervalSeconds: 0.1, exchanges: [exchangeFrame] };
}

describe("live store", () => {
  it("starts from the engine status and its recent events", () => {
    const state = seeded(initialLiveState, { status, events: [event(1), event(2)] });

    expect(state.status).toBe(status);
    expect(state.events.map((e) => e.event)).toEqual(["Event 1", "Event 2"]);
    expect(state.received.status).toBe(0);
  });

  it("replaces the status and counts each push", () => {
    const state = withStatus(withStatus(initialLiveState, status), status);

    expect(state.status).toBe(status);
    expect(state.received).toEqual({ status: 2, event: 0, flow: 0 });
  });

  it("keeps the newest events, oldest first", () => {
    let state = initialLiveState;
    for (let n = 1; n <= KEPT_EVENTS + 5; n++) {
      state = withEvent(state, event(n));
    }

    expect(state.events).toHaveLength(KEPT_EVENTS);
    expect(state.events[0]?.event).toBe("Event 6");
    expect(state.events.at(-1)?.event).toBe(`Event ${KEPT_EVENTS + 5}`);
    expect(state.received.event).toBe(KEPT_EVENTS + 5);
  });

  it("merges the trades of each frame into the latest trades, newest first", () => {
    let state = withFlow(
      initialLiveState,
      frame("Kraken", [
        { timeMs: 10, price: 81_000 },
        { timeMs: 30, price: 81_001 },
      ]),
    );
    state = withFlow(state, frame("Coinbase", [{ timeMs: 20, price: 81_002 }]));

    expect(state.trades.map((t) => [t.exchange, t.timeMs])).toEqual([
      ["Kraken", 30],
      ["Coinbase", 20],
      ["Kraken", 10],
    ]);
    expect(state.trades[0]).toMatchObject({ side: "Sell", priceDecimals: 1, amount: 0.01 });
    expect(state.flow?.exchanges[0]?.exchange).toBe("Coinbase");
    expect(state.received.flow).toBe(2);
  });

  it("keeps only the latest trades", () => {
    const many = Array.from({ length: 25 }, (_, i) => ({ timeMs: i, price: 81_000 + i }));

    const state = withFlow(initialLiveState, frame("Kraken", many));

    expect(state.trades).toHaveLength(LATEST_TRADES);
    expect(state.trades[0]?.timeMs).toBe(24);
  });

  it("forgets the flow when the flow view goes away", () => {
    const state = withoutFlow(
      withFlow(initialLiveState, frame("Kraken", [{ timeMs: 1, price: 1 }])),
    );

    expect(state.flow).toBeNull();
    expect(state.trades).toEqual([]);
    expect(state.received.flow).toBe(1);
  });
});
