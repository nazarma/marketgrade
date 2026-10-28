import { useEffect, useState } from "react";
import { Navigate, Route, Routes } from "react-router";
import { getBackendInfo, getBackendStatus, getEngineStatus, resetBackendInfo } from "@/api";
import { AppShell } from "@/app/AppShell";
import { BackendUnreachable, Starting } from "@/app/BackendUnreachable";
import { startLive, stopLive } from "@/live/hub";
import { seeded, useLiveStore } from "@/live/liveStore";
import { FlowScreen } from "@/screens/FlowScreen";
import { HealthScreen } from "@/screens/HealthScreen";
import { OverviewScreen } from "@/screens/OverviewScreen";
import { SettingsScreen } from "@/screens/SettingsScreen";

type Gate = { kind: "starting" } | { kind: "ready" } | { kind: "error"; message: string };

const message = (error: unknown) => (error instanceof Error ? error.message : String(error));

export default function App() {
  const [gate, setGate] = useState<Gate>({ kind: "starting" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let current = true;
    const fail = (error: unknown) => current && setGate({ kind: "error", message: message(error) });

    // Health first, so a dead backend shows its error; then the first state, then live pushes.
    (async () => {
      await getBackendStatus();
      const snapshot = await getEngineStatus();
      const info = await getBackendInfo();
      if (!current) {
        return;
      }
      useLiveStore.setState((state) => seeded(state, snapshot));
      await startLive(info, (error) =>
        fail(error ?? new Error("The connection to the backend was lost.")),
      );
      if (current) {
        setGate({ kind: "ready" });
      }
    })().catch(fail);

    return () => {
      current = false;
      void stopLive();
    };
  }, [attempt]);

  if (gate.kind === "error") {
    return (
      <BackendUnreachable
        message={gate.message}
        onRetry={() => {
          // The desktop shell starts a new backend if the old one is gone.
          resetBackendInfo();
          setGate({ kind: "starting" });
          setAttempt((n) => n + 1);
        }}
      />
    );
  }
  if (gate.kind === "starting") {
    return <Starting />;
  }
  return (
    <Routes>
      <Route element={<AppShell />}>
        <Route path="overview" element={<OverviewScreen />} />
        <Route path="flow" element={<FlowScreen />} />
        <Route path="health" element={<HealthScreen />} />
        <Route path="settings" element={<SettingsScreen />} />
        <Route path="*" element={<Navigate to="/overview" replace />} />
      </Route>
    </Routes>
  );
}
