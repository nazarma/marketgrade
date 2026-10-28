import type { BookState, ConnectionState, RecordingState } from "@/api";

export type Tone = "ok" | "warn" | "bad" | "muted";

export interface Badge {
  label: string;
  tone: Tone;
}

export function connectionBadge(state: ConnectionState): Badge {
  switch (state) {
    case "Live":
      return { label: "Connected", tone: "ok" };
    case "Connecting":
      return { label: "Connecting", tone: "warn" };
    case "Reconnecting":
      return { label: "Reconnecting", tone: "warn" };
    case "Blocked":
      return { label: "Blocked in your region", tone: "bad" };
    case "Stopped":
      return { label: "Stopped", tone: "muted" };
    default:
      return { label: String(state), tone: "bad" };
  }
}

export function bookBadge(state: BookState): Badge {
  switch (state) {
    case "Valid":
      return { label: "Book checked", tone: "ok" };
    case "Syncing":
      return { label: "Waiting for snapshot", tone: "warn" };
    case "Invalid":
      return { label: "Book invalid, repairing", tone: "bad" };
    case "NoPairRules":
      return { label: "No book", tone: "muted" };
    default:
      return { label: String(state), tone: "bad" };
  }
}

export function recordingBadge(state: RecordingState): Badge {
  switch (state) {
    case "Recording":
      return { label: "Recording", tone: "ok" };
    case "Off":
      return { label: "Recording off", tone: "muted" };
    case "PausedLowDisk":
      return { label: "Recording paused, disk almost full", tone: "warn" };
    case "Error":
      return { label: "Recording error", tone: "bad" };
    default:
      return { label: String(state), tone: "bad" };
  }
}
