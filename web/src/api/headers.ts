/**
 * The protocol's own header names.
 *
 * Two of them are not here because they are standard: `Authorization` and `Content-Type`. The rest belong to this
 * wire and are spelled once, so an upload cannot end up naming a header the listener does not read.
 */
export const Headers = {
  deviceName: "X-FS-Device-Name",
  deviceKind: "X-FS-Device-Kind",
  rel: "X-FS-Rel",
  total: "X-FS-Total",
  mtime: "X-FS-Mtime",
  session: "X-FS-Session",
  message: "X-FS-Msg",
  kind: "X-FS-Kind",
} as const;

/** What this page tells the computer it is. */
export const DeviceKind = "phone";
