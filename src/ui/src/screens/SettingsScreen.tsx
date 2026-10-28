import { ScreenHeader } from "@/app/ScreenHeader";
import { RecordingSettingsForm } from "@/RecordingSettingsForm";

export function SettingsScreen() {
  return (
    <>
      <ScreenHeader title="Settings" description="Recording and history on this computer" />
      <RecordingSettingsForm />
    </>
  );
}
