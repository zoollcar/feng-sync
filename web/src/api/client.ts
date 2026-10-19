import { DeviceKind, Headers } from "./headers";
import type { ErrorReply } from "./types";

/**
 * Where the credential lives, and the two rules that put it there.
 *
 * `sessionStorage` and nowhere else. Never the address — a photograph of a QR code already is one — and never a
 * store that outlives the tab, which was handed a one-time code and should not keep what it bought. What has to
 * outlive the tab is the device's *name*, and that is not kept here at all: it is read back from the row the
 * computer keeps for this device, which is the one copy a rename writes to.
 */
const TokenKey = "fs_token";
const DeviceKey = "fs_device";

export const Session = {
  token: "",
  deviceId: "",
  load(): void {
    Session.token = sessionStorage.getItem(TokenKey) ?? "";
    Session.deviceId = sessionStorage.getItem(DeviceKey) ?? "";
  },
  setToken(token: string): void {
    Session.token = token;
    sessionStorage.setItem(TokenKey, token);
  },
  setDeviceId(deviceId: string): void {
    Session.deviceId = deviceId;
    sessionStorage.setItem(DeviceKey, deviceId);
  },
  clear(): void {
    Session.token = "";
    Session.deviceId = "";
    sessionStorage.removeItem(TokenKey);
    sessionStorage.removeItem(DeviceKey);
  },
};

/**
 * A random id, from the one random source a page served over plain HTTP has.
 *
 * `crypto.getRandomValues` exists in an insecure context; `crypto.subtle` does not, which is why nothing here signs
 * anything. The device id is 32 bytes and a message id 16, matching what the computer expects to see.
 */
export function randomHex(bytes: number): string {
  const buffer = new Uint8Array(bytes);
  crypto.getRandomValues(buffer);
  let out = "";
  for (const byte of buffer) out += (byte < 16 ? "0" : "") + byte.toString(16);
  return out;
}

/** What the computer is told this device is. A header has to be ASCII, so the word is never a person's. */
export function deviceName(): string {
  const platform = (navigator as Navigator & { userAgentData?: { platform?: string } }).userAgentData?.platform;
  return platform && /^[ -~]+$/.test(platform) ? platform : "phone";
}

function auth(): Record<string, string> {
  return Session.token ? { Authorization: `FS-LAN ${Session.deviceId}:${Session.token}` } : {};
}

/** The credential plus what this device calls itself, for the routes that admit a peer by name. */
export function peerHeaders(): Record<string, string> {
  return { ...auth(), [Headers.deviceName]: deviceName(), [Headers.deviceKind]: DeviceKind };
}

export type Reply<T> = { status: number; body: T | null };

function parse(text: string): unknown {
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return null;
  }
}

/** One request, in the shape every caller here wants: the status, and whatever body came with it. */
export async function request<T>(path: string, init: RequestInit = {}): Promise<Reply<T>> {
  const response = await fetch(path, { ...init, headers: { ...auth(), ...(init.headers as Record<string, string> | undefined) } });
  const text = await response.text();
  return { status: response.status, body: parse(text) as T | null };
}

export async function post<T>(path: string, body: unknown): Promise<Reply<T>> {
  return request<T>(path, {
    method: "POST",
    headers: body === null ? {} : { "Content-Type": "application/json" },
    body: body === null ? null : JSON.stringify(body),
  });
}

/** The reason code a refusal carried, which is what the language table is keyed on. */
export function reasonOf(body: unknown): string {
  return (body as ErrorReply | null)?.reason ?? "";
}
