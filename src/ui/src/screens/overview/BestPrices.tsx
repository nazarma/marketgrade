import type { ExchangeStatus } from "@/api";
import { formatPriceText } from "@/lib/format";

export function BestPrices({ exchanges }: { exchanges: ExchangeStatus[] }) {
  const usdt = exchanges.filter((e) => e.pair.endsWith("USDT")).map((e) => e.name);
  return (
    <section className="panel flex min-w-0 flex-[1_1_320px] flex-col gap-2 px-2 pt-[18px] pb-2">
      <h2 className="h2 px-3">Best prices now</h2>
      <div className="overflow-x-auto">
        <table className="tbl">
          <thead>
            <tr>
              <th scope="col">Exchange</th>
              <th scope="col" className="r">
                Best buy
              </th>
              <th scope="col" className="r">
                Best sell
              </th>
              <th scope="col" className="r">
                Spread
              </th>
            </tr>
          </thead>
          <tbody>
            {exchanges.map((e) => (
              <tr key={e.id}>
                <td>{e.name}</td>
                <td className="mono r ok">{formatPriceText(e.bestBid)}</td>
                <td className="mono r bad">{formatPriceText(e.bestAsk)}</td>
                <td className="mono r">{formatPriceText(e.spread)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {usdt.length > 0 && (
        <p className="lbl mx-3 mt-1 mb-2">
          {usdt.join(" and ")} quotes in USDT, so its prices are not directly comparable with USD.
        </p>
      )}
      <p className="lbl mx-3 mt-0 mb-2">Prices show only while the book is checked and valid.</p>
    </section>
  );
}
