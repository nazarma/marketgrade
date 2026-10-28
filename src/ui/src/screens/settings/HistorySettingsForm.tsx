import { useEffect, useState } from "react";
import { getHistorySettings, saveHistorySettings, type HistorySettings } from "@/api";
import { FormActions, NumberField, SettingsPanel, type SaveState } from "@/screens/settings/fields";

/** How long chart history is kept; the backend checks the range and deletes older history at once. */
export function HistorySettingsForm() {
  const [settings, setSettings] = useState<{
    values: HistorySettings;
    defaults: HistorySettings;
  } | null>(null);
  const [keepDays, setKeepDays] = useState("");
  const [error, setError] = useState<string | undefined>();
  const [loadError, setLoadError] = useState<string | null>(null);
  const [save, setSave] = useState<SaveState>({ kind: "idle" });

  useEffect(() => {
    let current = true;
    getHistorySettings().then(
      (loaded) => {
        if (current) {
          setSettings(loaded);
          setKeepDays(String(loaded.values.keepDays));
        }
      },
      (e: unknown) => current && setLoadError(e instanceof Error ? e.message : String(e)),
    );
    return () => {
      current = false;
    };
  }, []);

  if (loadError) {
    return <p className="mono bad m-0 text-sm">{loadError}</p>;
  }
  if (!settings) {
    return <p className="lbl m-0">Loading settings...</p>;
  }

  const submit = async () => {
    const value = Number(keepDays.trim());
    if (keepDays.trim() === "" || !Number.isInteger(value)) {
      setError("Enter a whole number.");
      return;
    }
    setSave({ kind: "saving" });
    try {
      const result = await saveHistorySettings({ keepDays: value });
      if (!result.ok) {
        setError(result.errors.keepDays);
        setSave({ kind: "idle" });
        return;
      }
      setError(undefined);
      setSettings({ ...settings, values: result.values });
      setKeepDays(String(result.values.keepDays));
      setSave({ kind: "saved" });
    } catch (e) {
      setSave({ kind: "failed", message: e instanceof Error ? e.message : String(e) });
    }
  };

  return (
    <SettingsPanel
      title="History"
      description="Per-minute delay, uptime and volume, gaps and reconnects, for the charts on Overview and Data health."
    >
      <NumberField
        label="Keep history for"
        unit="days"
        hint="7 to 3,650; older history is deleted once a day"
        error={error}
        value={keepDays}
        onChange={(value) => {
          setKeepDays(value);
          setSave({ kind: "idle" });
        }}
      />
      <FormActions
        save={save}
        onSave={() => void submit()}
        onReset={() => {
          setKeepDays(String(settings.defaults.keepDays));
          setError(undefined);
          setSave({ kind: "idle" });
        }}
      />
    </SettingsPanel>
  );
}
