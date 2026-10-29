import { describe, expect, it } from "vitest";
import type { DelayHistory } from "@/api";
import { delayChartData, gapMarkers, niceMax } from "@/screens/overview/delayChartData";
import { exchangeStatus } from "@/test/exchangeStatus";

const now = Date.parse("2026-10-28T13:53:30Z");

describe("delay chart data", () => {
  it("has a point per minute, null where the app was not running, and now from the live status", () => {
    const history: DelayHistory = {
      fromUtc: "2026-10-28T13:50:00Z",
      toUtc: "2026-10-28T13:53:30Z",
      exchanges: [
        {
          id: 2,
          exchange: "Coinbase",
          points: [
            {
              minuteUtc: "2026-10-28T13:50:00Z",
              p50Ms: 21,
              p99Ms: 40,
              lowerBound: false,
              aheadMs: null,
            },
            {
              minuteUtc: "2026-10-28T13:52:00.0000000Z",
              p50Ms: 1.2,
              p99Ms: 45,
              lowerBound: true,
              aheadMs: 6.5,
            },
          ],
        },
      ],
    };

    const data = delayChartData(history, [exchangeStatus({ delayP50Ms: 22.4 })], now, 3);

    expect(data.x).toEqual(
      [
        13 * 60 + 50,
        13 * 60 + 51,
        13 * 60 + 52,
        (now - Date.parse("2026-10-28T00:00:00Z")) / 60_000,
      ].map((m, i) => (i < 3 ? Date.parse("2026-10-28T00:00:00Z") / 1000 + m * 60 : now / 1000)),
    );
    expect(data.y).toEqual([[21, null, 1.2, 22.4]]);
    expect(data.lowerBound).toEqual([[false, false, true, false]]);
    expect(data.aheadMs).toEqual([[null, null, 6.5, null]]);
    expect(data.exchanges).toEqual([{ id: 2, name: "Coinbase" }]);
  });

  it("marks each gap once, from the gap log or a gap still open", () => {
    const gaps = [
      {
        exchangeId: 1,
        exchange: "Kraken",
        startUtc: "2026-10-28T13:41:07.204Z",
        endUtc: "2026-10-28T13:41:08Z",
        durationSeconds: 0.8,
        kind: "Checksum" as const,
        cause: "",
        repairAction: "",
      },
    ];
    const live = [
      exchangeStatus({
        id: 1,
        name: "Kraken",
        lastGap: {
          startUtc: "2026-10-28T13:41:07.2041234Z",
          endUtc: null,
          cause: "",
          startSeq: 1,
          endSeq: null,
          durationSeconds: null,
        },
      }),
    ];

    const markers = gapMarkers(gaps, live, Date.parse("2026-10-28T12:53:00Z"));

    expect(markers).toEqual([
      { timeSec: Date.parse("2026-10-28T13:41:07.204Z") / 1000, label: "Kraken gap" },
    ]);
    expect(gapMarkers(gaps, [], Date.parse("2026-10-28T13:45:00Z"))).toEqual([]);
  });

  it("gives gaps of several exchanges in the same minute one marker", () => {
    const gap = (exchange: string, startUtc: string) => ({
      exchangeId: 1,
      exchange,
      startUtc,
      endUtc: startUtc,
      durationSeconds: 1,
      kind: "ConnectionLost" as const,
      cause: "",
      repairAction: "",
    });

    const markers = gapMarkers(
      [
        gap("Binance", "2026-10-28T23:49:20.894Z"),
        gap("Kraken", "2026-10-28T23:49:20.894Z"),
        gap("Kraken", "2026-10-28T23:55:00Z"),
      ],
      [],
      Date.parse("2026-10-28T23:00:00Z"),
    );

    expect(markers.map((m) => m.label)).toEqual(["Binance, Kraken gap", "Kraken gap"]);
  });

  it("rounds the top of the scale up with room above the highest value", () => {
    expect(niceMax(196)).toBe(300);
    expect(niceMax(31)).toBe(40);
    expect(niceMax(null)).toBe(100);
  });
});
