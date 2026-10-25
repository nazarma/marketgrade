import type { ConnectionState, RecordingState } from "@/api";

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

export function recordingBadge(state: RecordingState): StateBadge {
  switch (state) {
    case "Recording":
      return { label: "Recording", className: live };
    case "Off":
      return { label: "Off", className: "bg-muted text-muted-foreground" };
    case "PausedLowDisk":
      return { label: "Paused, disk almost full", className: warning };
    case "Error":
      return { label: "Error", className: sell };
    default:
      return { label: String(state), className: sell };
  }
}

/** Binary units, as the settings use: 1 GB is 1024³ bytes. */
export function formatBytes(bytes: number | null): string {
  if (bytes === null) {
    return "–";
  }
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${unit === 0 || value >= 100 ? value.toFixed(0) : value.toFixed(1)} ${units[unit]}`;
}
