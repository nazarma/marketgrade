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

export interface EngineStatus {
  status: { timeUtc: string; exchanges: ExchangeStatus[] };
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

async function get<T>(path: string): Promise<{ port: number; body: T }> {
  try {
    const { port, token } = await getBackendInfo();
    const response = await fetch(`http://127.0.0.1:${port}${path}`, {
      headers: { Authorization: `Bearer ${token}` },
    });
    if (!response.ok) {
      throw new Error(`${path} failed with HTTP ${response.status}`);
    }
    return { port, body: (await response.json()) as T };
  } catch (error) {
    resetBackendInfo();
    throw error;
  }
}

export async function getBackendStatus(): Promise<BackendStatus> {
  const { port, body } = await get<Health>("/health");
  return { port, health: body };
}

export async function getEngineStatus(): Promise<EngineStatus> {
  return (await get<EngineStatus>("/engine/status")).body;
}
