import type { BookGap, BookState, ConnectionState, RecordingState } from "@/api";

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

export function bookBadge(state: BookState): StateBadge {
  switch (state) {
    case "Valid":
      return { label: "Valid", className: live };
    case "Syncing":
      return { label: "Syncing", className: warning };
    case "Invalid":
      return { label: "Invalid", className: sell };
    case "NoPairRules":
      return { label: "No pair rules", className: "bg-muted text-muted-foreground" };
    default:
      return { label: String(state), className: sell };
  }
}

/** Median and 99th percentile, for example "12.3 / 45.6 ms". */
export function formatDelay(p50Ms: number | null, p99Ms: number | null): string {
  return p50Ms === null || p99Ms === null ? "–" : `${p50Ms.toFixed(1)} / ${p99Ms.toFixed(1)} ms`;
}

/** Median and 99th percentile in whole nanoseconds, for example "7,264 / 43,264 ns". */
export function formatProcessing(p50Ns: number | null, p99Ns: number | null): string {
  const ns = (value: number) => Math.round(value).toLocaleString("en-US");
  return p50Ns === null || p99Ns === null ? "–" : `${ns(p50Ns)} / ${ns(p99Ns)} ns`;
}

export function formatGap(gap: BookGap | null): string | undefined {
  if (gap === null) {
    return undefined;
  }
  const duration =
    gap.durationSeconds === null ? "still open" : `${gap.durationSeconds.toFixed(1)} s`;
  return `Last gap: ${gap.cause} (${duration})`;
}

export const CLOCK_WARNING_MS = 50;

export interface ClockOffset {
  label: string;
  /** Set when delays cannot be trusted as shown. */
  warning: string | null;
}

export function clockOffset(offsetMs: number | null): ClockOffset {
  if (offsetMs === null) {
    return {
      label: "Clock offset unknown",
      warning: "No time server answered, so delays are not corrected for this computer's clock.",
    };
  }
  const label = `Clock offset ${offsetMs > 0 ? "+" : ""}${offsetMs.toFixed(1)} ms`;
  return Math.abs(offsetMs) > CLOCK_WARNING_MS
    ? {
        label,
        warning: `This computer's clock is more than ${CLOCK_WARNING_MS} ms off; delays are corrected for it.`,
      }
    : { label, warning: null };
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
