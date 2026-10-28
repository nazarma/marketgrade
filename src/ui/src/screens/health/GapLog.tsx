import type { GapEntry } from "@/api";
import { gapKindLabel } from "@/lib/exchanges";
import { formatDuration, formatTime, parseTime } from "@/lib/format";

export function GapLog({ gaps, rangeLabel }: { gaps: GapEntry[] | undefined; rangeLabel: string }) {
  return (
    <section className="panel flex flex-col gap-2 px-2 pt-[18px] pb-2">
      <h2 className="h2 px-3">Gap log</h2>
      {gaps && gaps.length === 0 ? (
        <p className="lbl px-3 pb-2">No gaps: {rangeLabel.toLowerCase()}, every check passed.</p>
      ) : (
        <div className="overflow-x-auto">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Started</th>
                <th scope="col">Exchange</th>
                <th scope="col">Found by</th>
                <th scope="col" className="r">
                  Duration
                </th>
                <th scope="col">What the app did</th>
              </tr>
            </thead>
            <tbody>
              {(gaps ?? []).map((gap) => {
                const start = parseTime(gap.startUtc);
                return (
                  <tr key={`${gap.exchangeId}-${gap.startUtc}`}>
                    <td className="mono whitespace-nowrap">
                      {isToday(start)
                        ? ""
                        : `${new Date(start).toLocaleDateString("en-US", { month: "short", day: "numeric" })} `}
                      {formatTime(start, true)}
                    </td>
                    <td>{gap.exchange}</td>
                    <td className={gap.kind === "ConnectionLost" ? "" : "warn"} title={gap.cause}>
                      {gapKindLabel(gap.kind, gap.exchangeId)}
                    </td>
                    <td className="mono r whitespace-nowrap">
                      {formatDuration(gap.durationSeconds)}
                    </td>
                    <td className="text-muted-foreground">{gap.repairAction}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function isToday(ms: number): boolean {
  return new Date(ms).toDateString() === new Date().toDateString();
}
