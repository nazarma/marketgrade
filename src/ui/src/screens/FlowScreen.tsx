import { ScreenHeader } from "@/app/ScreenHeader";

export function FlowScreen() {
  return (
    <ScreenHeader
      title="Live order flow"
      description="BTC/USD. Sell orders fall from the top, buy orders rise from the bottom, trades flash at the current price."
    />
  );
}
