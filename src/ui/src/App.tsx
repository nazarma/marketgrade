import { useEffect, useState, type ReactNode } from "react";
import {
  getBackendStatus,
  getEngineStatus,
  type BackendStatus,
  type ConnectionEvent,
  type EngineStatus,
  type ExchangeStatus,
  type RecordingStatus,
} from "@/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { formatBytes, formatSinceLastMessage, recordingBadge, stateBadge } from "@/exchangeState";
import { RecordingSettingsForm } from "@/RecordingSettingsForm";

const POLL_INTERVAL_MS = 1000;
const RECENT_EVENTS = 10;

type ConnectionState =
  | { kind: "starting" }
  | { kind: "connected"; backend: BackendStatus; engine: EngineStatus | null }
  | { kind: "error"; message: string };

type Screen = "status" | "settings";

export default function App() {
  const [state, setState] = useState<ConnectionState>({ kind: "starting" });
  const [attempt, setAttempt] = useState(0);
  const [screen, setScreen] = useState<Screen>("status");

  useEffect(() => {
    let current = true;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const fail = (error: unknown) =>
      current &&
      setState({
        kind: "error",
        message: error instanceof Error ? error.message : String(error),
      });

    // Polls the engine once per second until the backend stops answering.
    const poll = (backend: BackendStatus) =>
      getEngineStatus().then((engine) => {
        if (current) {
          setState({ kind: "connected", backend, engine });
          timer = setTimeout(() => void poll(backend), POLL_INTERVAL_MS);
        }
      }, fail);

    getBackendStatus().then((backend) => {
      if (current) {
        setState({ kind: "connected", backend, engine: null });
        void poll(backend);
      }
    }, fail);

    return () => {
      current = false;
      clearTimeout(timer);
    };
  }, [attempt]);

  const retry = () => {
    setState({ kind: "starting" });
    setAttempt((n) => n + 1);
  };

  const engine = state.kind === "connected" ? state.engine : null;

  return (
    <main className="mx-auto flex min-h-screen w-full max-w-4xl flex-col gap-6 p-8">
      <header className="flex flex-wrap items-baseline justify-between gap-4">
        <div className="flex items-baseline gap-6">
          <h1 className="text-2xl font-semibold tracking-tight">Marketgrade</h1>
          <nav className="flex gap-4 text-sm">
            <NavLink screen="status" current={screen} onSelect={setScreen}>
              Status
            </NavLink>
            <NavLink screen="settings" current={screen} onSelect={setScreen}>
              Settings
            </NavLink>
          </nav>
        </div>
        <BackendLine state={state} />
      </header>

      {state.kind === "error" && (
        <Card>
          <CardHeader>
            <CardTitle>Backend not reachable</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col items-start gap-4">
            <p className="font-mono text-sm break-all text-muted-foreground">{state.message}</p>
            <Button onClick={retry}>Retry</Button>
          </CardContent>
        </Card>
      )}

      {screen === "settings" && state.kind === "connected" && <RecordingSettingsForm />}

      {screen === "status" && (
        <>
          <Card>
            <CardHeader>
              <CardTitle>Exchanges</CardTitle>
            </CardHeader>
            <CardContent className="flex flex-col gap-4">
              {engine ? (
                <ExchangeTable exchanges={engine.status.exchanges} />
              ) : (
                <p className="text-muted-foreground">Waiting for the engine...</p>
              )}
              {engine?.status.recording && <RecordingLine recording={engine.status.recording} />}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Recent events</CardTitle>
            </CardHeader>
            <CardContent>
              <RecentEvents events={engine?.events ?? []} />
            </CardContent>
          </Card>
        </>
      )}
    </main>
  );
}

function NavLink({
  screen,
  current,
  onSelect,
  children,
}: {
  screen: Screen;
  current: Screen;
  onSelect: (screen: Screen) => void;
  children: ReactNode;
}) {
  const active = screen === current;
  return (
    <button
      type="button"
      onClick={() => onSelect(screen)}
      aria-current={active ? "page" : undefined}
      className={
        active ? "font-medium text-foreground" : "text-muted-foreground hover:text-foreground"
      }
    >
      {children}
    </button>
  );
}

function RecordingLine({ recording }: { recording: RecordingStatus }) {
  const badge = recordingBadge(recording.state);
  return (
    <div
      className="flex flex-wrap items-center gap-x-4 gap-y-2 border-t pt-4 text-sm"
      title={recording.lastError ?? recording.folder}
    >
      <span className="font-medium">Recording</span>
      <Badge className={badge.className}>{badge.label}</Badge>
      <span className="text-muted-foreground">
        Today{" "}
        <span className="font-mono text-foreground tabular-nums">
          {formatBytes(recording.todayBytes)}
        </span>
      </span>
      <span className="text-muted-foreground">
        Total{" "}
        <span className="font-mono text-foreground tabular-nums">
          {formatBytes(recording.totalBytes)}
        </span>
      </span>
      <span className="text-muted-foreground">
        Free{" "}
        <span className="font-mono text-foreground tabular-nums">
          {formatBytes(recording.freeBytes)}
        </span>
      </span>
      {recording.droppedMessages > 0 && (
        <span className="text-warning">{recording.droppedMessages} not recorded</span>
      )}
    </div>
  );
}

function BackendLine({ state }: { state: ConnectionState }) {
  return (
    <div className="flex items-center gap-2 text-sm text-muted-foreground">
      {state.kind === "connected" && (
        <span className="font-mono">
          Backend {state.backend.health.version} · port {state.backend.port}
        </span>
      )}
      {state.kind === "starting" && <Badge className="bg-warning/15 text-warning">Starting</Badge>}
      {state.kind === "connected" && <Badge className="bg-buy/15 text-buy">Online</Badge>}
      {state.kind === "error" && <Badge className="bg-sell/15 text-sell">Offline</Badge>}
    </div>
  );
}

function ExchangeTable({ exchanges }: { exchanges: ExchangeStatus[] }) {
  return (
    <table className="w-full text-sm">
      <thead className="text-left text-muted-foreground">
        <tr>
          <th className="pb-2 font-normal">Exchange</th>
          <th className="pb-2 font-normal">Pair</th>
          <th className="pb-2 font-normal">State</th>
          <th className="pb-2 text-right font-normal">Messages/s</th>
          <th className="pb-2 text-right font-normal">Last message</th>
        </tr>
      </thead>
      <tbody>
        {exchanges.map((exchange) => {
          const badge = stateBadge(exchange.state);
          return (
            <tr key={exchange.id} className="border-t" title={exchange.lastError ?? undefined}>
              <td className="py-2 font-medium">{exchange.name}</td>
              <td className="py-2 font-mono text-muted-foreground">{exchange.pair}</td>
              <td className="py-2">
                <Badge className={badge.className}>{badge.label}</Badge>
              </td>
              <td className="py-2 text-right font-mono tabular-nums">
                {exchange.messagesPerSecond.toFixed(1)}
              </td>
              <td className="py-2 text-right font-mono tabular-nums">
                {formatSinceLastMessage(exchange.msSinceLastMessage)}
              </td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

function RecentEvents({ events }: { events: ConnectionEvent[] }) {
  if (events.length === 0) {
    return <p className="text-muted-foreground">No events yet.</p>;
  }
  const recent = events.slice(-RECENT_EVENTS).reverse();
  return (
    <ul className="flex flex-col gap-1 text-sm">
      {recent.map((e, i) => (
        <li key={`${e.timeUtc}-${e.exchange}-${i}`} className="flex gap-3">
          <span className="font-mono text-muted-foreground tabular-nums">
            {new Date(e.timeUtc).toLocaleTimeString()}
          </span>
          <span className="w-20 shrink-0 font-medium">{e.exchange}</span>
          <span>
            {e.event}
            {e.detail && <span className="text-muted-foreground"> ({e.detail})</span>}
          </span>
        </li>
      ))}
    </ul>
  );
}
