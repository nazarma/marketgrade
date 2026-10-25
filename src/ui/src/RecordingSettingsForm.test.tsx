// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { getRecordingSettings, saveRecordingSettings, type RecordingSettings } from "@/api";
import { FOLDER_CHANGED_NOTICE, RecordingSettingsForm } from "@/RecordingSettingsForm";

vi.mock("@/api", () => ({ getRecordingSettings: vi.fn(), saveRecordingSettings: vi.fn() }));
vi.mock("@tauri-apps/plugin-dialog", () => ({ open: vi.fn() }));

const defaults: RecordingSettings = {
  enabled: true,
  folder: "/data/recordings",
  keepDays: 7,
  maxDiskBytes: 20 * 1024 ** 3,
  writeBufferBytes: 1024 ** 2,
  writeIntervalMs: 1000,
  compress: true,
  compressionLevel: 3,
};

describe("RecordingSettingsForm", () => {
  beforeEach(() => {
    vi.mocked(getRecordingSettings).mockResolvedValue({ values: defaults, defaults });
  });

  afterEach(() => {
    cleanup();
    vi.resetAllMocks();
  });

  it("shows the settings in the form's units", async () => {
    render(<RecordingSettingsForm />);

    expect(await screen.findByLabelText("Folder")).toHaveProperty("value", "/data/recordings");
    expect(screen.getByLabelText("Maximum disk space")).toHaveProperty("value", "20");
    expect(screen.getByLabelText("Write buffer")).toHaveProperty("value", "1024");
    expect(screen.getByLabelText("Write interval")).toHaveProperty("value", "1000");
  });

  it("shows the backend's error next to each invalid field", async () => {
    vi.mocked(saveRecordingSettings).mockResolvedValue({
      ok: false,
      errors: {
        keepDays: "Must be between 1 and 3,650 days.",
        folder: "This folder is not writable.",
      },
    });
    render(<RecordingSettingsForm />);
    const keepFor = await screen.findByLabelText("Keep for");

    fireEvent.change(keepFor, { target: { value: "0" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText("Must be between 1 and 3,650 days.")).toBeDefined();
    expect(keepFor.getAttribute("aria-invalid")).toBe("true");
    const describedBy = keepFor.getAttribute("aria-describedby") ?? "";
    expect(document.getElementById(describedBy)?.textContent).toBe(
      "Must be between 1 and 3,650 days.",
    );
    expect(screen.getByLabelText("Folder").getAttribute("aria-invalid")).toBe("true");
    expect(screen.getByLabelText("Write interval").getAttribute("aria-invalid")).toBeNull();
    expect(saveRecordingSettings).toHaveBeenCalledWith({ ...defaults, keepDays: 0 });
  });

  it("does not send text that is not a number", async () => {
    render(<RecordingSettingsForm />);
    const interval = await screen.findByLabelText("Write interval");

    fireEvent.change(interval, { target: { value: "fast" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByText("Enter a whole number.")).toBeDefined();
    expect(saveRecordingSettings).not.toHaveBeenCalled();
  });

  it("explains what happens to old recordings after a folder change", async () => {
    vi.mocked(saveRecordingSettings).mockImplementation((values) =>
      Promise.resolve({ ok: true, values }),
    );
    render(<RecordingSettingsForm />);
    const folder = await screen.findByLabelText("Folder");

    fireEvent.change(folder, { target: { value: "/elsewhere" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect((await screen.findByRole("status")).textContent).toBe(FOLDER_CHANGED_NOTICE);
  });

  it("fills the form with the defaults on reset", async () => {
    vi.mocked(getRecordingSettings).mockResolvedValue({
      values: { ...defaults, keepDays: 30, compress: false },
      defaults,
    });
    render(<RecordingSettingsForm />);
    const keepFor = await screen.findByLabelText("Keep for");
    expect(keepFor).toHaveProperty("value", "30");

    fireEvent.click(screen.getByRole("button", { name: "Reset to defaults" }));

    expect(keepFor).toHaveProperty("value", "7");
    expect(screen.getByLabelText("Compress finished hours")).toHaveProperty("checked", true);
  });
});
