import { invoke, isTauri } from "@tauri-apps/api/core";

export interface BackendInfo {
  port: number;
  token: string;
}

export interface Health {
  status: string;
  version: string;
  startedAtUtc: string;
}

export interface BackendStatus {
  port: number;
  health: Health;
}

export type ConnectionState = "Connecting" | "Live" | "Reconnecting" | "Blocked" | "Stopped";

export type BookState = "NoPairRules" | "Syncing" | "Valid" | "Invalid";

/** A period in which the book was invalid, from the failed check to the repaired book. */
export interface BookGap {
  startUtc: string;
  /** Null while the book is still being repaired. */
  endUtc: string | null;
  cause: string;
  startSeq: number;
  endSeq: number | null;
  durationSeconds: number | null;
}

export interface ExchangeStatus {
  id: number;
  name: string;
  pair: string;
  state: ConnectionState;
  /** Book update messages only; trade, heartbeat, control and error messages are counted separately. */
  messagesPerSecond: number;
  bytesPerSecond: number;
  msSinceLastMessage: number | null;
  connectionId: number;
  reconnectCount: number;
  lastError: string | null;
  dataMessages: number;
  heartbeatMessages: number;
  controlMessages: number;
  errorMessages: number;
  bytes: number;
  bookState: BookState;
  /** Prices with the pair's decimals; null while the book is not valid. */
  bestBid: string | null;
  bestAsk: string | null;
  spread: string | null;
  bidLevels: number;
  askLevels: number;
  maxBidLevels: number;
  maxAskLevels: number;
  /** Exchange timestamp to arrival over the last minute, clock-corrected when the offset is known. */
  delayP50Ms: number | null;
  delayP99Ms: number | null;
  /** Time to parse, apply and check one message over the last minute. */
  processingP50Ns: number | null;
  processingP99Ns: number | null;
  /** Gaps that started today (UTC). */
  gapsToday: number;
  lastGap: BookGap | null;
  /** Time from receiving a message to the book worker starting on it, last minute. */
  queueWaitP50Ns: number | null;
  queueWaitP99Ns: number | null;
  /** Messages since the last snapshot that arrived within 100 ms after it; not in the delay figures. */
  catchUpMessages: number;
  catchUpMaxDelayMs: number | null;
  /** Message timestamp minus its oldest change time, last minute; only Coinbase reports it. */
  exchangeHoldP50Ms: number | null;
  exchangeHoldP99Ms: number | null;
  tradeMessages: number;
  /** Trades parsed; a trade message can hold several. */
  trades: number;
  tradesPerSecond: number;
  /** Trade messages that could not be read; they never affect the book. */
  tradeFailures: number;
  /** Delays measured over the last minute, and of those the ones below zero (counted as zero). */
  delayCount: number;
  delayNegative: number;
  /** More than 10 % of the delays were negative, so the delay figures are a lower bound. */
  delayLowerBound: boolean;
  /** How far below zero the negative delays went, 99th percentile; null without any. */
  delayAheadMs: number | null;
}

export interface ConnectionEvent {
  timeUtc: string;
  exchange: string;
  event: string;
  detail: string | null;
}

export type RecordingState = "Recording" | "Off" | "PausedLowDisk" | "Error";

export interface RecordingStatus {
  state: RecordingState;
  folder: string;
  /** Size on disk of today's (UTC) recordings. */
  todayBytes: number;
  totalBytes: number;
  /** Null if the drive could not be checked. */
  freeBytes: number | null;
  droppedMessages: number;
  lastError: string | null;
}

export interface EngineStatus {
  status: {
    timeUtc: string;
    exchanges: ExchangeStatus[];
    recording: RecordingStatus | null;
    /** How far this computer's clock is ahead of NTP time; null if unknown. */
    clockOffsetMs: number | null;
  };
  /** Oldest first. */
  events: ConnectionEvent[];
}

let backendInfo: Promise<BackendInfo> | undefined;

/** Forgets the port and token, so the next request asks the desktop shell again (it restarts a dead backend). */
export function resetBackendInfo() {
  backendInfo = undefined;
}

/** The desktop shell's backend; in development outside the desktop app, the one named by Vite's environment. */
export function getBackendInfo(): Promise<BackendInfo> {
  backendInfo ??=
    import.meta.env.DEV && !isTauri()
      ? import("./devBackend").then((dev) => dev.devBackendInfo())
      : invoke<BackendInfo>("get_backend_info");
  return backendInfo;
}

/** Recording settings as the backend stores them: sizes in bytes, times in milliseconds. */
export interface RecordingSettings {
  enabled: boolean;
  folder: string;
  keepDays: number;
  maxDiskBytes: number;
  writeBufferBytes: number;
  writeIntervalMs: number;
  compress: boolean;
  compressionLevel: number;
}

export interface RecordingSettingsResponse {
  values: RecordingSettings;
  defaults: RecordingSettings;
}

/** One message per invalid field, keyed by the field's API name. */
export type FieldErrors = Partial<Record<keyof RecordingSettings, string>>;

export type SaveResult =
  { ok: true; values: RecordingSettings } | { ok: false; errors: FieldErrors };

/** Sends a request with the token; any failure except an answered 400 makes the next request ask for the backend again. */
async function request(
  path: string,
  init?: RequestInit,
): Promise<{ port: number; response: Response }> {
  try {
    const { port, token } = await getBackendInfo();
    const response = await fetch(`http://127.0.0.1:${port}${path}`, {
      ...init,
      headers: { ...init?.headers, Authorization: `Bearer ${token}` },
    });
    if (!response.ok && response.status !== 400) {
      throw new Error(`${path} failed with HTTP ${response.status}`);
    }
    return { port, response };
  } catch (error) {
    resetBackendInfo();
    throw error;
  }
}

async function get<T>(path: string): Promise<{ port: number; body: T }> {
  const { port, response } = await request(path);
  if (!response.ok) {
    throw new Error(`${path} failed with HTTP ${response.status}`);
  }
  return { port, body: (await response.json()) as T };
}

export async function getBackendStatus(): Promise<BackendStatus> {
  const { port, body } = await get<Health>("/health");
  return { port, health: body };
}

export async function getEngineStatus(): Promise<EngineStatus> {
  return (await get<EngineStatus>("/engine/status")).body;
}

export async function getRecordingSettings(): Promise<RecordingSettingsResponse> {
  return (await get<RecordingSettingsResponse>("/settings/recording")).body;
}

/** Saves and applies the settings, or returns the backend's message for each invalid field. */
export async function saveRecordingSettings(values: RecordingSettings): Promise<SaveResult> {
  const { response } = await request("/settings/recording", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(values),
  });
  if (response.status === 400) {
    const problem = (await response.json()) as { errors?: Record<string, string[]> };
    const errors: FieldErrors = {};
    for (const [field, messages] of Object.entries(problem.errors ?? {})) {
      errors[field as keyof RecordingSettings] = messages[0];
    }
    return { ok: false, errors };
  }
  return { ok: true, values: (await response.json()) as RecordingSettings };
}

export interface HistorySettings {
  keepDays: number;
}

export async function getHistorySettings(): Promise<{
  values: HistorySettings;
  defaults: HistorySettings;
}> {
  return (await get<{ values: HistorySettings; defaults: HistorySettings }>("/settings/history"))
    .body;
}

/** Saves and applies the settings, or returns the backend's message for each invalid field. */
export async function saveHistorySettings(
  values: HistorySettings,
): Promise<{ ok: true; values: HistorySettings } | { ok: false; errors: { keepDays?: string } }> {
  const { response } = await request("/settings/history", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(values),
  });
  if (response.status === 400) {
    const problem = (await response.json()) as { errors?: Record<string, string[]> };
    return { ok: false, errors: { keepDays: problem.errors?.keepDays?.[0] } };
  }
  return { ok: true, values: (await response.json()) as HistorySettings };
}

export interface DelayPoint {
  minuteUtc: string;
  p50Ms: number | null;
  p99Ms: number | null;
  /** More than 10 % of the minute's delays were negative, so these are a lower bound. */
  lowerBound: boolean;
  aheadMs: number | null;
}

export interface DelayHistory {
  fromUtc: string;
  toUtc: string;
  exchanges: { id: number; exchange: string; points: DelayPoint[] }[];
}

/** Per-minute state as parallel arrays, oldest first; minutes the app was not running are missing. */
export interface MinuteColumns {
  /** Start of each minute, Unix milliseconds. */
  minuteMs: number[];
  running: number[];
  valid: number[];
  invalid: number[];
  reconnects: number[];
  gaps: number[];
}

export interface ExchangeHealth {
  id: number;
  exchange: string;
  /** Valid seconds / running seconds, 0 to 1; null if the app never ran in the range. */
  uptime: number | null;
  runningSeconds: number;
  validSeconds: number;
  /** Approximate (from coarse histograms, within about 12 %). */
  delayP50Ms: number | null;
  delayP99Ms: number | null;
  delayMaxMs: number | null;
  /** More than 10 % of the delays in the range were negative, so the delay figures are a lower bound. */
  delayLowerBound: boolean;
  delayAheadMs: number | null;
  bookUpdates: number;
  trades: number;
  minutes: MinuteColumns;
  reconnects: { timeUtc: string; reason: string }[];
}

export interface HealthHistory {
  fromUtc: string;
  toUtc: string;
  exchanges: ExchangeHealth[];
}

export interface GapEntry {
  exchangeId: number;
  exchange: string;
  startUtc: string;
  endUtc: string;
  durationSeconds: number;
  kind: "Checksum" | "Sequence" | "Parse" | "ConnectionLost";
  cause: string;
  repairAction: string;
}

export async function getDelayHistory(minutes: number): Promise<DelayHistory> {
  return (await get<DelayHistory>(`/history/delay?minutes=${minutes}`)).body;
}

export async function getHealthHistory(hours: number): Promise<HealthHistory> {
  return (await get<HealthHistory>(`/history/health?hours=${hours}`)).body;
}

export async function getGaps(hours: number): Promise<GapEntry[]> {
  return (await get<GapEntry[]>(`/history/gaps?hours=${hours}`)).body;
}

export type FlowKind = "BookChange" | "Trade";
export type FlowSide = "Buy" | "Sell";

/** A sampled book change or trade; for a book change the amount is the signed change at its level. */
export interface FlowItem {
  /** Unix milliseconds: arrival for a book change, the exchange's time for a trade. */
  timeMs: number;
  kind: FlowKind;
  side: FlowSide;
  price: number;
  amount: number;
}

export interface ExchangeFlowFrame {
  id: number;
  exchange: string;
  pair: string;
  priceDecimals: number;
  /** All book changes and trades received in the interval. */
  bookChanges: number;
  trades: number;
  /** Events overwritten before they could be sampled (still counted above). */
  lost: number;
  /** Null while the book is not valid. */
  mid: number | null;
  /** Best first, [price, amount], at most 12. */
  bids: [number, number][];
  asks: [number, number][];
  /** Oldest first: every trade up to 50 and the 40 largest book changes by notional value. */
  events: FlowItem[];
}

export interface FlowFrame {
  timeUtc: string;
  intervalSeconds: number;
  exchanges: ExchangeFlowFrame[];
}
