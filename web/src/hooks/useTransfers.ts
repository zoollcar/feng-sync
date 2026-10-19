import { useEffect, useState } from "react";
import { request } from "../api/client";
import { Routes } from "../api/routes";
import type { ProgressReply, Transfer } from "../api/types";

/** How often a page asks while something is moving, and while nothing is. */
const BusyInterval = 600;
const IdleInterval = 2000;

/**
 * What the computer is moving right now.
 *
 * The page cannot measure any of this itself: an upload leaves its hands long before the bytes land, and a
 * download is handed to the browser's own downloader. So the side doing the writing is the only honest source, and
 * the rate comes from there too — a speed derived from how often this page asked would be the speed of the poll.
 *
 * The interval follows the work: twice a second while a bar is moving, and every two seconds once everything has
 * settled, so a phone left on the table is not asking for nothing.
 */
export function useTransfers(active: boolean) {
  const [transfers, setTransfers] = useState<Transfer[]>([]);

  useEffect(() => {
    if (!active) return;
    let stopped = false;
    let timer = 0;

    const tick = async () => {
      let next = IdleInterval;
      try {
        const answer = await request<ProgressReply>(Routes.progress);
        if (!stopped && answer.status === 200 && answer.body) {
          setTransfers(answer.body.transfers ?? []);
          if (answer.body.transfers?.some((row) => !row.done)) next = BusyInterval;
        }
      } catch {
        // A missed poll is a missed sample, not a failed transfer: the next one reports the same bytes.
      }
      if (!stopped) timer = window.setTimeout(() => void tick(), next);
    };

    void tick();
    return () => {
      stopped = true;
      window.clearTimeout(timer);
    };
  }, [active]);

  return transfers;
}
