import type { FieldErrors, RecordingSettings } from "@/api";

const KB = 1024;
const GB = 1024 * 1024 * 1024;

/** The settings form: numbers as typed text, in the units the form shows. */
export interface RecordingForm {
  enabled: boolean;
  folder: string;
  keepDays: string;
  maxDiskGb: string;
  writeBufferKb: string;
  writeIntervalMs: string;
  compress: boolean;
  compressionLevel: string;
}

export function toForm(settings: RecordingSettings): RecordingForm {
  return {
    enabled: settings.enabled,
    folder: settings.folder,
    keepDays: String(settings.keepDays),
    maxDiskGb: String(Math.round((settings.maxDiskBytes / GB) * 100) / 100),
    writeBufferKb: String(Math.round(settings.writeBufferBytes / KB)),
    writeIntervalMs: String(settings.writeIntervalMs),
    compress: settings.compress,
    compressionLevel: String(settings.compressionLevel),
  };
}

/**
 * Converts the form to settings. Only checks that numbers are numbers; the backend checks the
 * ranges, so its messages appear next to the fields.
 */
export function fromForm(
  form: RecordingForm,
): { ok: true; values: RecordingSettings } | { ok: false; errors: FieldErrors } {
  const errors: FieldErrors = {};
  const number = (text: string, field: keyof RecordingSettings, decimals = false) => {
    const value = Number(text.trim());
    if (text.trim() === "" || !Number.isFinite(value) || (!decimals && !Number.isInteger(value))) {
      errors[field] = decimals ? "Enter a number." : "Enter a whole number.";
    }
    return value;
  };

  const values: RecordingSettings = {
    enabled: form.enabled,
    folder: form.folder.trim(),
    keepDays: number(form.keepDays, "keepDays"),
    maxDiskBytes: Math.round(number(form.maxDiskGb, "maxDiskBytes", true) * GB),
    writeBufferBytes: number(form.writeBufferKb, "writeBufferBytes") * KB,
    writeIntervalMs: number(form.writeIntervalMs, "writeIntervalMs"),
    compress: form.compress,
    compressionLevel: number(form.compressionLevel, "compressionLevel"),
  };
  return Object.keys(errors).length > 0 ? { ok: false, errors } : { ok: true, values };
}
