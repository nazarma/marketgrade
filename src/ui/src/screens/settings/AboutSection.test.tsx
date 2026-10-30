// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { isTauri } from "@tauri-apps/api/core";
import { resolveResource } from "@tauri-apps/api/path";
import { openPath, openUrl } from "@tauri-apps/plugin-opener";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AboutSection, NOTICES_RESOURCE, REPOSITORY_URL } from "@/screens/settings/AboutSection";

vi.mock("@tauri-apps/api/core", () => ({ isTauri: vi.fn(() => false) }));
vi.mock("@tauri-apps/api/path", () => ({ resolveResource: vi.fn() }));
vi.mock("@tauri-apps/plugin-opener", () => ({ openPath: vi.fn(), openUrl: vi.fn() }));

describe("AboutSection", () => {
  afterEach(() => {
    cleanup();
    vi.resetAllMocks();
  });

  it("shows the version, the licence and the disclaimer", () => {
    render(<AboutSection />);

    // Vite sets the version from the root package.json (vite.config.ts).
    expect(__APP_VERSION__).toMatch(/^\d+\.\d+\.\d+$/);
    expect(screen.getByText(__APP_VERSION__)).toBeDefined();
    expect(screen.getByText("MIT")).toBeDefined();
    expect(screen.getByText(/not affiliated with Kraken, Coinbase or Binance/)).toBeDefined();
  });

  it("leaves links to the browser outside the app", () => {
    render(<AboutSection />);
    const link = screen.getByRole("link", { name: "github.com/nazarma/marketgrade" });

    expect(link.getAttribute("href")).toBe(REPOSITORY_URL);
    expect(fireEvent.click(link)).toBe(true);
    expect(openUrl).not.toHaveBeenCalled();
  });

  it("opens the repository in the system browser inside the app", () => {
    vi.mocked(isTauri).mockReturnValue(true);
    vi.mocked(openUrl).mockResolvedValue();
    render(<AboutSection />);

    const followed = fireEvent.click(
      screen.getByRole("link", { name: "github.com/nazarma/marketgrade" }),
    );

    expect(followed).toBe(false);
    expect(openUrl).toHaveBeenCalledWith(REPOSITORY_URL);
  });

  it("opens the bundled notices inside the app", async () => {
    vi.mocked(isTauri).mockReturnValue(true);
    vi.mocked(resolveResource).mockResolvedValue("/app/resources/THIRD-PARTY-NOTICES.txt");
    vi.mocked(openPath).mockResolvedValue();
    render(<AboutSection />);

    fireEvent.click(screen.getByRole("link", { name: "Third-party notices" }));

    await vi.waitFor(() =>
      expect(openPath).toHaveBeenCalledWith("/app/resources/THIRD-PARTY-NOTICES.txt"),
    );
    expect(resolveResource).toHaveBeenCalledWith(NOTICES_RESOURCE);
  });

  it("shows why a link could not be opened", async () => {
    vi.mocked(isTauri).mockReturnValue(true);
    vi.mocked(openUrl).mockRejectedValue(new Error("Not allowed to open url"));
    render(<AboutSection />);

    fireEvent.click(screen.getByRole("link", { name: "github.com/nazarma/marketgrade" }));

    expect(await screen.findByText("Not allowed to open url")).toBeDefined();
  });
});
