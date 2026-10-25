import { open } from "@tauri-apps/plugin-dialog";
import { useEffect, useId, useState, type ReactNode } from "react";
import {
  getRecordingSettings,
  saveRecordingSettings,
  type FieldErrors,
  type RecordingSettings,
  type RecordingSettingsResponse,
} from "@/api";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { fromForm, toForm, type RecordingForm } from "@/recordingSettings";

export const FOLDER_CHANGED_NOTICE =
  "New recordings go to the new folder. Existing recordings stay in the old folder and are no longer cleaned up automatically.";

type SaveState =
  { kind: "idle" } | { kind: "saving" } | { kind: "saved" } | { kind: "failed"; message: string };

export function RecordingSettingsForm() {
  const [settings, setSettings] = useState<RecordingSettingsResponse | null>(null);
  const [form, setForm] = useState<RecordingForm | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [errors, setErrors] = useState<FieldErrors>({});
  const [save, setSave] = useState<SaveState>({ kind: "idle" });
  const [folderChanged, setFolderChanged] = useState(false);

  useEffect(() => {
    let current = true;
    getRecordingSettings().then(
      (loaded) => {
        if (current) {
          setSettings(loaded);
          setForm(toForm(loaded.values));
        }
      },
      (error: unknown) =>
        current && setLoadError(error instanceof Error ? error.message : String(error)),
    );
    return () => {
      current = false;
    };
  }, []);

  if (loadError) {
    return <p className="font-mono text-sm text-muted-foreground">{loadError}</p>;
  }
  if (!settings || !form) {
    return <p className="text-muted-foreground">Loading settings...</p>;
  }

  const update = (changes: Partial<RecordingForm>) => {
    setForm({ ...form, ...changes });
    setSave({ kind: "idle" });
  };

  const submit = async () => {
    const parsed = fromForm(form);
    if (!parsed.ok) {
      setErrors(parsed.errors);
      return;
    }
    setSave({ kind: "saving" });
    try {
      const result = await saveRecordingSettings(parsed.values);
      if (!result.ok) {
        setErrors(result.errors);
        setSave({ kind: "idle" });
        return;
      }
      setErrors({});
      setFolderChanged(result.values.folder !== settings.values.folder);
      setSettings({ ...settings, values: result.values });
      setForm(toForm(result.values));
      setSave({ kind: "saved" });
    } catch (error) {
      setSave({ kind: "failed", message: error instanceof Error ? error.message : String(error) });
    }
  };

  const chooseFolder = async () => {
    const folder = await open({ directory: true, multiple: false, defaultPath: form.folder });
    if (typeof folder === "string") {
      update({ folder });
    }
  };

  const numberField = (field: keyof RecordingSettings, formField: keyof RecordingForm) => ({
    error: errors[field],
    value: String(form[formField]),
    onChange: (value: string) => update({ [formField]: value }),
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Recording</CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-5">
        <Toggle
          label="Record every message to disk"
          checked={form.enabled}
          onChange={(enabled) => update({ enabled })}
        />

        <Field label="Folder" error={errors.folder}>
          {(id, describedBy) => (
            <div className="flex gap-2">
              <Input
                id={id}
                value={form.folder}
                onChange={(e) => update({ folder: e.target.value })}
                aria-invalid={errors.folder ? true : undefined}
                aria-describedby={describedBy}
                className="font-mono"
              />
              <Button variant="outline" onClick={() => void chooseFolder()}>
                Choose folder
              </Button>
            </div>
          )}
        </Field>

        <div className="grid gap-5 sm:grid-cols-2">
          <NumberField
            label="Keep for"
            unit="days"
            hint="1 to 3,650"
            {...numberField("keepDays", "keepDays")}
          />
          <NumberField
            label="Maximum disk space"
            unit="GB"
            hint="At least 1 GB, at most the free space"
            {...numberField("maxDiskBytes", "maxDiskGb")}
          />
          <NumberField
            label="Write buffer"
            unit="KB"
            hint="64 KB to 64 MB (65,536 KB)"
            {...numberField("writeBufferBytes", "writeBufferKb")}
          />
          <NumberField
            label="Write interval"
            unit="ms"
            hint="100 ms to 60,000 ms"
            {...numberField("writeIntervalMs", "writeIntervalMs")}
          />
        </div>

        <Toggle
          label="Compress finished hours"
          checked={form.compress}
          onChange={(compress) => update({ compress })}
        />
        <NumberField
          label="Compression level"
          unit=""
          hint="1 (fastest) to 19 (smallest)"
          {...numberField("compressionLevel", "compressionLevel")}
        />

        <div className="flex flex-wrap items-center gap-3">
          <Button onClick={() => void submit()} disabled={save.kind === "saving"}>
            Save
          </Button>
          <Button
            variant="outline"
            onClick={() => {
              update(toForm(settings.defaults));
              setErrors({});
            }}
          >
            Reset to defaults
          </Button>
          {save.kind === "saved" && <span className="text-sm text-buy">Saved and applied.</span>}
          {save.kind === "failed" && <span className="text-sm text-sell">{save.message}</span>}
        </div>

        {folderChanged && save.kind === "saved" && (
          <p
            role="status"
            className="rounded-lg border border-warning/40 bg-warning/10 p-3 text-sm"
          >
            {FOLDER_CHANGED_NOTICE}
          </p>
        )}
      </CardContent>
    </Card>
  );
}

function Field({
  label,
  hint,
  error,
  children,
}: {
  label: string;
  hint?: string;
  error?: string;
  children: (id: string, describedBy: string) => ReactNode;
}) {
  const id = useId();
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-sm font-medium">
        {label}
      </label>
      {children(id, `${id}-message`)}
      <p
        id={`${id}-message`}
        className={error ? "text-sm text-sell" : "text-xs text-muted-foreground"}
      >
        {error ?? hint}
      </p>
    </div>
  );
}

function NumberField({
  label,
  unit,
  hint,
  error,
  value,
  onChange,
}: {
  label: string;
  unit: string;
  hint: string;
  error?: string;
  value: string;
  onChange: (value: string) => void;
}) {
  return (
    <Field label={label} hint={hint} error={error}>
      {(id, describedBy) => (
        <div className="flex items-center gap-2">
          <Input
            id={id}
            inputMode="decimal"
            value={value}
            onChange={(e) => onChange(e.target.value)}
            aria-invalid={error ? true : undefined}
            aria-describedby={describedBy}
            className="w-32 font-mono tabular-nums"
          />
          {unit && <span className="text-sm text-muted-foreground">{unit}</span>}
        </div>
      )}
    </Field>
  );
}

function Toggle({
  label,
  checked,
  onChange,
}: {
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
}) {
  return (
    <label className="flex items-center gap-2 text-sm font-medium">
      <input
        type="checkbox"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
        className="size-4 accent-primary"
      />
      {label}
    </label>
  );
}
