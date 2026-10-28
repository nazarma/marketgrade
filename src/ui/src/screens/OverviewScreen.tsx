import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";
import { getDelayHistory, getGaps, getHealthHistory } from "@/api";
import { ScreenHeader } from "@/app/ScreenHeader";
import { useLiveStore } from "@/live/liveStore";
import { exchangeInfo } from "@/lib/exchanges";
import { clockNote } from "@/lib/clock";
import { parseTime } from "@/lib/format";
import { BestPrices } from "@/screens/overview/BestPrices";
import { DelayChart } from "@/screens/overview/DelayChart";
import { delayChartData, gapMarkers } from "@/screens/overview/delayChartData";
import { ExchangeCard } from "@/screens/overview/ExchangeCard";
import { RecentEvents } from "@/screens/overview/RecentEvents";
import { RecordingLine } from "@/screens/overview/RecordingLine";

const MINUTE = 60_000;

export function OverviewScreen() {
  const status = useLiveStore((state) => state.status);
  const events = useLiveStore((state) => state.events);
  const exchanges = status?.exchanges ?? [];
  const delay = useQuery({
    queryKey: ["history", "delay", 60],
    queryFn: () => getDelayHistory(60),
    refetchInterval: 30_000,
  });
  const health = useQuery({
    queryKey: ["history", "health", 24],
    queryFn: () => getHealthHistory(24),
    refetchInterval: MINUTE,
  });
  const gaps = useQuery({
    queryKey: ["history", "gaps", 1],
    queryFn: () => getGaps(1),
    refetchInterval: 30_000,
  });

  // The live status arrives every second; the chart's last point follows it.
  const statusTime = status?.timeUtc ?? delay.data?.toUtc;
  const chart = useMemo(
    () => delayChartData(delay.data, exchanges, statusTime ? parseTime(statusTime) : 0),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [delay.data, statusTime],
  );
  const markers = useMemo(
    () => gapMarkers(gaps.data, exchanges, (chart.x[0] ?? 0) * 1000),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [gaps.data, chart],
  );
  const clock = clockNote(status?.clockOffsetMs ?? null);

  return (
    <>
      <ScreenHeader
        title="Overview"
        description={`BTC/USD on ${exchanges.length || 3} exchanges, measured from this computer`}
      >
        {status?.recording && <RecordingLine recording={status.recording} />}
      </ScreenHeader>

      <div className="grid grid-cols-[repeat(auto-fit,minmax(260px,1fr))] gap-4">
        {exchanges.map((exchange) => {
          const history = health.data?.exchanges.find((e) => e.id === exchange.id);
          return (
            <ExchangeCard
              key={exchange.id}
              exchange={exchange}
              uptime={
                history && {
                  fraction: history.uptime,
                  runningSeconds: history.runningSeconds,
                  validSeconds: history.validSeconds,
                }
              }
            />
          );
        })}
      </div>

      <div className="flex flex-wrap gap-4">
        <section className="panel flex min-w-0 flex-[2_1_520px] flex-col gap-3 px-5 py-[18px]">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <h2 className="h2">Delay, last 60 minutes</h2>
            <Legend ids={exchanges.map((e) => e.id)} />
          </div>
          <DelayChart data={chart} markers={markers} />
          <div className="lbl">
            Median milliseconds between the exchange sending a message and this computer receiving
            it, per minute. Breaks are minutes when the app was not running.
          </div>
          {clock && <div className="lbl warn">{clock}</div>}
        </section>
        <BestPrices exchanges={exchanges} />
      </div>

      <RecentEvents events={events} />
    </>
  );
}

function Legend({ ids }: { ids: number[] }) {
  return (
    <div className="flex gap-4 text-xs text-muted-foreground">
      {ids.map((id) => {
        const info = exchangeInfo(id);
        return (
          <span key={id} className="inline-flex items-center gap-1.5">
            <svg width="18" height="4" aria-hidden="true">
              <line
                x1="0"
                y1="2"
                x2="18"
                y2="2"
                stroke={info.color}
                strokeWidth="2"
                strokeDasharray={info.dash.join(" ") || undefined}
              />
            </svg>
            {info.name}
          </span>
        );
      })}
    </div>
  );
}
