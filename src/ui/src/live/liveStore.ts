import { create } from "zustand";
import type { ConnectionEvent, EngineStatus, FlowFrame } from "@/api";

export type HubState = "connecting" | "live" | "reconnecting";

export type HubMessage = "status" | "event" | "flow";

/** One trade of the latest trades table. */
export interface LatestTrade {
  exchange: string;
  priceDecimals: number;
  timeMs: number;
  side: "Buy" | "Sell";
  price: number;
  amount: number;
}

export const KEPT_EVENTS = 200;
export const LATEST_TRADES = 10;

export interface LiveState {
  hub: HubState;
  status: EngineStatus["status"] | null;
  /** Oldest first, at most {@link KEPT_EVENTS}. */
  events: ConnectionEvent[];
  /** The latest flow frame while the Live flow screen subscribes; null otherwise. */
  flow: FlowFrame | null;
  /** Newest first, across exchanges. */
  trades: LatestTrade[];
  /** Hub messages received per type since the app started, to check what the backend pushes. */
  received: Record<HubMessage, number>;
}

export const initialLiveState: LiveState = {
  hub: "connecting",
  status: null,
  events: [],
  flow: null,
  trades: [],
  received: { status: 0, event: 0, flow: 0 },
};

export const useLiveStore = create<LiveState>()(() => initialLiveState);

const counted = (state: LiveState, message: HubMessage): LiveState["received"] => ({
  ...state.received,
  [message]: state.received[message] + 1,
});

/** The state from <c>GET /engine/status</c>, before the first pushes. */
export function seeded(state: LiveState, snapshot: EngineStatus): LiveState {
  return { ...state, status: snapshot.status, events: snapshot.events.slice(-KEPT_EVENTS) };
}

export function withStatus(state: LiveState, status: EngineStatus["status"]): LiveState {
  return { ...state, status, received: counted(state, "status") };
}

export function withEvent(state: LiveState, event: ConnectionEvent): LiveState {
  const events = [...state.events, event];
  return {
    ...state,
    events: events.length > KEPT_EVENTS ? events.slice(-KEPT_EVENTS) : events,
    received: counted(state, "event"),
  };
}

/** The frame, and its trades merged into the latest trades, newest first. */
export function withFlow(state: LiveState, frame: FlowFrame): LiveState {
  const fresh: LatestTrade[] = [];
  for (const exchange of frame.exchanges) {
    for (const item of exchange.events) {
      if (item.kind === "Trade") {
        fresh.push({
          exchange: exchange.exchange,
          priceDecimals: exchange.priceDecimals,
          timeMs: item.timeMs,
          side: item.side,
          price: item.price,
          amount: item.amount,
        });
      }
    }
  }
  const trades =
    fresh.length === 0
      ? state.trades
      : [...fresh, ...state.trades].sort((a, b) => b.timeMs - a.timeMs).slice(0, LATEST_TRADES);
  return { ...state, flow: frame, trades, received: counted(state, "flow") };
}

/** The flow view is gone: forget its frames and trades, so a new visit starts fresh. */
export function withoutFlow(state: LiveState): LiveState {
  return { ...state, flow: null, trades: [] };
}
