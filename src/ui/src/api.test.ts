import { invoke } from "@tauri-apps/api/core";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  getBackendStatus,
  getEngineStatus,
  getRecordingSettings,
  resetBackendInfo,
  saveRecordingSettings,
  type RecordingSettings,
} from "./api";

vi.mock("@tauri-apps/api/core", () => ({ invoke: vi.fn(), isTauri: () => true }));

const health = { status: "ok", version: "0.1.0", startedAtUtc: "2026-01-01T00:00:00Z" };
const engineStatus = {
  status: { timeUtc: "2026-01-01T00:00:01Z", exchanges: [], recording: null },
  events: [
    { timeUtc: "2026-01-01T00:00:00Z", exchange: "Kraken", event: "Connected", detail: null },
  ],
};

describe("api", () => {
  const fetchMock = vi.fn<typeof fetch>();

  beforeEach(() => {
    resetBackendInfo();
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

  it("asks the desktop shell for the backend only once while requests succeed", async () => {
    fetchMock.mockImplementation(() => Promise.resolve(Response.json(engineStatus)));

    await getEngineStatus();
    const result = await getEngineStatus();

    expect(invoke).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenLastCalledWith("http://127.0.0.1:51234/engine/status", {
      headers: { Authorization: "Bearer secret" },
    });
    expect(result).toEqual(engineStatus);
  });

  it("asks the desktop shell again after a failed request", async () => {
    fetchMock.mockRejectedValueOnce(new TypeError("Failed to fetch"));
    await expect(getEngineStatus()).rejects.toThrow("Failed to fetch");

    fetchMock.mockResolvedValue(Response.json(engineStatus));
    await getEngineStatus();

    expect(invoke).toHaveBeenCalledTimes(2);
  });

  const recording: RecordingSettings = {
    enabled: true,
    folder: "/data/recordings",
    keepDays: 7,
    maxDiskBytes: 20 * 1024 ** 3,
    writeBufferBytes: 1024 ** 2,
    writeIntervalMs: 1000,
    compress: true,
    compressionLevel: 3,
  };

  it("gets the recording settings with their defaults", async () => {
    fetchMock.mockResolvedValue(Response.json({ values: recording, defaults: recording }));

    const result = await getRecordingSettings();

    expect(fetchMock).toHaveBeenCalledWith("http://127.0.0.1:51234/settings/recording", {
      headers: { Authorization: "Bearer secret" },
    });
    expect(result).toEqual({ values: recording, defaults: recording });
  });

  it("puts the recording settings as JSON and returns the saved values", async () => {
    fetchMock.mockResolvedValue(Response.json(recording));

    const result = await saveRecordingSettings(recording);

    expect(fetchMock).toHaveBeenCalledWith("http://127.0.0.1:51234/settings/recording", {
      method: "PUT",
      headers: { "Content-Type": "application/json", Authorization: "Bearer secret" },
      body: JSON.stringify(recording),
    });
    expect(result).toEqual({ ok: true, values: recording });
  });

  it("returns the first error of each field when the backend rejects the settings", async () => {
    fetchMock.mockResolvedValue(
      Response.json(
        {
          title: "One or more validation errors occurred.",
          errors: { keepDays: ["Too short.", "Other."] },
        },
        { status: 400 },
      ),
    );

    const result = await saveRecordingSettings(recording);

    expect(result).toEqual({ ok: false, errors: { keepDays: "Too short." } });
    // A validation error is an answer, so the backend is not looked up again.
    await getRecordingSettings().catch(() => undefined);
    expect(invoke).toHaveBeenCalledTimes(1);
  });
});
