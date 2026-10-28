import type { RecordingStatus } from "@/api";
import { recordingBadge } from "@/components/badges";
import { formatBytes } from "@/lib/format";

/** Recording state and sizes, in the Overview header. */
export function RecordingLine({ recording }: { recording: RecordingStatus }) {
  const badge = recordingBadge(recording.state);
  const dot = badge.tone === "ok" ? "dot" : badge.tone === "muted" ? "dot dot-off" : "dot dot-warn";
  return (
    <div
      className="flex flex-wrap items-center gap-2.5"
      title={recording.lastError ?? recording.folder}
    >
      <span className="pill">
        <span className={dot} aria-hidden="true" />
        {badge.label}
      </span>
      <span className="lbl mono whitespace-nowrap">
        today {formatBytes(recording.todayBytes)} · total {formatBytes(recording.totalBytes)} ·{" "}
        {formatBytes(recording.freeBytes)} free
        {recording.droppedMessages > 0 && (
          <span className="warn">
            {" "}
            · {recording.droppedMessages.toLocaleString("en-US")} not recorded
          </span>
        )}
      </span>
    </div>
  );
}
