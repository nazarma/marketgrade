import { describe, expect, it } from "vitest";
import {
  formatAmount,
  formatBytes,
  formatCompact,
  formatCount,
  formatDuration,
  formatHours,
  formatMicros,
  formatMs,
  formatPrice,
  formatPriceText,
  formatRate,
  formatTime,
  formatUptime,
  NONE,
} from "@/lib/format";

describe("formatters", () => {
  it("keeps a price's decimals and adds thousands separators", () => {
    expect(formatPriceText("81176.70")).toBe("81,176.70");
    expect(formatPriceText("67003.4")).toBe("67,003.4");
    expect(formatPriceText("0.01")).toBe("0.01");
    expect(formatPriceText("1234567")).toBe("1,234,567");
    expect(formatPriceText("-1234.5")).toBe("-1,234.5");
    expect(formatPriceText(null)).toBe(NONE);
    expect(formatPrice(81176.7, 2)).toBe("81,176.70");
    expect(formatPrice(81176.75, 1)).toBe("81,176.8");
    expect(formatPrice(null, 2)).toBe(NONE);
  });

  it("shows delays in milliseconds with one decimal under 10 ms", () => {
    expect(formatMs(7.34)).toBe("7.3 ms");
    expect(formatMs(9.96)).toBe("10.0 ms");
    expect(formatMs(31.4)).toBe("31 ms");
    expect(formatMs(4210)).toBe("4,210 ms");
    expect(formatMs(0)).toBe("0.0 ms");
    expect(formatMs(null)).toBe(NONE);
  });

  it("shows processing times in microseconds", () => {
    expect(formatMicros(7_264)).toBe("7.3 µs");
    expect(formatMicros(43_264)).toBe("43 µs");
    expect(formatMicros(1_250_000)).toBe("1,250 µs");
    expect(formatMicros(undefined)).toBe(NONE);
  });

  it("shows sizes in binary units", () => {
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(1536)).toBe("1.5 KB");
    expect(formatBytes(214 * 1024 ** 2)).toBe("214 MB");
    expect(formatBytes(20 * 1024 ** 3)).toBe("20.0 GB");
    expect(formatBytes(null)).toBe(NONE);
  });

  it("formats counts, compact counts and rates", () => {
    expect(formatCount(1936.4)).toBe("1,936");
    expect(formatCompact(950)).toBe("950");
    expect(formatCompact(22_100_000)).toBe("22.1 M");
    expect(formatCompact(209_000_000)).toBe("209.0 M");
    expect(formatCompact(12_400)).toBe("12.4 K");
    expect(formatRate(3870.2)).toBe("3,870 /s");
    expect(formatRate(4.46)).toBe("4.5 /s");
    expect(formatRate(0)).toBe("0 /s");
  });

  it("never rounds uptime up to 100 %", () => {
    expect(formatUptime(1)).toBe("100%");
    expect(formatUptime(0.99999)).toBe("99.99%");
    expect(formatUptime(0.9997)).toBe("99.97%");
    expect(formatUptime(0.8736)).toBe("87.3%");
    expect(formatUptime(null)).toBe(NONE);
  });

  it("formats durations and running hours", () => {
    expect(formatDuration(0.8)).toBe("0.8 s");
    expect(formatDuration(14.2)).toBe("14 s");
    expect(formatDuration(200)).toBe("3 min 20 s");
    expect(formatDuration(120)).toBe("2 min");
    expect(formatDuration(7_500)).toBe("2 h 5 min");
    expect(formatHours(23.5 * 3600)).toBe("23.5 h");
    expect(formatHours(148 * 3600)).toBe("6 days 4 h");
  });

  it("formats local times of day and amounts", () => {
    const time = new Date(2026, 9, 28, 13, 52, 4, 118);
    expect(formatTime(time)).toBe("13:52:04");
    expect(formatTime(time, true)).toBe("13:52:04.118");
    expect(formatTime("not a time")).toBe(NONE);
    expect(formatAmount(0.042)).toBe("0.0420");
    expect(formatAmount(1.23456)).toBe("1.2346");
    expect(formatAmount(0.00000005)).toBe("0.00000005");
    expect(formatAmount(0.0000579)).toBe("0.0000579");
  });
});
