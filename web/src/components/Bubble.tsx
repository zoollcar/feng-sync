import { useState } from "react";
import { Routes } from "../api/routes";
import type { Entry, Transfer } from "../api/types";
import { rateText, t } from "../i18n/strings";
import { clock, size } from "../lib/format";
import { IconAlert, IconDown, IconFile, IconFolder, IconUp } from "./icons";
import { ProgressBar } from "./ProgressBar";

type Props = {
  entry: Entry;
  mine: boolean;
  held: boolean;
  transfer?: Transfer;
  onReceive: (id: string) => Promise<string>;
  onError: (message: string) => void;
};

/**
 * One line of the room.
 *
 * A file bubble has two shapes and the difference is a statement about the computer answering, not about the room:
 * it either holds these bytes — and then this page gets a link the browser is allowed to press — or it does not,
 * and the only honest offer is to ask it to become a holder first. That is why 接收 is a request here rather than
 * a download, and why the download that follows is an ordinary link carrying nothing but a cookie.
 */
export function Bubble({ entry, mine, held, transfer, onReceive, onError }: Props) {
  const [busy, setBusy] = useState(false);
  const isText = entry.kind === "text";

  const ask = async () => {
    setBusy(true);
    const failure = await onReceive(entry.messageId);
    setBusy(false);
    if (failure) onError(failure);
  };

  return (
    <li className={`flex ${mine ? "justify-end" : "justify-start"}`}>
      <div className="max-w-[86%] min-w-0">
        {!mine && entry.authorName && (
          <p className="mb-1 pl-1 text-[11px] font-medium text-muted">{entry.authorName}</p>
        )}

        <div
          className={
            mine
              ? "rounded-2xl rounded-br-md bg-accent-soft px-3.5 py-2.5"
              : "rounded-2xl rounded-bl-md border border-line bg-surface px-3.5 py-2.5 shadow-card"
          }
        >
          {isText ? (
            <p className="text-[15px] leading-relaxed break-words whitespace-pre-wrap">{entry.text}</p>
          ) : (
            <>
              <div className="flex items-start gap-2.5">
                <span className="grid h-9 w-9 shrink-0 place-items-center rounded-xl bg-canvas text-muted">
                  {entry.isDirectory ? <IconFolder className="h-4 w-4" /> : <IconFile className="h-4 w-4" />}
                </span>
                <div className="min-w-0">
                  <p className="truncate text-sm font-medium">{entry.fileName}</p>
                  <p className="text-[11px] tabular-nums text-muted">
                    {size(entry.bytes)} · {entry.isDirectory ? t("isdir") : t("isfile")}
                  </p>
                </div>
              </div>

              {transfer && !transfer.done && <LiveTransfer transfer={transfer} />}

              <div className="mt-2.5 flex items-center gap-2">
                {held ? (
                  <a
                    href={entry.isDirectory ? Routes.archive(entry.messageId) : Routes.attachment(entry.messageId)}
                    download={entry.isDirectory ? `${entry.fileName ?? "folder"}.zip` : (entry.fileName ?? "file")}
                    className="inline-flex items-center gap-1.5 rounded-full bg-accent px-3 py-1.5 text-xs font-semibold text-white transition-colors hover:bg-accent-ink"
                  >
                    <IconDown className="h-3.5 w-3.5" />
                    {t("take")}
                  </a>
                ) : (
                  <button
                    type="button"
                    onClick={() => void ask()}
                    disabled={busy}
                    className="inline-flex items-center gap-1.5 rounded-full border border-line bg-canvas px-3 py-1.5 text-xs font-semibold text-ink transition-colors hover:bg-line disabled:opacity-60"
                  >
                    <IconUp className="h-3.5 w-3.5" />
                    {busy ? t("waiting") : t("fetch")}
                  </button>
                )}
                {held && <span className="text-[11px] text-ok">{t("got")}</span>}
              </div>
            </>
          )}
        </div>
      </div>
    </li>
  );
}

/** A transfer already in flight for this entry, drawn where the person is looking rather than only in the panel. */
function LiveTransfer({ transfer }: { transfer: Transfer }) {
  const remaining = transfer.speed > 0 && transfer.total > 0 ? (transfer.total - transfer.bytes) / transfer.speed : 0;
  return (
    <div className="mt-2.5 rounded-xl bg-canvas px-2.5 py-2">
      <ProgressBar bytes={transfer.bytes} total={transfer.total} />
      <p className="mt-1 flex items-center gap-1.5 text-[11px] tabular-nums text-muted">
        {transfer.direction === "in" ? <IconDown className="h-3 w-3" /> : <IconUp className="h-3 w-3" />}
        <span>{transfer.direction === "in" ? t("receiving") : t("sending")}</span>
        {transfer.error ? (
          <span className="text-danger">· {t("stopped")}</span>
        ) : transfer.speed > 0 ? (
          <>
            <span>·</span>
            <span className="font-medium text-ink/70">{rateText(transfer.speed)}</span>
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
  );
}

/** The room, with the two states a room can be in: nothing said yet, and the conversation. */
export function MessageList({
  entries,
  me,
  heldOf,
  transferOf,
  onReceive,
  onError,
}: {
  entries: Entry[];
  me: string;
  heldOf: (entry: Entry) => boolean;
  transferOf: (entry: Entry) => Transfer | undefined;
  onReceive: (id: string) => Promise<string>;
  onError: (message: string) => void;
}) {
  if (entries.length === 0) {
    return (
      <div className="flex flex-1 flex-col items-center justify-center gap-2 px-8 py-16 text-center">
        <span className="grid h-12 w-12 place-items-center rounded-2xl bg-surface text-muted shadow-card">
          <IconAlert className="h-5 w-5" />
        </span>
        <p className="text-sm text-muted">{t("empty")}</p>
      </div>
    );
  }

  return (
    <ul className="flex flex-col gap-3 px-3 py-3">
      {entries.map((entry) => (
        <Bubble
          key={entry.messageId}
          entry={entry}
          mine={entry.authorDeviceId === me}
          held={heldOf(entry)}
          transfer={transferOf(entry)}
          onReceive={onReceive}
          onError={onError}
        />
      ))}
    </ul>
  );
}
