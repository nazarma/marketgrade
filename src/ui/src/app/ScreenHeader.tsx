import type { ReactNode } from "react";
import { useLiveStore } from "@/live/liveStore";

/** Title, a one-line description and the screen's actions, followed by the connection pill on every screen. */
export function ScreenHeader({
  title,
  description,
  children,
}: {
  title: string;
  description: string;
  children?: ReactNode;
}) {
  return (
    <header className="flex flex-wrap items-center justify-between gap-4">
      <div className="min-w-0">
        <h1 className="m-0 text-[22px] font-semibold">{title}</h1>
        <p className="mt-1 mb-0 text-[13px] text-muted-foreground">{description}</p>
      </div>
      <div className="flex flex-wrap items-center gap-2.5">
        {children}
        <ConnectionPill />
      </div>
    </header>
  );
}

/** "Live" while the push connection to the backend is up, "Reconnecting" otherwise. */
export function ConnectionPill() {
  const live = useLiveStore((state) => state.hub === "live");
  return (
    <span className="pill" role="status">
      <span className={live ? "dot" : "dot dot-warn"} aria-hidden="true" />
      {live ? "Live" : "Reconnecting"}
    </span>
  );
}
