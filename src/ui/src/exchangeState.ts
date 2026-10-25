import type { ConnectionState } from "@/api";

export interface StateBadge {
  label: string;
  className: string;
}

const live = "bg-buy/15 text-buy";
const warning = "bg-warning/15 text-warning";
const sell = "bg-sell/15 text-sell";

/** Every badge carries text, so colour is never the only signal. */
export function stateBadge(state: ConnectionState): StateBadge {
  switch (state) {
    case "Live":
      return { label: "Live", className: live };
    case "Connecting":
      return { label: "Connecting", className: warning };
    case "Reconnecting":
      return { label: "Reconnecting", className: warning };
    case "Blocked":
      return { label: "Blocked in your region", className: warning };
    case "Stopped":
      return { label: "Stopped", className: sell };
    default:
      return { label: String(state), className: sell };
  }
}

export function formatSinceLastMessage(ms: number | null): string {
  return ms === null ? "–" : `${(ms / 1000).toFixed(1)} s`;
}
