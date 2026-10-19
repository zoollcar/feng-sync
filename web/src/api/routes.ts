/**
 * Every address this page asks for, as a literal.
 *
 * They are written out in full rather than composed from a prefix because the built bundle is checked for them:
 * the listener's own tests assert that the page names the routes it is allowed to name, and a template literal
 * assembled at runtime would still be correct and still fail that check. One string per route, no concatenation.
 */
export const Routes = {
  hello: "/api/v1/hello",
  pair: "/api/v1/pair",
  peers: "/api/v1/peers",
  name: "/api/v1/name",
  messages: "/api/v1/messages",
  inbox: "/api/v1/inbox",
  progress: "/api/v1/progress",
  leave: "/api/v1/presence/mine",
  log: (since: number) => `/api/v1/log?since=${since}`,
  attachment: (id: string) => `/api/v1/attachments/${encodeURIComponent(id)}`,
  archive: (id: string) => `/api/v1/attachments/${encodeURIComponent(id)}/zip`,
  receive: (id: string) => `/api/v1/attachments/${encodeURIComponent(id)}/receive`,
} as const;
