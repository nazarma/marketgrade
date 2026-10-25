import { invoke } from "@tauri-apps/api/core";

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

export interface ExchangeStatus {
  id: number;
  name: string;
  pair: string;
  state: ConnectionState;
  /** Data messages only; heartbeats, control and error messages are counted separately. */
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
  status: { timeUtc: string; exchanges: ExchangeStatus[]; recording: RecordingStatus | null };
  /** Oldest first. */
  events: ConnectionEvent[];
}

let backendInfo: Promise<BackendInfo> | undefined;

/** Forgets the port and token, so the next request asks the desktop shell again (it restarts a dead backend). */
export function resetBackendInfo() {
  backendInfo = undefined;
}

function getBackendInfo(): Promise<BackendInfo> {
  backendInfo ??= invoke<BackendInfo>("get_backend_info");
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
