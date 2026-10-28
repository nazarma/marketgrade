import { Link } from "react-router";
import type { ConnectionEvent } from "@/api";
import { formatTime } from "@/lib/format";

const SHOWN = 10;

/** Events that mean data was or may be missing, shown in the warning colour. */
const ISSUES =
  /invalid|dead|lost|closed by server|blocked|error|rate limited|paused|not compressed|repaired/i;

export function RecentEvents({ events }: { events: ConnectionEvent[] }) {
  const recent = events.slice(-SHOWN).reverse();
  return (
    <section className="panel flex flex-col gap-2 px-2 pt-[18px] pb-2">
      <div className="flex items-center justify-between px-3">
        <h2 className="h2">Recent events</h2>
        <Link to="/health" className="text-[13px]">
          Open data health
        </Link>
      </div>
      {recent.length === 0 ? (
        <p className="lbl px-3 pb-2">No events yet.</p>
      ) : (
        <div className="overflow-x-auto">
          <table className="tbl">
            <thead>
              <tr>
                <th scope="col">Time</th>
                <th scope="col">Exchange</th>
                <th scope="col">Event</th>
                <th scope="col">Detail</th>
              </tr>
            </thead>
            <tbody>
              {recent.map((e, i) => (
                <tr key={`${e.timeUtc}-${e.exchange}-${i}`}>
                  <td className="mono whitespace-nowrap">{formatTime(e.timeUtc)}</td>
                  <td className="whitespace-nowrap">{e.exchange}</td>
                  <td className={`whitespace-nowrap ${ISSUES.test(e.event) ? "warn" : ""}`}>
                    {e.event}
                  </td>
                  <td className="text-muted-foreground">{e.detail ?? ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
