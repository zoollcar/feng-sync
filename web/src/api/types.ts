/** The device this page is talking to, as `/hello` answers. */
export type Hello = {
  v: number;
  deviceId: string;
  deviceName: string;
  kind: string;
  pairingArmed: boolean;
};

/** One row of the computer's trust list, as this page is allowed to see it — itself, and nobody else. */
export type Peer = {
  deviceId: string;
  deviceName: string;
  kind: string;
};

/** The answer to a successful pairing. */
export type PairReply = {
  token: string;
  v: number;
  peer: Peer;
};

export type GroupKind = "text" | "file" | "directory";

/**
 * One line of the room.
 *
 * `heldHere` is a statement about the computer answering, not about the room: it either has these bytes or it does
 * not, and only the second answer leaves this page with a 接收 to offer.
 */
export type Entry = {
  messageId: string;
  authorDeviceId: string;
  authorName: string;
  localSeq: number;
  kind: GroupKind;
  text?: string | null;
  fileName?: string | null;
  relativePath?: string | null;
  bytes: number;
  isDirectory: boolean;
  createdUtc: string;
  heldHere: boolean;
};

export type LogReply = { v: number; head: number; entries: Entry[] };

/**
 * One transfer the computer is moving.
 *
 * A page cannot measure this itself. An upload leaves its hands long before the bytes land, and a download is
 * handed to the browser's own downloader — so the side doing the writing is the only honest source, and `speed` is
 * measured there too rather than derived from how often this page happened to ask.
 */
export type Transfer = {
  id: string;
  direction: "in" | "out";
  deviceId: string;
  name: string;
  path: string;
  bytes: number;
  total: number;
  speed: number;
  started: number;
  session?: string | null;
  msg?: string | null;
  done: boolean;
  error?: string | null;
};

export type ProgressReply = { v: number; transfers: Transfer[] };

/** A refusal, in the shape every route answers with. */
export type ErrorReply = { error?: string; reason?: string };
