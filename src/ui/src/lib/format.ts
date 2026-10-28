/** Number and time formats used across the screens. A missing value is always an en dash. */

export const NONE = "–";

const integer = new Intl.NumberFormat("en-US", { maximumFractionDigits: 0 });

/** Thousands separators in the integer part of a decimal string, keeping its decimals: "81176.70" → "81,176.70". */
export function formatPriceText(text: string | null | undefined): string {
  if (text === null || text === undefined || text === "") {
    return NONE;
  }
  const negative = text.startsWith("-");
  const [whole, fraction] = (negative ? text.slice(1) : text).split(".");
  const grouped = (whole ?? "").replace(/\B(?=(\d{3})+(?!\d))/g, ",");
  return `${negative ? "-" : ""}${grouped}${fraction === undefined ? "" : `.${fraction}`}`;
}

/** A price with the pair's decimals and thousands separators. */
export function formatPrice(value: number | null | undefined, decimals: number): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return NONE;
  }
  return value.toLocaleString("en-US", {
    minimumFractionDigits: decimals,
    maximumFractionDigits: decimals,
  });
}

/** Milliseconds: one decimal under 10 ms, whole milliseconds above ("7.3 ms", "31 ms", "4,210 ms"). */
export function formatMs(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms)) {
    return NONE;
  }
  return ms < 10 ? `${ms.toFixed(1)} ms` : `${integer.format(ms)} ms`;
}

/** Nanoseconds shown as microseconds, one decimal under 10 µs ("7.3 µs", "43 µs"). */
export function formatMicros(ns: number | null | undefined): string {
  if (ns === null || ns === undefined || !Number.isFinite(ns)) {
    return NONE;
  }
  const us = ns / 1000;
  return us < 10 ? `${us.toFixed(1)} µs` : `${integer.format(us)} µs`;
}

/** Binary units, as the settings use: 1 GB is 1024³ bytes. */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined) {
    return NONE;
  }
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${unit === 0 || value >= 100 ? value.toFixed(0) : value.toFixed(1)} ${units[unit]}`;
}

/** A whole number with thousands separators. */
export function formatCount(value: number | null | undefined): string {
  return value === null || value === undefined || !Number.isFinite(value)
    ? NONE
    : integer.format(value);
}

/** Large counts shortened: "950", "12.4 K", "22.1 M", "1.2 B". */
export function formatCompact(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return NONE;
  }
  const units: [number, string][] = [
    [1e9, "B"],
    [1e6, "M"],
    [1e3, "K"],
  ];
  for (const [size, unit] of units) {
    if (Math.abs(value) >= size) {
      return `${(value / size).toFixed(1)} ${unit}`;
    }
  }
  return integer.format(value);
}

/** A rate per second: whole numbers from 10 on, one decimal below ("1,936 /s", "4.5 /s"). */
export function formatRate(perSecond: number | null | undefined): string {
  if (perSecond === null || perSecond === undefined || !Number.isFinite(perSecond)) {
    return NONE;
  }
  return `${perSecond >= 10 || perSecond === 0 ? integer.format(perSecond) : perSecond.toFixed(1)} /s`;
}

/**
 * Uptime as a percentage that never rounds up to 100 % unless it is: "100%", "99.97%", "87.3%".
 * Two decimals from 99 % on, where the difference matters.
 */
export function formatUptime(fraction: number | null | undefined): string {
  if (fraction === null || fraction === undefined || !Number.isFinite(fraction)) {
    return NONE;
  }
  if (fraction >= 1) {
    return "100%";
  }
  const percent = fraction * 100;
  const decimals = percent >= 99 ? 2 : 1;
  const factor = 10 ** decimals;
  return `${(Math.floor(percent * factor) / factor).toFixed(decimals)}%`;
}

/** Seconds as "0.8 s", "14 s", "3 min 20 s", "2 h 5 min". */
export function formatDuration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) {
    return NONE;
  }
  if (seconds < 10) {
    return `${seconds.toFixed(1)} s`;
  }
  if (seconds < 60) {
    return `${Math.round(seconds)} s`;
  }
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    const rest = Math.round(seconds - minutes * 60);
    return rest === 0 ? `${minutes} min` : `${minutes} min ${rest} s`;
  }
  const hours = Math.floor(minutes / 60);
  const rest = minutes - hours * 60;
  return rest === 0 ? `${hours} h` : `${hours} h ${rest} min`;
}

/** Hours as "23.5 h" or "6 days 4 h" for tooltips. */
export function formatHours(seconds: number): string {
  const hours = seconds / 3600;
  if (hours < 48) {
    return `${hours.toFixed(1)} h`;
  }
  const days = Math.floor(hours / 24);
  return `${days} days ${Math.round(hours - days * 24)} h`;
}

/**
 * Unix milliseconds of an ISO 8601 time from the backend. It writes up to seven fractional digits, and not
 * every web view parses more than three, so the rest is cut off first.
 */
export function parseTime(iso: string): number {
  return Date.parse(iso.replace(/(\.\d{3})\d+/, "$1"));
}

const two = (n: number) => String(n).padStart(2, "0");

/** Local time of day, "13:41:07", or "13:52:04.118" with milliseconds. */
export function formatTime(time: Date | string | number, milliseconds = false): string {
  const date = toDate(time);
  if (Number.isNaN(date.getTime())) {
    return NONE;
  }
  const base = `${two(date.getHours())}:${two(date.getMinutes())}:${two(date.getSeconds())}`;
  return milliseconds ? `${base}.${String(date.getMilliseconds()).padStart(3, "0")}` : base;
}

/** Local hours and minutes, "13:41". */
export function formatClock(time: Date | string | number): string {
  const date = toDate(time);
  return `${two(date.getHours())}:${two(date.getMinutes())}`;
}

function toDate(time: Date | string | number): Date {
  return time instanceof Date ? time : new Date(typeof time === "string" ? parseTime(time) : time);
}

/** An amount of the base currency with four decimals, "0.0420". */
export function formatAmount(value: number): string {
  return value.toLocaleString("en-US", { minimumFractionDigits: 4, maximumFractionDigits: 4 });
}
