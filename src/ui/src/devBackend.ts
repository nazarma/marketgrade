import type { BackendInfo } from "@/api";

/**
 * Development only: the backend's port and token from Vite's environment, so the UI can be opened in a normal
 * browser against a backend started by hand (see the README). Production builds never include this module.
 */
export function devBackendInfo(): BackendInfo {
  const port = Number(import.meta.env.VITE_BACKEND_PORT);
  const token = import.meta.env.VITE_BACKEND_TOKEN as string | undefined;
  if (!Number.isInteger(port) || port <= 0 || !token) {
    throw new Error(
      "Not running inside the desktop app: set VITE_BACKEND_PORT and VITE_BACKEND_TOKEN to use a backend started by hand.",
    );
  }
  return { port, token };
}
