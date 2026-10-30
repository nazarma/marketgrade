import { useState, type MouseEvent } from "react";
import { isTauri } from "@tauri-apps/api/core";
import { resolveResource } from "@tauri-apps/api/path";
import { openPath, openUrl } from "@tauri-apps/plugin-opener";
import { SettingsPanel } from "@/screens/settings/fields";

export const REPOSITORY_URL = "https://github.com/nazarma/marketgrade";
/** Shipped next to the app (bundle resources); the opener plugin may open only this file and the repository. */
export const NOTICES_RESOURCE = "THIRD-PARTY-NOTICES.txt";
const NOTICES_URL = `${REPOSITORY_URL}/blob/main/THIRD-PARTY-NOTICES.md`;

/** Version, source, licence and disclaimer. No update checks: the app never contacts anything but the exchanges and time servers. */
export function AboutSection() {
  const [error, setError] = useState<string | null>(null);

  // Inside the app a link opens in the system browser (or the notices in the system's text viewer); in a
  // normal browser the link works as it is.
  const open = (target: () => Promise<void>) => (e: MouseEvent) => {
    if (!isTauri()) {
      return;
    }
    e.preventDefault();
    setError(null);
    target().catch((reason: unknown) =>
      setError(reason instanceof Error ? reason.message : String(reason)),
    );
  };

  return (
    <SettingsPanel title="About" description="Marketgrade, free and open source.">
      <dl className="m-0 grid grid-cols-[180px_1fr] gap-x-4 gap-y-2 text-sm">
        <dt className="text-muted-foreground">Version</dt>
        <dd className="mono m-0">{__APP_VERSION__}</dd>
        <dt className="text-muted-foreground">Source code</dt>
        <dd className="m-0">
          <a
            href={REPOSITORY_URL}
            target="_blank"
            rel="noreferrer"
            onClick={open(() => openUrl(REPOSITORY_URL))}
          >
            github.com/nazarma/marketgrade
          </a>
        </dd>
        <dt className="text-muted-foreground">Licence</dt>
        <dd className="m-0">MIT</dd>
        <dt className="text-muted-foreground">Third-party software</dt>
        <dd className="m-0">
          <a
            href={NOTICES_URL}
            target="_blank"
            rel="noreferrer"
            onClick={open(async () => openPath(await resolveResource(NOTICES_RESOURCE)))}
          >
            Third-party notices
          </a>
        </dd>
      </dl>
      {error && <p className="bad m-0 text-sm">{error}</p>}
      <p className="lbl m-0 text-[13px]">
        Marketgrade is not affiliated with Kraken, Coinbase or Binance. It shows public market data
        for personal, non-commercial use. Nothing in it is financial advice.
      </p>
    </SettingsPanel>
  );
}
