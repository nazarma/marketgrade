import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { getGaps, getHealthHistory } from "@/api";
import { ScreenHeader } from "@/app/ScreenHeader";
import { parseTime } from "@/lib/format";
import { ChecksPanel } from "@/screens/health/ChecksPanel";
import { DelayVolume } from "@/screens/health/DelayVolume";
import { GapLog } from "@/screens/health/GapLog";
import { Timeline } from "@/screens/health/Timeline";

const DAY = { hours: 24, label: "Last 24 hours" };
const RANGES = [DAY, { hours: 168, label: "Last 7 days" }];

export function HealthScreen() {
  const [hours, setHours] = useState(24);
  const range = RANGES.find((r) => r.hours === hours) ?? DAY;
  const health = useQuery({
    queryKey: ["history", "health", hours],
    queryFn: () => getHealthHistory(hours),
    refetchInterval: 60_000,
  });
  const gaps = useQuery({
    queryKey: ["history", "gaps", hours],
    queryFn: () => getGaps(hours),
    refetchInterval: 60_000,
  });
  const exchanges = health.data?.exchanges ?? [];

  return (
    <>
      <ScreenHeader
        title="Data health"
        description={`Is the data correct, complete and on time? ${range.label}.`}
      >
        <div className="flex" role="group" aria-label="Range">
          {RANGES.map((r, i) => (
            <button
              key={r.hours}
              type="button"
              className={`btn-secondary ${i === 0 ? "rounded-r-none" : "-ml-px rounded-l-none"}`}
              aria-pressed={r.hours === hours}
              onClick={() => setHours(r.hours)}
            >
              {r.label}
            </button>
          ))}
        </div>
      </ScreenHeader>

      {health.error && (
        <p className="bad m-0 text-sm">The history could not be read: {String(health.error)}</p>
      )}
      {health.data && (
        <Timeline
          exchanges={exchanges}
          fromMs={parseTime(health.data.fromUtc)}
          toMs={parseTime(health.data.toUtc)}
          hours={hours}
        />
      )}

      <div className="flex flex-wrap gap-4">
        <DelayVolume exchanges={exchanges} rangeLabel={range.label} />
        <ChecksPanel ids={exchanges.length > 0 ? exchanges.map((e) => e.id) : [1, 2, 3]} />
      </div>

      <GapLog gaps={gaps.data} rangeLabel={range.label} />
    </>
  );
}
