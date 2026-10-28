import { Tooltip } from "radix-ui";
import { useState, type MouseEvent } from "react";
import type { ExchangeHealth } from "@/api";
import { formatClock, formatDuration, formatTime, parseTime } from "@/lib/format";
import {
  bucketMinutes,
  mergeSegments,
  toBuckets,
  type Bucket,
  type TimelineState,
} from "@/screens/health/timelineBuckets";

const STATE_LABEL: Record<TimelineState, string> = {
  valid: "Connected, data checked",
  invalid: "Gap, data not trusted",
  nodata: "No data",
};

const HATCH = "repeating-linear-gradient(135deg, #1A202A 0 4px, #11151C 4px 8px)";

/** One row per exchange: valid, gap and no-data stretches, with a short marker at each reconnect. */
export function Timeline({
  exchanges,
  fromMs,
  toMs,
  hours,
}: {
  exchanges: ExchangeHealth[];
  fromMs: number;
  toMs: number;
  hours: number;
}) {
  const span = toMs - fromMs;
  const at = (ms: number) => `${(((ms - fromMs) / span) * 100).toFixed(3)}%`;
  const width = (from: number, to: number) => `${(((to - from) / span) * 100).toFixed(3)}%`;
  const minutes = bucketMinutes(hours);
  const ticks = [0, 0.25, 0.5, 0.75, 1].map((f) => fromMs + f * span);

  return (
    <section className="panel flex flex-col gap-[18px] px-5 py-[18px]">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="h2">Connection timeline</h2>
        <Legend />
      </div>
      <Tooltip.Provider delayDuration={100}>
        <div className="grid grid-cols-[90px_minmax(0,1fr)] items-center gap-x-4 gap-y-3.5">
          {exchanges.map((exchange) => (
            <Row
              key={exchange.id}
              exchange={exchange}
              fromMs={fromMs}
              toMs={toMs}
              minutes={minutes}
              at={at}
              width={width}
            />
          ))}
          <span />
          <div className="lbl mono flex justify-between">
            {ticks.map((t) => (
              <span key={t}>{hours <= 24 ? formatClock(t) : dayLabel(t)}</span>
            ))}
          </div>
        </div>
      </Tooltip.Provider>
    </section>
  );
}

function Row({
  exchange,
  fromMs,
  toMs,
  minutes,
  at,
  width,
}: {
  exchange: ExchangeHealth;
  fromMs: number;
  toMs: number;
  minutes: number;
  at: (ms: number) => string;
  width: (from: number, to: number) => string;
}) {
  const buckets = toBuckets(exchange.minutes, fromMs, toMs, minutes);
  const segments = mergeSegments(buckets);
  const [hover, setHover] = useState<{ bucket: Bucket; left: number } | null>(null);

  const onMove = (event: MouseEvent<HTMLDivElement>) => {
    const box = event.currentTarget.getBoundingClientRect();
    const fraction = Math.min(Math.max((event.clientX - box.left) / box.width, 0), 0.9999);
    const bucket = buckets[Math.floor(fraction * buckets.length)];
    setHover(bucket ? { bucket, left: event.clientX - box.left } : null);
  };

  const gaps = buckets.reduce((sum, b) => sum + b.gaps, 0);
  const summary = `${exchange.exchange}: ${gaps} gap${gaps === 1 ? "" : "s"}, ${exchange.reconnects.length} reconnect${
    exchange.reconnects.length === 1 ? "" : "s"
  }, valid ${formatDuration(exchange.validSeconds)} of ${formatDuration(exchange.runningSeconds)} running.`;

  return (
    <>
      <span className="text-sm">{exchange.exchange}</span>
      <div className="relative">
        <div
          className="relative h-7 overflow-visible rounded-md"
          style={{ background: HATCH }}
          role="img"
          aria-label={summary}
          onMouseMove={onMove}
          onMouseLeave={() => setHover(null)}
        >
          {segments
            .filter((s) => s.state !== "nodata")
            .map((s) =>
              s.state === "valid" ? (
                <div
                  key={s.startMs}
                  className="absolute inset-y-0"
                  style={{
                    left: at(s.startMs),
                    width: width(s.startMs, s.endMs),
                    background: "var(--mg-valid-track)",
                  }}
                />
              ) : (
                <div
                  key={s.startMs}
                  className="absolute -top-1 -bottom-1 min-w-1 rounded-[2px]"
                  style={{
                    left: at(s.startMs),
                    width: width(s.startMs, s.endMs),
                    background: "var(--mg-sell)",
                  }}
                />
              ),
            )}
        </div>
        {exchange.reconnects.map((reconnect) => {
          const time = parseTime(reconnect.timeUtc);
          return (
            <Tooltip.Root key={reconnect.timeUtc}>
              <Tooltip.Trigger asChild>
                <button
                  type="button"
                  aria-label={`Reconnect at ${formatTime(time)}: ${reconnect.reason}`}
                  className="absolute -top-2 h-3 w-[7px] -translate-x-1/2 cursor-default border-0 p-0"
                  style={{ left: at(time), background: "transparent" }}
                >
                  <span
                    className="mx-auto block h-full w-[3px] rounded-[2px]"
                    style={{ background: "var(--mg-warning)" }}
                  />
                </button>
              </Tooltip.Trigger>
              <Tooltip.Portal>
                <Tooltip.Content
                  side="top"
                  sideOffset={6}
                  className="max-w-xs rounded-md border border-border bg-raised px-2.5 py-1.5 text-xs"
                >
                  <div className="mono">{formatTime(time)}</div>
                  <div className="text-muted-foreground">Reconnect: {reconnect.reason}</div>
                </Tooltip.Content>
              </Tooltip.Portal>
            </Tooltip.Root>
          );
        })}
        {hover && <BucketDetails bucket={hover.bucket} left={hover.left} />}
      </div>
    </>
  );
}

function BucketDetails({ bucket, left }: { bucket: Bucket; left: number }) {
  const minute = bucket.endMs - bucket.startMs <= 60_000;
  const lines: string[] = [];
  if (bucket.running === 0) {
    lines.push("The app was not running");
  } else {
    const parts = [
      `valid ${formatDuration(bucket.valid)}`,
      ...(bucket.invalid > 0 ? [`invalid ${formatDuration(bucket.invalid)}`] : []),
    ];
    lines.push(`Ran ${formatDuration(bucket.running)}: ${parts.join(", ")}`);
    if (bucket.state === "nodata") {
      lines.push("No checked book: waiting for a snapshot, or not connected");
    }
  }
  if (bucket.gaps > 0) {
    lines.push(`${bucket.gaps} gap${bucket.gaps === 1 ? "" : "s"} started`);
  }
  if (bucket.reconnects > 0) {
    lines.push(`${bucket.reconnects} reconnect${bucket.reconnects === 1 ? "" : "s"}`);
  }
  return (
    <div
      className="pointer-events-none absolute top-9 z-10 w-max max-w-xs -translate-x-1/2 rounded-md border border-border bg-raised px-2.5 py-1.5 text-xs"
      style={{ left }}
      role="tooltip"
    >
      <div className="mono">
        {minute
          ? formatClock(bucket.startMs)
          : `${formatClock(bucket.startMs)} to ${formatClock(bucket.endMs)}`}{" "}
        · {STATE_LABEL[bucket.state]}
      </div>
      {lines.map((line) => (
        <div key={line} className="text-muted-foreground">
          {line}
        </div>
      ))}
    </div>
  );
}

function Legend() {
  return (
    <div className="flex flex-wrap gap-4 text-xs text-muted-foreground">
      <span className="inline-flex items-center gap-1.5">
        <span className="h-2.5 w-4 rounded-[3px]" style={{ background: "var(--mg-valid-track)" }} />
        {STATE_LABEL.valid}
      </span>
      <span className="inline-flex items-center gap-1.5">
        <span className="h-3.5 w-1 rounded-[2px]" style={{ background: "var(--mg-sell)" }} />
        {STATE_LABEL.invalid}
      </span>
      <span className="inline-flex items-center gap-1.5">
        <span
          className="h-2.5 w-4 rounded-[3px] border border-border"
          style={{ background: HATCH }}
        />
        No data (app not running, or no checked book)
      </span>
      <span className="inline-flex items-center gap-1.5">
        <span className="relative h-3.5 w-4">
          <span className="absolute inset-x-0 bottom-0 h-2 rounded-[2px] bg-subtle" />
          <span
            className="absolute top-0 left-1/2 h-2 w-[3px] -translate-x-1/2 rounded-[2px]"
            style={{ background: "var(--mg-warning)" }}
          />
        </span>
        Reconnect
      </span>
    </div>
  );
}

function dayLabel(ms: number): string {
  const date = new Date(ms);
  return `${date.toLocaleDateString("en-US", { weekday: "short" })} ${formatClock(ms)}`;
}
