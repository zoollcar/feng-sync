import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { post, randomHex, reasonOf, Session } from "../api/client";
import { Routes } from "../api/routes";
import type { Entry, Invite, Transfer } from "../api/types";
import { useRoom } from "../hooks/useRoom";
import { useTransfers } from "../hooks/useTransfers";
import { useUpload } from "../hooks/useUpload";
import type { useSession } from "../hooks/useSession";
import { reasonText, t } from "../i18n/strings";
import { MessageList } from "./Bubble";
import { Composer } from "./Composer";
import { IconMonitor } from "./icons";
import { TransferPanel } from "./TransferPanel";

/**
 * The room, as a phone sees it: the conversation, the transfers moving through it, and a way to add to either.
 *
 * Everything here reads from the computer. A page has no listener, so it never hears anything — the log is polled,
 * the transfer board is polled, and the only writes are the ones a person asked for.
 */
export function RoomScreen({ session }: { session: ReturnType<typeof useSession> }) {
  const me = session.peer?.deviceId ?? Session.deviceId;
  const room = useRoom(true);
  const transfers = useTransfers(true);
  const uploads = useUpload(() => void room.refresh());
  const [auto, setAuto] = useState(false);
  const [openSettings, setOpenSettings] = useState(false);
  const [name, setName] = useState(session.peer?.deviceName ?? "");
  const [inviteNote, setInviteNote] = useState("");
  const [asking, setAsking] = useState("");
  const asked = useRef(new Set<string>());

  // Auto-receive is this page's own promise, kept on this side: the computer is asked to take an entry it does not
  // hold, and the page then shows the link that follows. Off by default, because bytes arriving in somebody's
  // folder unasked is the one effect a phone cannot undo.
  useEffect(() => {
    if (!auto) return;
    for (const entry of room.entries) {
      if (entry.kind === "text" || entry.heldHere || entry.authorDeviceId === me) continue;
      if (asked.current.has(entry.messageId)) continue;
      asked.current.add(entry.messageId);
      void room.receive(entry.messageId).then((failure) => {
        if (failure) session.setNote(failure);
      });
    }
  }, [auto, me, room.entries, room.receive, session]);

  const say = useCallback(
    async (text: string) => {
      // The id is minted here, before the request leaves, because the same sentence reaches every copy of this room
      // by its own route. A page that let each machine number them would read its own words back twice.
      const messageId = randomHex(16);
      try {
        const answer = await post<Entry>(Routes.messages, { text, messageId });
        if (answer.status !== 201 || !answer.body) {
          session.setNote(reasonText(reasonOf(answer.body)));
          return false;
        }
        room.append(answer.body);
        return true;
      } catch {
        session.setNote(t("errNet"));
        return false;
      }
    },
    [room, session],
  );

  const transferOf = useCallback(
    (entry: Entry): Transfer | undefined => {
      const mine = transfers.filter((item) => item.msg === entry.messageId);
      return mine.find((item) => !item.done) ?? mine[0];
    },
    [transfers],
  );

  const members = useMemo(() => {
    const seen = new Map<string, string>();
    if (session.peer?.deviceName) seen.set(me, session.peer.deviceName);
    // The room's own table, which the computer sends with every beat. It is what makes a member that has not
    // spoken yet visible here: a page that only read the authors of the conversation could not name a device that
    // has joined and stayed quiet.
    for (const member of room.members) {
      if (member.deviceName) seen.set(member.deviceId, member.deviceName);
    }
    for (const entry of room.entries) {
      if (entry.authorDeviceId && entry.authorName) seen.set(entry.authorDeviceId, entry.authorName);
    }
    return [...seen.values()];
  }, [me, room.entries, room.members, session.peer]);

  // Answering settles the row and says which way it came out — whose room the phone joined, or that it declined —
  // so the note carries that sentence instead of going blank on a success, the way the outgoing invite already
  // reports its own outcome through `askDevice`.
  const answerInvite = useCallback(
    async (invite: Invite, accept: boolean) => {
      setInviteNote(await room.answerInvite(invite, accept));
    },
    [room],
  );

  const askDevice = useCallback(
    async (deviceId: string) => {
      setAsking(deviceId);
      setInviteNote("");
      const outcome = await room.invite(deviceId);
      setAsking("");
      setInviteNote(outcome.text);
    },
    [room],
  );

  const computer = session.hello?.deviceName ?? t("title");

  return (
    <div className="flex h-dvh flex-col bg-canvas">
      <header className="sticky top-0 z-20 border-b border-line bg-surface/90 backdrop-blur">
        <div className="mx-auto flex w-full max-w-3xl items-center gap-3 px-4 py-2.5">
          <span className="grid h-9 w-9 shrink-0 place-items-center rounded-xl bg-accent-soft text-accent-ink">
            <IconMonitor className="h-4 w-4" />
          </span>
          <div className="min-w-0 flex-1">
            <p className="truncate text-sm font-semibold">{computer}</p>
            <p className="truncate text-[11px] tabular-nums text-muted">{location.host}</p>
          </div>
          <span className="rounded-full bg-ok/10 px-2 py-0.5 text-[11px] font-semibold text-ok">{t("room")}</span>
          <button
            type="button"
            onClick={() => setOpenSettings((value) => !value)}
            aria-expanded={openSettings}
            className="h-8 rounded-full border border-line px-2.5 text-[11px] font-semibold text-muted transition-colors hover:bg-canvas hover:text-ink"
          >
            {t("my")}
          </button>
        </div>

        {openSettings && (
          <div className="mx-auto w-full max-w-3xl border-t border-line px-4 py-3">
            <label className="text-[11px] font-medium text-muted" htmlFor="my-name">
              {t("my")}
            </label>
            <div className="mt-1 flex gap-2">
              <input
                id="my-name"
                value={name}
                onChange={(event) => setName(event.target.value)}
                maxLength={60}
                className="h-10 min-w-0 flex-1 rounded-xl border border-line bg-canvas px-3 text-sm outline-none focus:border-accent focus:bg-surface"
              />
              <button
                type="button"
                onClick={() => void session.rename(name)}
                className="h-10 shrink-0 rounded-xl border border-line bg-canvas px-3 text-xs font-semibold"
              >
                {t("rename")}
              </button>
            </div>

            <label className="mt-3 flex items-start gap-2 text-xs leading-relaxed">
              <input
                type="checkbox"
                checked={auto}
                onChange={(event) => setAuto(event.target.checked)}
                className="mt-0.5 h-4 w-4 accent-[#2563eb]"
              />
              <span>{t("auto")}</span>
            </label>
            <p className="mt-1 text-[11px] leading-relaxed text-muted">{t("where")}</p>
            {session.note && <p className="mt-2 text-[11px] font-medium text-ok">{session.note}</p>}
          </div>
        )}
      </header>

      <main className="min-h-0 flex-1 overflow-y-auto">
        <div className="mx-auto flex min-h-full w-full max-w-3xl flex-col">
          {room.error && (
            <p className="mx-3 mt-3 rounded-xl bg-danger/5 px-3 py-2 text-xs text-danger" role="alert">
              {room.error}
            </p>
          )}

          {/* A question somebody asked this phone. It is the one thing on this screen that is waiting for a
              person, so it comes before the conversation rather than after it. */}
          {room.invites.length > 0 && (
            <div className="mx-3 mt-3 rounded-xl border border-line bg-accent-soft px-3 py-2.5">
              <p className="text-sm font-semibold text-ink">
                {t("inviteAsk", room.invites[0].fromName || room.invites[0].fromDeviceId)}
              </p>
              <p className="mt-1 text-[11px] leading-relaxed text-muted">{t("inviteAskHint")}</p>
              <div className="mt-2 flex gap-2">
                <button
                  type="button"
                  onClick={() => void answerInvite(room.invites[0], true)}
                  className="h-8 rounded-lg bg-accent px-3 text-xs font-semibold text-white"
                >
                  {t("accept")}
                </button>
                <button
                  type="button"
                  onClick={() => void answerInvite(room.invites[0], false)}
                  className="h-8 rounded-lg border border-line bg-surface px-3 text-xs font-semibold text-muted"
                >
                  {t("decline")}
                </button>
              </div>
            </div>
          )}

          <TransferPanel transfers={transfers} />

          {members.length > 1 && (
            <p className="mx-4 mt-3 truncate text-[11px] text-muted">
              {t("spoke")}
              {members.join("、")}
            </p>
          )}

          {/* Devices this computer can hear and has not agreed with. The page shows them because the computer
              does, and pressing 邀请 is the page asking the computer to ask them — a browser has no route of its
              own, and never will. */}
          {room.invitable.length > 0 && (
            <div className="mx-4 mt-2">
              <p className="text-[11px] font-medium text-muted" title={t("invitableHint")}>
                {t("invitable")}
              </p>
              <ul className="mt-1 flex flex-wrap gap-1.5">
                {room.invitable.map((device) => (
                  <li
                    key={device.deviceId}
                    className="flex items-center gap-1.5 rounded-full border border-line bg-surface py-1 pl-2.5 pr-1"
                  >
                    <span className="max-w-[9rem] truncate text-[11px] text-ink">
                      {device.deviceName || device.deviceId}
                    </span>
                    <button
                      type="button"
                      disabled={asking !== ""}
                      onClick={() => void askDevice(device.deviceId)}
                      className="h-6 rounded-full bg-accent-soft px-2 text-[11px] font-semibold text-accent-ink disabled:opacity-50"
                    >
                      {asking === device.deviceId ? t("waiting") : t("invite")}
                    </button>
                  </li>
                ))}
              </ul>
            </div>
          )}
          {inviteNote && <p className="mx-4 mt-1 text-[11px] text-muted">{inviteNote}</p>}

          <div className="flex flex-1 flex-col">
            <MessageList
              entries={room.entries}
              me={me}
              heldOf={room.isHeld}
              transferOf={transferOf}
              onReceive={room.receive}
              onError={session.setNote}
            />
          </div>
        </div>
      </main>

      <Composer
        onSay={say}
        onFiles={uploads.send}
        uploads={uploads.rows}
        transfers={transfers}
        onRetry={uploads.retry}
      />
    </div>
  );
}
