import type { ExchangeStatus } from "@/api";
import { bookBadge, connectionBadge } from "@/components/badges";
import { StatusBadge } from "@/components/StatusBadge";
import { formatDelay, lowerBoundNote } from "@/lib/delay";
import { exchangeInfo } from "@/lib/exchanges";
import { formatCount, formatHours, formatUptime } from "@/lib/format";

/** Uptime over the last 24 hours, from the history. */
export interface Uptime {
  fraction: number | null;
  runningSeconds: number;
  validSeconds: number;
}

export function ExchangeCard({ exchange, uptime }: { exchange: ExchangeStatus; uptime?: Uptime }) {
  const info = exchangeInfo(exchange.id);
  const blocked = exchange.state === "Blocked";
  return (
    <section className="panel flex flex-col gap-4 px-[18px] py-[18px]" aria-label={exchange.name}>
      <div className="flex items-center justify-between gap-2">
        <div className="flex min-w-0 items-center gap-2">
          <span
            className="size-2.5 shrink-0 rounded-[3px]"
            style={{ background: info.color }}
            aria-hidden="true"
          />
          <h2 className="h2 whitespace-nowrap">{exchange.name}</h2>
          <span className="lbl mono truncate">{exchange.pair}</span>
        </div>
        <StatusBadge badge={connectionBadge(exchange.state)} />
      </div>

      {blocked ? (
        <div
          className="flex min-h-[124px] flex-col justify-center gap-1.5"
          title={exchange.lastError ?? undefined}
        >
          <p className="bad m-0 text-lg font-semibold">Blocked in your region</p>
          <p className="lbl m-0 text-[13px]">
            {exchange.name} refuses connections from this country (HTTP 451). Marketgrade tries
            again every 30 minutes.
          </p>
        </div>
      ) : (
        <div className="grid grid-cols-2 gap-x-4 gap-y-3.5">
          <Stat
            label="Delay (median)"
            value={formatDelay(exchange.delayP50Ms, exchange.delayLowerBound)}
            title={
              exchange.delayLowerBound
                ? lowerBoundNote(exchange.delayAheadMs)
                : "Exchange timestamp to arrival on this computer, last minute"
            }
          />
          <Stat
            label="Book updates/s"
            value={formatCount(exchange.messagesPerSecond)}
            title="Order book messages per second; trades are counted separately"
          />
          <Stat
            label="Gaps today"
            value={formatCount(exchange.gapsToday)}
            className={exchange.gapsToday > 0 ? "warn" : undefined}
            title={
              exchange.lastGap ? `Last gap: ${exchange.lastGap.cause}` : "Days start at 00:00 UTC"
            }
          />
          <Stat
            label="Uptime, 24 h"
            value={formatUptime(uptime?.fraction)}
            title={
              uptime && uptime.runningSeconds > 0
                ? `Book valid for ${formatHours(uptime.validSeconds)} of the ${formatHours(uptime.runningSeconds)} the app ran`
                : "The app has not run in the last 24 hours"
            }
          />
        </div>
      )}

      <div className="mt-auto flex flex-col items-start gap-2 border-t border-subtle pt-3">
        <span className="text-[13px] text-muted-foreground">{info.checkedBy}</span>
        {!blocked && (
          <StatusBadge
            badge={bookBadge(exchange.bookState)}
            icon={exchange.bookState === "Invalid" ? "alert" : undefined}
          />
        )}
      </div>
    </section>
  );
}

function Stat({
  label,
  value,
  className,
  title,
}: {
  label: string;
  value: string;
  className?: string;
  title?: string;
}) {
  return (
    <div title={title}>
      <div className="lbl">{label}</div>
      <div className={`stat-value ${className ?? ""}`}>{value}</div>
    </div>
  );
}
