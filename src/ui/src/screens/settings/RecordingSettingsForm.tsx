import { open } from "@tauri-apps/plugin-dialog";
import { useEffect, useState } from "react";
import {
  getRecordingSettings,
  saveRecordingSettings,
  type FieldErrors,
  type RecordingSettings,
  type RecordingSettingsResponse,
} from "@/api";
import { fromForm, toForm, type RecordingForm } from "@/recordingSettings";
import {
  Field,
  FormActions,
  NumberField,
  SettingsPanel,
  Toggle,
  type SaveState,
} from "@/screens/settings/fields";

export const FOLDER_CHANGED_NOTICE =
  "New recordings go to the new folder. Existing recordings stay in the old folder and are no longer cleaned up automatically.";

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
    return <p className="mono bad m-0 text-sm">{loadError}</p>;
  }
  if (!settings || !form) {
    return <p className="lbl m-0">Loading settings...</p>;
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
    <SettingsPanel
      title="Recording"
      description="Every message from every exchange, exactly as received, in compressed hourly files."
    >
      <Toggle
        label="Record every message to disk"
        checked={form.enabled}
        onChange={(enabled) => update({ enabled })}
      />

      <Field label="Folder" error={errors.folder}>
        {(id, describedBy) => (
          <div className="flex gap-2">
            <input
              id={id}
              value={form.folder}
              onChange={(e) => update({ folder: e.target.value })}
              aria-invalid={errors.folder ? true : undefined}
              aria-describedby={describedBy}
              className="field-input mono min-w-0 flex-1"
            />
            <button
              type="button"
              className="btn-secondary h-10"
              onClick={() => void chooseFolder()}
            >
              Choose folder
            </button>
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

      <FormActions
        save={save}
        onSave={() => void submit()}
        onReset={() => {
          update(toForm(settings.defaults));
          setErrors({});
        }}
      />

      {folderChanged && save.kind === "saved" && (
        <p
          role="status"
          className="m-0 rounded-lg border border-warning/40 bg-warning/10 p-3 text-sm"
        >
          {FOLDER_CHANGED_NOTICE}
        </p>
      )}
    </SettingsPanel>
  );
}
