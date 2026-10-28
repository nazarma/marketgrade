import type { ExchangeHealth } from "@/api";
import { formatCompact, formatHours, formatMs, formatUptime } from "@/lib/format";

export function DelayVolume({
  exchanges,
  rangeLabel,
}: {
  exchanges: ExchangeHealth[];
  rangeLabel: string;
}) {
  return (
    <section className="panel flex min-w-0 flex-[1_1_460px] flex-col gap-2 px-2 pt-[18px] pb-2">
      <h2 className="h2 px-3">Delay and volume</h2>
      <div className="overflow-x-auto">
        <table className="tbl">
          <thead>
            <tr>
              <th scope="col">Exchange</th>
              <th scope="col" className="r">
                Median
              </th>
              <th scope="col" className="r">
                99th percentile
              </th>
              <th scope="col" className="r">
                Worst
              </th>
              <th scope="col" className="r">
                Book updates
              </th>
              <th scope="col" className="r">
                Trades
              </th>
              <th scope="col" className="r">
                Uptime
              </th>
            </tr>
          </thead>
          <tbody>
            {exchanges.map((e) => {
              // A worst case worth a look: a second or more, or far beyond the usual worst.
              const unusual =
                e.delayMaxMs !== null &&
                (e.delayMaxMs >= 1_000 ||
                  (e.delayP99Ms !== null && e.delayMaxMs > 5 * e.delayP99Ms));
              return (
                <tr key={e.id}>
                  <td>{e.exchange}</td>
                  <td className="mono r">{formatMs(e.delayP50Ms)}</td>
                  <td className="mono r">{formatMs(e.delayP99Ms)}</td>
                  <td className={`mono r ${unusual ? "warn" : ""}`}>{formatMs(e.delayMaxMs)}</td>
                  <td className="mono r">{formatCompact(e.bookUpdates)}</td>
                  <td className="mono r">{formatCompact(e.trades)}</td>
                  <td
                    className="mono r"
                    title={
                      e.runningSeconds > 0
                        ? `Book valid for ${formatHours(e.validSeconds)} of the ${formatHours(e.runningSeconds)} the app ran`
                        : "The app did not run in this range"
                    }
                  >
                    {formatUptime(e.uptime)}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <p className="lbl mx-3 mt-1 mb-0">
        99th percentile: 99 of 100 messages arrived faster than this.
      </p>
      <p className="lbl mx-3 mt-0 mb-2">
        {rangeLabel}, while the app was running. Median and 99th percentile are approximate (within
        about 12 %); worst is exact. Uptime is the time with a checked, valid book divided by the
        time the app ran.
      </p>
    </section>
  );
}
