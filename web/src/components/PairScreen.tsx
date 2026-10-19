import { useState } from "react";
import { t } from "../i18n/strings";
import { IconMonitor } from "./icons";

/**
 * The screen a stranger sees.
 *
 * Pairing is something a person does, so it has to have somewhere to be done from: this page is the one route
 * reachable with no credential at all. The code is the only thing standing between this browser and the room, which
 * is why it is shown large and upper-cased — it is six characters typed off somebody else's screen.
 */
export function PairScreen({
  computer,
  host,
  initialCode,
  error,
  onPair,
}: {
  computer: string;
  host: string;
  initialCode: string;
  error: string;
  onPair: (code: string) => Promise<void>;
}) {
  const [code, setCode] = useState(initialCode.toUpperCase());
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    if (!code.trim()) return;
    setBusy(true);
    await onPair(code);
    setBusy(false);
  };

  return (
    <div className="flex min-h-dvh flex-col items-center justify-center px-5 py-10">
      <div className="w-full max-w-sm">
        <div className="mb-6 flex flex-col items-center gap-3 text-center">
          <span className="grid h-14 w-14 place-items-center rounded-2xl bg-surface text-accent shadow-card">
            <IconMonitor className="h-6 w-6" />
          </span>
          <div>
            <h1 className="text-lg font-semibold">{computer || t("title")}</h1>
            <p className="mt-0.5 text-xs text-muted tabular-nums">{host}</p>
          </div>
        </div>

        <div className="rounded-card border border-line bg-surface p-5 shadow-card">
          <h2 className="text-sm font-semibold">{t("pair")}</h2>
          <p className="mt-1 text-xs leading-relaxed text-muted">{t("hint")}</p>

          <input
            value={code}
            onChange={(event) => setCode(event.target.value.toUpperCase())}
            onKeyDown={(event) => {
              if (event.key === "Enter") void submit();
            }}
            autoComplete="off"
            autoCapitalize="characters"
            spellCheck={false}
            maxLength={12}
            inputMode="text"
            aria-label={t("pair")}
            placeholder="ABC123"
            className="mt-4 h-14 w-full rounded-xl border border-line bg-canvas text-center text-2xl font-semibold tracking-[0.35em] uppercase outline-none transition-colors focus:border-accent focus:bg-surface"
          />

          <button
            type="button"
            onClick={() => void submit()}
            disabled={busy || !code.trim()}
            className="mt-3 h-12 w-full rounded-xl bg-accent text-sm font-semibold text-white transition-colors hover:bg-accent-ink disabled:opacity-40"
          >
            {busy ? t("connecting") : t("go")}
          </button>

          {error && (
            <p className="mt-3 rounded-lg bg-danger/5 px-3 py-2 text-xs leading-relaxed text-danger" role="alert">
              {error}
            </p>
          )}
        </div>

        <p className="mt-4 text-center text-[11px] leading-relaxed text-muted">{t("where")}</p>
      </div>
    </div>
  );
}
