import { useRef, useState } from "react";
import type { Transfer } from "../api/types";
import type { UploadRow } from "../hooks/useUpload";
import { t } from "../i18n/strings";
import { size } from "../lib/format";
import { IconFile, IconFolder, IconSend } from "./icons";
import { ProgressBar } from "./ProgressBar";

type Props = {
  onSay: (text: string) => Promise<boolean>;
  onFiles: (files: File[], isFolder: boolean) => void;
  uploads: UploadRow[];
  transfers: Transfer[];
  onRetry: (key: string) => void;
};

/**
 * The bottom of the page: what to say, and what to send.
 *
 * The pickers are two hidden inputs rather than one with a mode, because a browser cannot be asked for a folder and
 * a file list at the same time — `webkitdirectory` changes what the same control returns, and the two answers are
 * not the same shape. The file list is copied before the input is cleared, since emptying it is the only way to
 * let the same file be picked twice and a browser may empty the live list out from under a transfer in progress.
 */
export function Composer({ onSay, onFiles, uploads, transfers, onRetry }: Props) {
  const [text, setText] = useState("");
  const [note, setNote] = useState("");
  const files = useRef<HTMLInputElement>(null);
  const folder = useRef<HTMLInputElement>(null);

  const say = async () => {
    if (!text.trim()) return;
    if (await onSay(text)) setText("");
  };

  return (
    <div className="border-t border-line bg-surface/95 px-3 pt-2 pb-[calc(0.5rem+env(safe-area-inset-bottom))] backdrop-blur">
      {uploads.length > 0 && (
        <ul className="mb-2 space-y-1.5">
          {uploads.map((row) => (
            <UploadLine key={row.key} row={row} transfers={transfers} onRetry={onRetry} />
          ))}
        </ul>
      )}

      {note && (
        <p className="mb-2 rounded-lg bg-danger/5 px-2.5 py-1.5 text-[11px] text-danger" role="alert">
          {note}
        </p>
      )}

      <div className="flex items-end gap-1.5">
        <button
          type="button"
          onClick={() => files.current?.click()}
          aria-label={t("pickFiles")}
          title={t("files")}
          className="grid h-11 w-11 shrink-0 place-items-center rounded-xl text-muted transition-colors hover:bg-canvas hover:text-ink"
        >
          <IconFile />
        </button>
        <button
          type="button"
          onClick={() => folder.current?.click()}
          aria-label={t("pickFolder")}
          title={t("folder")}
          className="grid h-11 w-11 shrink-0 place-items-center rounded-xl text-muted transition-colors hover:bg-canvas hover:text-ink"
        >
          <IconFolder />
        </button>

        <input
          value={text}
          onChange={(event) => setText(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === "Enter" && !event.shiftKey) {
              event.preventDefault();
              void say();
            }
          }}
          placeholder={t("talk")}
          maxLength={2000}
          className="h-11 min-w-0 flex-1 rounded-xl border border-line bg-canvas px-3.5 text-[15px] outline-none transition-colors placeholder:text-muted/70 focus:border-accent focus:bg-surface"
        />

        <button
          type="button"
          onClick={() => void say()}
          disabled={!text.trim()}
          aria-label={t("talk")}
          className="grid h-11 w-11 shrink-0 place-items-center rounded-xl bg-accent text-white transition-colors hover:bg-accent-ink disabled:opacity-40"
        >
          <IconSend />
        </button>
      </div>

      <input
        ref={files}
        type="file"
        multiple
        className="hidden"
        onChange={(event) => {
          const picked = Array.from(event.target.files ?? []);
          event.target.value = "";
          if (picked.length) onFiles(picked, false);
        }}
      />
      <input
        ref={folder}
        type="file"
        multiple
        className="hidden"
        {...{ webkitdirectory: "", directory: "" }}
        onChange={(event) => {
          const picked = Array.from(event.target.files ?? []);
          event.target.value = "";
          if (picked.length === 0) return;
          if (!(picked[0] as File & { webkitRelativePath?: string }).webkitRelativePath) {
            setNote(t("noFolder"));
            return;
          }
          setNote("");
          onFiles(picked, true);
        }}
      />
    </div>
  );
}

/**
 * One file of a batch.
 *
 * The bar follows the computer's board rather than the upload event, because the browser's own progress stops when
 * the last byte leaves for the socket and the file is only really there once it is on disk. The upload event is
 * still used for the first moments — before the computer has opened a row for it — so the bar is never blank.
 */
function UploadLine({
  row,
  transfers,
  onRetry,
}: {
  row: UploadRow;
  transfers: Transfer[];
  onRetry: (key: string) => void;
}) {
  const live = transfers.find((item) => item.session === row.session && item.path === row.rel);
  const bytes = live?.bytes ?? row.loaded;
  const total = live?.total ?? row.bytes;
  const done = row.state === "sent";

  return (
    <li className="rounded-xl bg-canvas px-2.5 py-2">
      <div className="flex items-center gap-2">
        <p className="min-w-0 flex-1 truncate text-xs font-medium">{row.name}</p>
        <span className="shrink-0 text-[11px] tabular-nums text-muted">
          {done ? t("done") : row.state === "failed" ? t("failed") : `${row.index}/${row.total}`}
        </span>
      </div>
      {!done && row.state !== "failed" && <ProgressBar className="mt-1.5" bytes={bytes} total={total} />}
      <div className="mt-1 flex items-center gap-2">
        <p className={`min-w-0 flex-1 truncate text-[11px] ${row.state === "failed" ? "text-danger" : "text-muted"}`}>
          {row.message || size(row.bytes)}
        </p>
        {row.state === "failed" && (
          <button
            type="button"
            onClick={() => onRetry(row.key)}
            className="shrink-0 rounded-full border border-line bg-surface px-2 py-0.5 text-[11px] font-semibold"
          >
            {t("retry")}
          </button>
        )}
      </div>
    </li>
  );
}
