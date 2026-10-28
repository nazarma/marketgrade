/** What the screens show about each exchange, by its engine id. */
export interface ExchangeInfo {
  name: string;
  /** CSS colour; every chart pairs it with {@link dash} or a label, never colour alone. */
  color: string;
  /** Line dash for charts, empty for solid. */
  dash: number[];
  /** The exchange card's footer. */
  checkedBy: string;
  /** The longer line on the Data health screen. */
  check: string;
  /** What a sequence gap is called on this exchange. */
  sequenceGap: string;
}

export const EXCHANGES: Record<number, ExchangeInfo> = {
  1: {
    name: "Kraken",
    color: "#A78BFA",
    dash: [],
    checkedBy: "Checked by checksum, top 10 levels",
    check: "Checksum of the top 10 levels after every update",
    sequenceGap: "Sequence gap",
  },
  2: {
    name: "Coinbase",
    color: "#5CC8FF",
    dash: [2, 4],
    checkedBy: "Checked by sequence number",
    check: "Sequence number must increase by exactly 1",
    sequenceGap: "Sequence gap",
  },
  3: {
    name: "Binance",
    color: "#F5B841",
    dash: [8, 5],
    checkedBy: "Checked by update ID range",
    check: "Each update must continue the previous update ID range",
    sequenceGap: "Update ID gap",
  },
};

const FALLBACK: ExchangeInfo = {
  name: "",
  color: "#8B95A5",
  dash: [],
  checkedBy: "",
  check: "",
  sequenceGap: "Sequence gap",
};

export function exchangeInfo(id: number): ExchangeInfo {
  return EXCHANGES[id] ?? FALLBACK;
}

/** The gap log's "Found by" column. */
export function gapKindLabel(kind: string, exchangeId: number): string {
  switch (kind) {
    case "Checksum":
      return "Checksum mismatch";
    case "Sequence":
      return exchangeInfo(exchangeId).sequenceGap;
    case "Parse":
      return "Unreadable message";
    case "ConnectionLost":
      return "Connection lost";
    default:
      return kind;
  }
}
