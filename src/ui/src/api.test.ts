import { invoke } from "@tauri-apps/api/core";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { getBackendStatus } from "./api";

vi.mock("@tauri-apps/api/core", () => ({ invoke: vi.fn() }));

const health = { status: "ok", version: "0.1.0", startedAtUtc: "2026-01-01T00:00:00Z" };

describe("getBackendStatus", () => {
  const fetchMock = vi.fn<typeof fetch>();

  beforeEach(() => {
    vi.mocked(invoke).mockResolvedValue({ port: 51234, token: "secret" });
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.resetAllMocks();
  });

  it("calls /health on the backend port with the bearer token", async () => {
    fetchMock.mockResolvedValue(Response.json(health));

    const result = await getBackendStatus();

    expect(invoke).toHaveBeenCalledWith("get_backend_info");
    expect(fetchMock).toHaveBeenCalledWith("http://127.0.0.1:51234/health", {
      headers: { Authorization: "Bearer secret" },
    });
    expect(result).toEqual({ port: 51234, health });
  });

  it("throws when the backend rejects the request", async () => {
    fetchMock.mockResolvedValue(new Response(null, { status: 401 }));

    await expect(getBackendStatus()).rejects.toThrow("HTTP 401");
  });
});
