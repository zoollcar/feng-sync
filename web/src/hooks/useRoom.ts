import { useCallback, useEffect, useRef, useState } from "react";
import { post, randomHex, reasonOf, request, Session } from "../api/client";
import { Routes } from "../api/routes";
import type { Entry, Invite, LogReply, Member } from "../api/types";
import { reasonText, t } from "../i18n/strings";

/** What pressing 邀请 on a device came to. */
export type InviteOutcome = { accepted: boolean; text: string };

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
  // Who else is in this room, who this computer could still ask, and the questions it is holding for this page.
  // All three arrive on the beat that already carries the conversation, because a phone has no second way to learn
  // any of them: it cannot hear the subnet, and nothing can dial it back.
  const [members, setMembers] = useState<Member[]>([]);
  const [invitable, setInvitable] = useState<Member[]>([]);
  const [invites, setInvites] = useState<Invite[]>([]);
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
    setMembers(answer.body.members ?? []);
    setInvitable(answer.body.invitable ?? []);
    setInvites(answer.body.pendingInvites ?? []);
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

  /**
   * Answers a question this computer is holding for this page, and settles it one of two ways.
   *
   * A browser cannot be dialled, so it is never asked directly: the computer keeps the inviter's request open and
   * tells this page about it on the beat. Agreeing joins the inviter's room and says whose it is; declining leaves
   * the invitation unopened. Either answer takes the question off screen, and the next beat brings it back only if
   * it somehow stayed open. The invite arrives from the caller, so nothing here reads the list this beat put there.
   */
  const answerInvite = useCallback(async (invite: Invite, accept: boolean): Promise<string> => {
    try {
      const answer = await post<{ decision: string }>(Routes.answerInvite(invite.inviteId), { accept });
      setInvites((current) => current.filter((item) => item.inviteId !== invite.inviteId));
      if (answer.status === 200 || answer.status === 201) {
        return accept ? t("joinedGroup", invite.fromName || invite.fromDeviceId) : t("declinedInvite");
      }
      return reasonText(reasonOf(answer.body));
    } catch {
      return t("errNet");
    }
  }, []);

  /**
   * Asks the computer to invite a device this page can see but cannot reach.
   *
   * The request is held until somebody at that machine answers, which is why this can take a minute: the page has
   * no route to the device and never will, so the only honest place to wait is here.
   */
  const invite = useCallback(async (deviceId: string): Promise<InviteOutcome> => {
    try {
      const answer = await post<{ decision: string }>(Routes.invites, {
        // The id is this page's own, in the shape the protocol asks for. The rest of the body describes this
        // device, which the computer replaces with the identity it has already authenticated — a caller may ask
        // on its own behalf and never as somebody else.
        inviteId: randomHex(16),
        deviceId: Session.deviceId,
        deviceName: "",
        kind: "phone",
        port: 0,
        grantToken: "",
        targetDeviceId: deviceId,
      });
      if (answer.status !== 200 && answer.status !== 201) {
        return { accepted: false, text: reasonText(reasonOf(answer.body)) };
      }

      const decision = answer.body?.decision ?? "";
      const said =
        decision === "accepted"
          ? t("inviteJoined")
          : decision === "declined"
            ? t("inviteDeclined")
            : decision === "withdrawn"
              ? t("inviteWithdrawn")
              : t("inviteExpired");
      return { accepted: decision === "accepted", text: said };
    } catch {
      return { accepted: false, text: t("errNet") };
    }
  }, []);

  return {
    entries, error, ready, append, receive, isHeld, refresh: poll,
    members, invitable, invites, answerInvite, invite,
  };
}
