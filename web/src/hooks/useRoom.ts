import { useCallback, useEffect, useRef, useState } from "react";
import { post, reasonOf, request } from "../api/client";
import { Routes } from "../api/routes";
import type { Entry, LogReply } from "../api/types";
import { reasonText, t } from "../i18n/strings";

/**
 * This machine's copy of the room, read the way a browser can read it.
 *
 * A page has no listener, so it never hears anything: it asks the computer it paired with for whatever arrived
 * past the number it last saw, three seconds at a time. That is the same shape the computer's own window uses for
 * the members it polls, and the same reason — nobody owns the room, so everybody catches up by asking.
 *
 * Identity is the message id and nothing else. Two members told the same sentence reach this page by different
 * routes, and a page that numbered them itself would show its own words back twice.
 */
export function useRoom(active: boolean) {
  const [entries, setEntries] = useState<Entry[]>([]);
  const [error, setError] = useState("");
  const [held, setHeld] = useState<Record<string, true>>({});
  const [ready, setReady] = useState(false);
  const since = useRef(0);
  const seen = useRef(new Set<string>());

  const merge = useCallback((incoming: Entry[]) => {
    const fresh = incoming.filter((entry) => !seen.current.has(entry.messageId));
    if (fresh.length === 0) return;
    for (const entry of fresh) seen.current.add(entry.messageId);
    setEntries((current) => [...current, ...fresh]);
  }, []);

  const poll = useCallback(async () => {
    let answer;
    try {
      answer = await request<LogReply>(Routes.log(since.current));
    } catch {
      // A beat that cannot reach the computer is a pause, not a loss: the next one asks again from the same
      // number, because the watermark only moves when a reply actually arrived.
      return;
    }

    if (answer.status !== 200) {
      setError(
        answer.status === 401 || answer.status === 403 ? t("pair_required") : reasonText(reasonOf(answer.body)),
      );
      return;
    }

    setError("");
    if (!answer.body) return;
    since.current = answer.body.head || 0;
    merge(answer.body.entries ?? []);
    setReady(true);
  }, [merge]);

  useEffect(() => {
    if (!active) return;
    let stopped = false;
    void poll();
    const timer = window.setInterval(() => {
      if (!stopped) void poll();
    }, 3000);
    return () => {
      stopped = true;
      window.clearInterval(timer);
    };
  }, [active, poll]);

  /** Puts a row on screen before the next beat brings it back — the sentence this page just said. */
  const append = useCallback((entry: Entry) => merge([entry]), [merge]);

  /**
   * Asks the computer to take one entry it does not hold, so this page gets a link it is allowed to press.
   *
   * A browser cannot dial anybody, so the only member that could hand these bytes over is the one already
   * answering these requests. This is the same 接收 the computer's own window presses, on the phone's behalf.
   */
  const receive = useCallback(async (id: string): Promise<string> => {
    try {
      const answer = await post<Entry>(Routes.receive(id), null);
      if (answer.status !== 200 || !answer.body) return reasonText(reasonOf(answer.body));
      setHeld((current) => ({ ...current, [id]: true }));
      setEntries((current) =>
        current.map((entry) => (entry.messageId === id ? { ...entry, heldHere: true } : entry)),
      );
      return "";
    } catch {
      return t("errNet");
    }
  }, []);

  const isHeld = useCallback((entry: Entry) => entry.heldHere || held[entry.messageId] === true, [held]);

  return { entries, error, ready, append, receive, isHeld, refresh: poll };
}
