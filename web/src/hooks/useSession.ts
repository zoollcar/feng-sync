import { useCallback, useEffect, useRef, useState } from "react";
import { deviceName, peerHeaders, post, randomHex, reasonOf, request, Session } from "../api/client";
import { DeviceKind } from "../api/headers";
import { Routes } from "../api/routes";
import type { Hello, PairReply, Peer } from "../api/types";
import { reasonText, t } from "../i18n/strings";

export type Phase = "loading" | "pair" | "room" | "fatal";

type Fragment = Record<string, string>;

/**
 * The parameters a scanned code carries in the address fragment.
 *
 * A fragment and not a query string, because it never leaves the browser: it is not sent to the server, not
 * written into a proxy log, and not visible in a Referer. What it carries is a one-time pairing code, which is
 * exactly the thing that should not outlive the request that spends it.
 */
function readFragment(): Fragment {
  const out: Fragment = {};
  for (const pair of location.hash.replace(/^#/, "").split("&")) {
    const at = pair.indexOf("=");
    if (at > 0) out[pair.slice(0, at)] = decodeURIComponent(pair.slice(at + 1));
  }
  return out;
}

/**
 * Who this page is talking to, and whether it is allowed in.
 *
 * The order is the protocol's: `/hello` answers what computer this is and whether a code is armed, `/peers`
 * answers whether this browser already holds a credential — as a cookie, which is the only thing that can still
 * be here when the tab's storage is not — and only then does the page either enter the room or ask for a code.
 */
export function useSession() {
  const [phase, setPhase] = useState<Phase>("loading");
  const [hello, setHello] = useState<Hello | null>(null);
  const [peer, setPeer] = useState<Peer | null>(null);
  const [error, setError] = useState("");
  const [note, setNote] = useState("");
  const [prefilled, setPrefilled] = useState("");
  const booted = useRef(false);

  const enter = useCallback((row: Peer | null) => {
    if (row) {
      Session.setDeviceId(row.deviceId);
      setPeer(row);
    }
    setPhase("room");
  }, []);

  const pair = useCallback(
    async (raw: string) => {
      setError("");
      // A fresh id for a fresh pairing, which is what the one-time code buys: the computer writes a new row, and
      // the old one stops being this browser.
      Session.setDeviceId(randomHex(32));
      try {
        const answer = await post<PairReply>(Routes.pair, {
          code: raw.toUpperCase(),
          deviceId: Session.deviceId,
          deviceName: deviceName(),
          kind: DeviceKind,
        });
        if (answer.status !== 201 || !answer.body) {
          setError(answer.body ? reasonText(reasonOf(answer.body)) : `${t("errOther")}${answer.status}`);
          return;
        }
        Session.setToken(answer.body.token);
        enter(answer.body.peer);
      } catch {
        setError(t("errNet"));
      }
    },
    [enter],
  );

  useEffect(() => {
    if (booted.current) return;
    booted.current = true;
    Session.load();
    const fragment = readFragment();
    if (fragment.k) setPrefilled(fragment.k);

    void (async () => {
      let answer;
      try {
        answer = await request<Hello>(Routes.hello);
      } catch {
        setError(t("errNet"));
        setPhase("fatal");
        return;
      }

      const h = answer.body;
      if (answer.status !== 200 || !h) {
        setError(t("errNet"));
        setPhase("fatal");
        return;
      }
      setHello(h);
      document.documentElement.lang = document.documentElement.lang || "zh";
      if (h.v !== 1) {
        setError(t("errVersion"));
        setPhase("fatal");
        return;
      }
      // The code names which computer it was made for. A different answer means this address is not that one,
      // and pairing into it would hand a credential to the wrong machine.
      if (fragment.d && fragment.d !== h.deviceId) {
        setError(t("errWrong"));
        setPhase("fatal");
        return;
      }

      const peers = await request<Peer>(Routes.peers);
      if (peers.status === 200 && peers.body) {
        enter(peers.body);
        return;
      }
      if (Session.token) {
        enter(null);
        return;
      }

      setPhase("pair");
      const fresh = !fragment.e || Number(fragment.e) * 1000 > Date.now();
      if (h.pairingArmed && fragment.k && fresh) await pair(fragment.k);
      else if (fragment.k) setError(t("errExpired"));
    })();
  }, [enter, pair]);

  const rename = useCallback(async (wanted: string) => {
    if (!wanted.trim()) {
      setNote(t("needName"));
      return;
    }
    try {
      const answer = await post<Peer>(Routes.name, { deviceName: wanted });
      if (answer.status !== 200 || !answer.body) {
        setNote(reasonText(reasonOf(answer.body)));
        return;
      }
      // The name the computer stored, not the one that was typed — the same rule a received file follows.
      setPeer((current) => ({ ...(current ?? { deviceId: "", kind: "" }), ...answer.body! }));
      setNote(t("renamed"));
    } catch {
      setNote(t("errNet"));
    }
  }, []);

  // A tab that is going away says so, which is the one departure a browser can announce. `keepalive` lets the
  // request outlive the document; the lease remains the backstop for a browser that refuses to send it. The
  // credential is not given up here — putting the phone down is not the same as revoking it.
  useEffect(() => {
    const leave = () => {
      if (!Session.token) return;
      try {
        void fetch(Routes.leave, { method: "DELETE", headers: peerHeaders(), keepalive: true });
      } catch {
        /* a browser that will not send it has nothing to tell the computer anyway */
      }
    };
    window.addEventListener("pagehide", leave);
    return () => window.removeEventListener("pagehide", leave);
  }, []);

  return { phase, hello, peer, error, note, setNote, setError, prefilled, pair, rename };
}
