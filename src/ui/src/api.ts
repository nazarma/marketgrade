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

export async function getBackendStatus(): Promise<BackendStatus> {
  const { port, token } = await invoke<BackendInfo>("get_backend_info");
  const response = await fetch(`http://127.0.0.1:${port}/health`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!response.ok) {
    throw new Error(`Health check failed with HTTP ${response.status}`);
  }
  return { port, health: (await response.json()) as Health };
}
