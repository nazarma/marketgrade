import { describe, expect, it } from "vitest";
import type { RecordingSettings } from "./api";
import { fromForm, toForm } from "./recordingSettings";

const settings: RecordingSettings = {
  enabled: false,
  folder: "/data/recordings",
  keepDays: 14,
  maxDiskBytes: 50 * 1024 ** 3,
  writeBufferBytes: 256 * 1024,
  writeIntervalMs: 500,
  compress: true,
  compressionLevel: 9,
};

describe("recording settings form", () => {
  it("converts bytes to GB and KB and back", () => {
    const form = toForm(settings);

    expect(form).toMatchObject({ maxDiskGb: "50", writeBufferKb: "256", writeIntervalMs: "500" });
    expect(fromForm(form)).toEqual({ ok: true, values: settings });
  });

  it("accepts a fractional number of GB", () => {
    const result = fromForm({ ...toForm(settings), maxDiskGb: "1.5" });

    expect(result).toEqual({ ok: true, values: { ...settings, maxDiskBytes: 1.5 * 1024 ** 3 } });
  });

  it("reports fields that are not numbers", () => {
    const result = fromForm({
      ...toForm(settings),
      keepDays: "",
      writeBufferKb: "1.5",
      maxDiskGb: "x",
    });

    expect(result).toEqual({
      ok: false,
      errors: {
        keepDays: "Enter a whole number.",
        writeBufferBytes: "Enter a whole number.",
        maxDiskBytes: "Enter a number.",
      },
    });
  });
});
