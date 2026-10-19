import { useEffect, useState } from "react";
import type { Transfer } from "../api/types";
import { rateText, t } from "../i18n/strings";
import { clock, percent } from "../lib/format";
import { IconChevron, IconDown, IconUp } from "./icons";
import { ProgressBar } from "./ProgressBar";

/** One line of the panel: a single file, or a whole folder folded into one row. */
export type TransferRow = {
  key: string;
  direction: "in" | "out";
  name: string;
  bytes: number;
  total: number;
  speed: number;
  done: boolean;
  error?: string | null;
  count: number;
};

/**
 * Folds the board into rows a person can read.
 *
 * A folder leaves as one file per request, so the board holds one entry per file sharing one session. Three
 * hundred rows for one folder would bury the transfer that matters, so a session becomes one row carrying a
 * count — and its total is only summed when every file in it declared one, because a partial total drawn as a
 * fraction is a number that goes backwards when the next file starts.
 */
export function groupTransfers(list: Transfer[]): TransferRow[] {
  const rows: TransferRow[] = [];
  const bySession = new Map<string, TransferRow>();

  for (const item of list) {
    const key = item.session ? `s:${item.session}` : item.id;
    const existing = bySession.get(key);
    if (!existing) {
      const row: TransferRow = {
        key,
        direction: item.direction,
        name: item.name,
        bytes: item.bytes,
        total: item.total,
        speed: item.speed,
        done: item.done,
        error: item.error,
        count: 1,
      };
      bySession.set(key, row);
      rows.push(row);
      continue;
    }
    existing.bytes += item.bytes;
    existing.total = existing.total > 0 && item.total > 0 ? existing.total + item.total : 0;
    existing.speed += item.speed;
    existing.done = existing.done && item.done;
    existing.error = existing.error ?? item.error;
    existing.count += 1;
  }

  return rows;
}

/**
 * What this machine is moving, in both directions.
 *
 * The one surface that answers the question the page could not answer before: how far has it got, and how fast.
 * It collapses itself four seconds after the last row finishes rather than staying as a permanent panel, because a
 * room is a conversation and a finished transfer is not news.
 */
export function TransferPanel({ transfers }: { transfers: Transfer[] }) {
  const rows = groupTransfers(transfers);
  const running = rows.some((row) => !row.done);
  const [open, setOpen] = useState(true);

  useEffect(() => {
    if (running) {
      setOpen(true);
      return;
    }
    const timer = window.setTimeout(() => setOpen(false), 4000);
    return () => window.clearTimeout(timer);
  }, [running]);

  if (rows.length === 0) return null;

  return (
    <section className="mx-3 mt-3 overflow-hidden rounded-card border border-line bg-surface shadow-card">
      <button
        type="button"
        onClick={() => setOpen((value) => !value)}
        className="flex w-full items-center gap-2 px-4 py-2.5 text-left"
        aria-expanded={open}
      >
        <span className="text-xs font-semibold tracking-wide text-muted uppercase">{t("transfers")}</span>
        <span className="rounded-full bg-accent-soft px-2 py-0.5 text-[11px] font-semibold text-accent-ink tabular-nums">
          {rows.length}
        </span>
        <span className="ml-auto flex items-center gap-1 text-xs text-muted">
          {running ? (
            <span className="flex items-center gap-1.5">
              <span className="relative flex h-1.5 w-1.5">
                <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-accent opacity-70" />
                <span className="relative inline-flex h-1.5 w-1.5 rounded-full bg-accent" />
              </span>
              {t("live")}
            </span>
          ) : (
            t("done")
          )}
          <IconChevron className={`h-4 w-4 transition-transform ${open ? "" : "-rotate-90"}`} />
        </span>
      </button>

      {open && (
        <ul className="divide-y divide-line border-t border-line">
          {rows.map((row) => (
            <TransferLine key={row.key} row={row} />
          ))}
        </ul>
      )}
    </section>
  );
}

function TransferLine({ row }: { row: TransferRow }) {
  const incoming = row.direction === "in";
  const fraction = row.total > 0 ? percent(row.bytes, row.total) : 0;
  const remaining = row.speed > 0 && row.total > 0 ? (row.total - row.bytes) / row.speed : 0;
  const label = row.name || (incoming ? t("receiving") : t("sending"));
  const title = row.count > 1 ? t("folderOf", row.count) : label;

  return (
    <li className="flex items-start gap-3 px-4 py-3">
      <span
        className={`mt-0.5 grid h-8 w-8 shrink-0 place-items-center rounded-xl ${
          incoming ? "bg-accent-soft text-accent-ink" : "bg-ok/10 text-ok"
        }`}
      >
        {incoming ? <IconDown className="h-4 w-4" /> : <IconUp className="h-4 w-4" />}
      </span>

      <div className="min-w-0 flex-1">
        <div className="flex items-baseline gap-2">
          <p className="min-w-0 flex-1 truncate text-sm font-medium" title={title}>
            {row.count > 1 ? t("folderOf", row.count) : label}
          </p>
          <span className="shrink-0 text-xs font-semibold tabular-nums text-muted">
            {row.done ? t("done") : row.total > 0 ? `${Math.round(fraction)}%` : ""}
          </span>
        </div>

        <ProgressBar className="mt-1.5" bytes={row.bytes} total={row.total} tone={row.done ? "ok" : "accent"} />

        <p className="mt-1 flex items-center gap-1.5 text-[11px] tabular-nums text-muted">
          <span>{incoming ? t("receiving") : t("sending")}</span>
          {row.error ? (
            <span className="text-danger">· {t("stopped")}</span>
          ) : row.speed > 0 ? (
            <>
              <span>·</span>
              <span className="font-medium text-ink/70">{rateText(row.speed)}</span>
              {remaining > 0 && (
                <>
                  <span>·</span>
                  <span>{t("left", clock(remaining))}</span>
                </>
              )}
            </>
          ) : null}
        </p>
      </div>
    </li>
  );
}
