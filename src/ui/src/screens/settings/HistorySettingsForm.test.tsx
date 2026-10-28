// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { getHistorySettings, saveHistorySettings } from "@/api";
import { HistorySettingsForm } from "@/screens/settings/HistorySettingsForm";

vi.mock("@/api", () => ({ getHistorySettings: vi.fn(), saveHistorySettings: vi.fn() }));

describe("HistorySettingsForm", () => {
  beforeEach(() => {
    vi.mocked(getHistorySettings).mockResolvedValue({
      values: { keepDays: 30 },
      defaults: { keepDays: 90 },
    });
  });

  afterEach(() => {
    cleanup();
    vi.resetAllMocks();
  });

  it("saves the number of days", async () => {
    vi.mocked(saveHistorySettings).mockImplementation((values) =>
      Promise.resolve({ ok: true, values }),
    );
    render(<HistorySettingsForm />);
    const keep = await screen.findByLabelText("Keep history for");
    expect(keep).toHaveProperty("value", "30");

    fireEvent.change(keep, { target: { value: "365" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText("Saved and applied.")).toBeDefined();
    expect(saveHistorySettings).toHaveBeenCalledWith({ keepDays: 365 });
  });

  it("shows the backend's message for a value out of range", async () => {
    vi.mocked(saveHistorySettings).mockResolvedValue({
      ok: false,
      errors: { keepDays: "Must be between 7 and 3,650 days." },
    });
    render(<HistorySettingsForm />);
    const keep = await screen.findByLabelText("Keep history for");

    fireEvent.change(keep, { target: { value: "3" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText("Must be between 7 and 3,650 days.")).toBeDefined();
    expect(keep.getAttribute("aria-invalid")).toBe("true");
  });

  it("does not send text that is not a whole number, and resets to the default", async () => {
    render(<HistorySettingsForm />);
    const keep = await screen.findByLabelText("Keep history for");

    fireEvent.change(keep, { target: { value: "a while" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(await screen.findByText("Enter a whole number.")).toBeDefined();
    expect(saveHistorySettings).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Reset to defaults" }));
    expect(keep).toHaveProperty("value", "90");
  });
});
