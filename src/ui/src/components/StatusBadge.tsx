import { AlertIcon, CheckIcon, CrossIcon, SyncIcon } from "@/app/icons";
import type { Badge } from "@/components/badges";

const icons = { ok: CheckIcon, warn: SyncIcon, bad: CrossIcon, muted: AlertIcon };

/** A status as a pill with an icon and text, so colour is never the only signal. */
export function StatusBadge({ badge, icon }: { badge: Badge; icon?: "alert" }) {
  const Icon = icon === "alert" ? AlertIcon : icons[badge.tone];
  return (
    <span
      className={`status-badge ${badge.tone === "muted" ? "text-muted-foreground" : badge.tone}`}
    >
      <Icon />
      {badge.label}
    </span>
  );
}
