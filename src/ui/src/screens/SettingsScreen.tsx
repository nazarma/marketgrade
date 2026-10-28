import { ScreenHeader } from "@/app/ScreenHeader";
import { HistorySettingsForm } from "@/screens/settings/HistorySettingsForm";
import { RecordingSettingsForm } from "@/screens/settings/RecordingSettingsForm";

export function SettingsScreen() {
  return (
    <>
      <ScreenHeader
        title="Settings"
        description="Recording and history on this computer. Changes apply at once."
      />
      <RecordingSettingsForm />
      <HistorySettingsForm />
    </>
  );
}
