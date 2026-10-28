import {
  HttpTransportType,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from "@microsoft/signalr";
import type { BackendInfo, ConnectionEvent, EngineStatus, FlowFrame } from "@/api";
import { useLiveStore, withEvent, withFlow, withStatus, withoutFlow } from "@/live/liveStore";

/** Reconnect attempts after a dropped connection; after the last one the connection counts as lost. */
const RETRY_DELAYS_MS = [0, 1_000, 2_000, 4_000, 8_000, 15_000];

let connection: HubConnection | null = null;
let flowWanted = false;

/**
 * Connects to the backend's live hub: WebSockets only, without negotiation, with the token in the query
 * string (browsers cannot set headers on a WebSocket). Pushes go into the live store. <paramref name="onLost"/>
 * runs once the connection is gone for good.
 */
export async function startLive(info: BackendInfo, onLost: (error?: Error) => void): Promise<void> {
  await stopLive();
  const hub = new HubConnectionBuilder()
    .withUrl(`http://127.0.0.1:${info.port}/hubs/live`, {
      transport: HttpTransportType.WebSockets,
      skipNegotiation: true,
      accessTokenFactory: () => info.token,
    })
    .withAutomaticReconnect(RETRY_DELAYS_MS)
    .configureLogging(LogLevel.Warning)
    .build();

  const set = useLiveStore.setState;
  hub.on("status", (status: EngineStatus["status"]) => set((state) => withStatus(state, status)));
  hub.on("event", (event: ConnectionEvent) => set((state) => withEvent(state, event)));
  hub.on("flow", (frame: FlowFrame) => set((state) => withFlow(state, frame)));
  hub.onreconnecting(() => set({ hub: "reconnecting" }));
  hub.onreconnected(() => {
    set({ hub: "live" });
    // A new connection is a new subscriber as far as the backend knows.
    if (flowWanted) {
      void hub.invoke("SubscribeFlow");
    }
  });
  hub.onclose((error) => {
    if (connection === hub) {
      connection = null;
      set({ hub: "reconnecting" });
      onLost(error);
    }
  });

  connection = hub;
  set({ hub: "connecting" });
  await hub.start();
  set({ hub: "live" });
  if (flowWanted) {
    await hub.invoke("SubscribeFlow");
  }
}

export async function stopLive(): Promise<void> {
  const hub = connection;
  connection = null;
  await hub?.stop();
}

/** Starts or stops the flow pushes; kept across reconnects. */
export function setFlowSubscribed(subscribed: boolean): void {
  if (flowWanted === subscribed) {
    return;
  }
  flowWanted = subscribed;
  if (!subscribed) {
    useLiveStore.setState(withoutFlow);
  }
  if (connection?.state === HubConnectionState.Connected) {
    void connection.invoke(subscribed ? "SubscribeFlow" : "UnsubscribeFlow").catch(() => undefined);
  }
}
