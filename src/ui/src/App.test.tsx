// @vitest-environment jsdom
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { getBackendInfo, getBackendStatus, getEngineStatus, resetBackendInfo } from "@/api";
import App from "@/App";
import { startLive } from "@/live/hub";

vi.mock("@/api", () => ({
  getBackendStatus: vi.fn(),
  getBackendInfo: vi.fn(),
  getEngineStatus: vi.fn(),
  resetBackendInfo: vi.fn(),
  getRecordingSettings: vi.fn(() => new Promise(() => undefined)),
  getDelayHistory: vi.fn(() => new Promise(() => undefined)),
  getHealthHistory: vi.fn(() => new Promise(() => undefined)),
  getGaps: vi.fn(() => new Promise(() => undefined)),
}));
vi.mock("@/live/hub", () => ({ startLive: vi.fn(), stopLive: vi.fn(() => Promise.resolve()) }));

const renderApp = () =>
  render(
    <QueryClientProvider client={new QueryClient()}>
      <MemoryRouter initialEntries={["/overview"]}>
        <App />
      </MemoryRouter>
    </QueryClientProvider>,
  );

describe("App", () => {
  beforeEach(() => {
    vi.stubGlobal("matchMedia", () => ({
      matches: false,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    }));
    vi.mocked(getBackendInfo).mockResolvedValue({ port: 51234, token: "secret" });
    vi.mocked(getEngineStatus).mockResolvedValue({
      status: {
        timeUtc: "2026-10-28T22:30:00Z",
        exchanges: [],
        recording: null,
        clockOffsetMs: null,
      },
      events: [],
    });
    vi.mocked(startLive).mockResolvedValue();
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    vi.resetAllMocks();
  });

  it("shows the error full screen when the backend is not reachable, and retries", async () => {
    vi.mocked(getBackendStatus).mockRejectedValueOnce(
      new Error("Backend exited before it was ready"),
    );

    renderApp();

    expect(await screen.findByRole("alert")).toHaveProperty(
      "textContent",
      expect.stringContaining("Backend exited before it was ready"),
    );
    expect(screen.queryByRole("navigation")).toBeNull();

    vi.mocked(getBackendStatus).mockResolvedValue({
      port: 51234,
      health: { status: "ok", version: "0.1.0", startedAtUtc: "2026-10-28T22:00:00Z" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));

    expect(await screen.findByRole("navigation", { name: "Main" })).toBeDefined();
    expect(resetBackendInfo).toHaveBeenCalled();
    expect(startLive).toHaveBeenCalledWith({ port: 51234, token: "secret" }, expect.any(Function));
  });

  it("shows the error when the live connection is lost for good", async () => {
    vi.mocked(getBackendStatus).mockResolvedValue({
      port: 51234,
      health: { status: "ok", version: "0.1.0", startedAtUtc: "2026-10-28T22:00:00Z" },
    });
    let lose: ((error?: Error) => void) | undefined;
    vi.mocked(startLive).mockImplementation((_, onLost) => {
      lose = onLost;
      return Promise.resolve();
    });

    renderApp();
    expect(await screen.findByRole("heading", { name: "Overview" })).toBeDefined();
    lose?.();

    expect(await screen.findByText("The connection to the backend was lost.")).toBeDefined();
  });
});
