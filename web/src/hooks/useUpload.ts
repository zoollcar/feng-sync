import { useRef, useState } from "react";
import { peerHeaders, randomHex, reasonOf, Session } from "../api/client";
import { Headers } from "../api/headers";
import { Routes } from "../api/routes";
import { reasonText, t } from "../i18n/strings";

export type UploadState = "waiting" | "sending" | "sent" | "failed";

export type UploadRow = {
  key: string;
  name: string;
  /** The path inside what was sent, which is what the computer files the transfer under. */
  rel: string;
  session: string;
  index: number;
  total: number;
  bytes: number;
  loaded: number;
  state: UploadState;
  message: string;
};

/** The most rows kept on screen. A folder of three hundred files is one sentence, not three hundred rows. */
const MaxRows = 12;

type Saved = { name?: string; savedAs?: string; renamed?: boolean; reason?: string };

/**
 * Sends files the way the wire expects them: one request per file, the next starting only when the previous one
 * has an answer.
 *
 * That serial shape is the point. A folder is many writes carrying one session and one message id, so a batch that
 * broke halfway leaves exactly the files the person already watched leave — not a room that announced a tree it
 * only half delivered. The rate shown beside a row comes from the computer's board rather than from the upload
 * event, because the browser's own progress stops at the last byte it handed to the socket, which is not the same
 * moment as the last byte on disk.
 */
export function useUpload(onSent: () => void) {
  const [rows, setRows] = useState<UploadRow[]>([]);
  const files = useRef(new Map<string, File>());
  const batch = useRef(0);

  const patch = (key: string, changes: Partial<UploadRow>) =>
    setRows((current) => current.map((row) => (row.key === key ? { ...row, ...changes } : row)));

  const uploadOne = (row: UploadRow, file: File, isFolder: boolean, messageId: string) =>
    new Promise<void>((resolve) => {
      const xhr = new XMLHttpRequest();
      xhr.open("POST", Routes.inbox);
      const headers = peerHeaders();
      headers[Headers.rel] = encodeURIComponent(row.rel);
      headers[Headers.total] = String(file.size);
      headers[Headers.mtime] = String(file.lastModified || 0);
      headers[Headers.session] = row.session;
      headers[Headers.message] = messageId;
      if (isFolder) headers[Headers.kind] = "directory";
      for (const [name, value] of Object.entries(headers)) xhr.setRequestHeader(name, value);

      patch(row.key, { state: "sending", loaded: 0, message: "" });
      xhr.upload.onprogress = (event) => {
        if (event.lengthComputable) patch(row.key, { loaded: event.loaded });
      };

      const settle = (state: UploadState, message: string) => {
        patch(row.key, { state, message, loaded: file.size });
        resolve();
      };

      xhr.onload = () => {
        let body: Saved | null = null;
        try {
          body = JSON.parse(xhr.responseText) as Saved;
        } catch {
          body = null;
        }
        if (xhr.status >= 200 && xhr.status < 300) {
          // The name the directory took, not the one that was sent: a file stored as `a (1).txt` is shown under
          // the name that is really there, which is the only name a later 接收 can open.
          const shown = body?.savedAs ?? file.name;
          settle("sent", `${t(body?.renamed ? "sentAs" : "sent")} ${shown}`);
          return;
        }
        settle("failed", `${t("failed")}：${body ? reasonText(reasonOf(body)) : xhr.status}`);
      };
      xhr.onerror = () => settle("failed", `${t("failed")}：${t("errNet")}`);
      xhr.send(file);
    });

  const run = async (batchRows: UploadRow[], isFolder: boolean, messageId: string, id: number) => {
    for (const row of batchRows) {
      if (batch.current !== id) return;
      const file = files.current.get(row.key);
      if (file) await uploadOne(row, file, isFolder, messageId);
    }
    onSent();
  };

  const send = (picked: File[], isFolder: boolean) => {
    if (picked.length === 0) return;
    // One session and one message id for the whole batch: a tree is one sentence in the room, so every write of
    // it carries the same id and the same 文件夹 kind.
    const session = `${Session.deviceId.slice(0, 12)}-${Date.now().toString(36)}`;
    const messageId = randomHex(16);
    const id = (batch.current += 1);

    const created = picked.map((file, index) => {
      const rel =
        isFolder && (file as File & { webkitRelativePath?: string }).webkitRelativePath
          ? (file as File & { webkitRelativePath: string }).webkitRelativePath
          : file.name;
      const key = `${session}:${index}:${file.name}`;
      files.current.set(key, file);
      return {
        key,
        name: file.name,
        rel,
        session,
        index: index + 1,
        total: picked.length,
        bytes: file.size,
        loaded: 0,
        state: "waiting" as UploadState,
        message: `${t("waiting")} ${index + 1}/${picked.length}`,
      };
    });

    setRows((current) => [...current, ...created].slice(-MaxRows));
    void run(created, isFolder, messageId, id);
  };

  const retry = (key: string) => {
    const row = rows.find((item) => item.key === key);
    const file = files.current.get(key);
    if (!row || !file) return;
    const messageId = randomHex(16);
    void uploadOne(row, file, false, messageId).then(onSent);
  };

  return {
    rows,
    send,
    retry,
    busy: rows.some((row) => row.state === "waiting" || row.state === "sending"),
  };
}
