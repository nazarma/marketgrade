import { useEffect, useState } from "react";
import { getBackendStatus, type BackendStatus } from "@/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

type ConnectionState =
  | { kind: "starting" }
  | { kind: "connected"; status: BackendStatus }
  | { kind: "error"; message: string };

export default function App() {
  const [state, setState] = useState<ConnectionState>({ kind: "starting" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let current = true;
    getBackendStatus().then(
      (status) => current && setState({ kind: "connected", status }),
      (error: unknown) =>
        current &&
        setState({
          kind: "error",
          message: error instanceof Error ? error.message : String(error),
        }),
    );
    return () => {
      current = false;
    };
  }, [attempt]);

  const retry = () => {
    setState({ kind: "starting" });
    setAttempt((n) => n + 1);
  };

  return (
    <main className="flex min-h-screen flex-col items-center justify-center gap-8 p-8">
      <h1 className="text-3xl font-semibold tracking-tight">Marketgrade</h1>
      <Card className="w-full max-w-md">
        {state.kind === "starting" && (
          <CardHeader>
            <CardTitle>Starting backend...</CardTitle>
            <CardDescription>
              <Badge className="bg-warning/15 text-warning">Starting</Badge>
            </CardDescription>
          </CardHeader>
        )}

        {state.kind === "connected" && (
          <>
            <CardHeader>
              <CardTitle>Backend connected</CardTitle>
              <CardDescription>
                <Badge className="bg-buy/15 text-buy">Online</Badge>
              </CardDescription>
            </CardHeader>
            <CardContent>
              <dl className="grid grid-cols-[auto_1fr] gap-x-6 gap-y-1 text-sm">
                <dt className="text-muted-foreground">Version</dt>
                <dd className="font-mono">{state.status.health.version}</dd>
                <dt className="text-muted-foreground">Port</dt>
                <dd className="font-mono">{state.status.port}</dd>
              </dl>
            </CardContent>
          </>
        )}

        {state.kind === "error" && (
          <>
            <CardHeader>
              <CardTitle>Backend not reachable</CardTitle>
              <CardDescription>
                <Badge className="bg-sell/15 text-sell">Offline</Badge>
              </CardDescription>
            </CardHeader>
            <CardContent className="flex flex-col items-start gap-4">
              <p className="font-mono text-sm break-all text-muted-foreground">{state.message}</p>
              <Button onClick={retry}>Retry</Button>
            </CardContent>
          </>
        )}
      </Card>
    </main>
  );
}
