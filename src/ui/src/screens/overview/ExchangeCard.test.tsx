// @vitest-environment jsdom
import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { ExchangeCard } from "@/screens/overview/ExchangeCard";
import { exchangeStatus } from "@/test/exchangeStatus";

const uptime = { fraction: 0.9997, runningSeconds: 23.5 * 3600, validSeconds: 23.49 * 3600 };

describe("ExchangeCard", () => {
  afterEach(cleanup);

  it("shows the delay, book updates, gaps, uptime and how the book is checked", () => {
    render(<ExchangeCard exchange={exchangeStatus({ gapsToday: 1 })} uptime={uptime} />);

    const card = screen.getByRole("region", { name: "Coinbase" });
    expect(within(card).getByText("Connected")).toBeDefined();
    expect(within(card).getByText("22 ms")).toBeDefined();
    expect(within(card).getByText("1,936")).toBeDefined();
    expect(within(card).getByText("1").className).toContain("warn");
    expect(within(card).getByText("99.97%").parentElement?.title).toBe(
      "Book valid for 23.5 h of the 23.5 h the app ran",
    );
    expect(within(card).getByText("Checked by sequence number")).toBeDefined();
    expect(within(card).getByText("Book checked")).toBeDefined();
  });

  it("shows a blocked exchange prominently instead of its numbers", () => {
    render(
      <ExchangeCard
        exchange={exchangeStatus({
          id: 3,
          name: "Binance",
          pair: "BTCUSDT",
          state: "Blocked",
          bookState: "Syncing",
          delayP50Ms: null,
        })}
      />,
    );

    const card = screen.getByRole("region", { name: "Binance" });
    expect(within(card).getAllByText("Blocked in your region")).toHaveLength(2);
    expect(within(card).queryByText("Delay (median)")).toBeNull();
    expect(within(card).getByText("Checked by update ID range")).toBeDefined();
  });

  it("says when there is no book and no data yet", () => {
    render(
      <ExchangeCard
        exchange={exchangeStatus({
          state: "Connecting",
          bookState: "NoPairRules",
          delayP50Ms: null,
        })}
      />,
    );

    const card = screen.getByRole("region", { name: "Coinbase" });
    expect(within(card).getByText("Connecting")).toBeDefined();
    expect(within(card).getByText("No book")).toBeDefined();
    expect(within(card).getAllByText("–")).toHaveLength(2);
  });

  it("marks an invalid book while it is repaired", () => {
    render(
      <ExchangeCard
        exchange={exchangeStatus({
          state: "Reconnecting",
          bookState: "Invalid",
          gapsToday: 2,
          lastGap: {
            startUtc: "2026-10-28T22:30:00Z",
            endUtc: null,
            cause: "Sequence gap: expected 11, got 12",
            startSeq: 1,
            endSeq: null,
            durationSeconds: null,
          },
        })}
      />,
    );

    expect(screen.getByText("Reconnecting")).toBeDefined();
    expect(screen.getByText("Book invalid, repairing").className).toContain("bad");
    expect(screen.getByText("2").parentElement?.title).toBe(
      "Last gap: Sequence gap: expected 11, got 12",
    );
  });
});
