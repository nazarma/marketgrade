import { LogoMark } from "@/app/icons";

/** Full screen, while there is no backend to show anything from. */
export function BackendUnreachable({ message, onRetry }: { message: string; onRetry: () => void }) {
  return (
    <main className="flex min-h-screen items-center justify-center p-8">
      <section className="panel flex w-full max-w-[560px] flex-col gap-4 p-6" role="alert">
        <div className="flex items-center gap-2.5">
          <LogoMark />
          <h1 className="m-0 text-lg font-semibold">Backend not reachable</h1>
        </div>
        <p className="m-0 text-sm text-muted-foreground">
          The part of Marketgrade that connects to the exchanges is not answering. Retry starts it
          again if it stopped.
        </p>
        <p className="mono m-0 rounded-lg bg-raised p-3 text-[13px] break-all">{message}</p>
        <div>
          <button type="button" className="btn-primary" onClick={onRetry}>
            Retry
          </button>
        </div>
      </section>
    </main>
  );
}

export function Starting() {
  return (
    <main className="flex min-h-screen items-center justify-center" aria-busy="true">
      <div className="flex items-center gap-2.5 text-sm text-muted-foreground">
        <LogoMark />
        Starting...
      </div>
    </main>
  );
}
