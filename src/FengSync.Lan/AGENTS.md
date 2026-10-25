# FengSync.Lan — invitation rules

The root `AGENTS.md` owns the room's shape (replica-not-store, message-id identity, the poll as propagation, the pairing credential). This file owns what was added when 发现 became 可邀请: the roster, the invitation, and the network of gateways that lets a browser be invited by a machine it cannot talk to.

## The roster is the only place a device is classified

- `LanDeviceRoster.Merge(selfId, peers, present, discovered, relayed, credentialed, invited)` (`LanDeviceRoster.cs`) is a pure function over snapshots and the single answer to "what kind of device is this". The window's two strips are both drawn from one `Merge` call, so they cannot disagree; a third strip would need a third classification and must not be added.
- Precedence is one-directional: an agreement (a `lan-peers.json` row) outranks a broadcast, and a broadcast outranks another member's report. Nothing is ever downgraded by weaker evidence.
- `credentialed` is an input rather than something the merge could infer, because `lan-peers.json` records who may talk to *this* machine while `lan-received-tokens.dat` records who *this* machine may call. A `windows` row with no received token is `LanDeviceState.Incomplete` — the legacy half-duplex pairing — and its button reads 补全, which is the same repair an invitation performs.
- A UDP sighting must never enter presence. `LanTransferWindow.OnPeerSeen` records into `_found` and re-merges; it does not call `Presence.Seen`. That is the whole reason a discovered computer cannot leak into the member strip, and it holds structurally because member rows are seeded only from `peers`.

## Two reachability answers, never one

- `LanRosterEntry.IsDialable` means "this machine can open a socket to it", computed from the port range alone. `LanRosterEntry.CanInvite` means "an invitation can be delivered to it", and is settled by a second pass over the finished table — a member row's gateway may appear later in the peer list, and a relayed row has no address of its own to test.
- Do not conflate them. `LanInviteRow.ActionVisibility` follows `CanInvite`; every dialler in the room (`PollOnceAsync`, `EndpointFor`, `Where(entry)`) still reads `IsDialable`, because a phone whose row is askable is a device nothing may connect to.
- The button is collapsed rather than disabled: a row nobody can reach is not a question that is merely not-yet-askable. The judgement is only as fresh as the last merge, and that race is answered where a person can act on it — `InviteAsync` refuses with `Lan.InviteNeedsGateway` when the gateway is gone.
- A device reported by a member is drawn as something to invite, never added quietly. Only the invitable half travels on the wire, since who is already in that room is not news to this machine.

## The invitation wire contract

- `POST /api/v1/invites` is the application's **only credential-free entry point**, and only when `TargetDeviceId` is empty or names this machine. A forwarded invitation (`TargetDeviceId` naming somebody else) requires the caller's pairing credential, and the caller's authenticated contact **overwrites** what the body claims about itself — a member may invite on another's behalf and may never claim to be another device.
- `POST /api/v1/invites/{id}/answer` is always credentialed and always answered by the device the invitation names: `AnswerInviteAsync` refuses when `answerer` is non-null and `!invite.IsFor(answerer.DeviceId)`. `answerer: null` is the window answering its own question, which the board already filtered by target.
- Limits are `LanProtocol` constants, one reader each: 90 s lifetime, 3 openings per source per 60 s, 8 live pending invitations (`Conflict` when full, counted on `Pending` rows only so an answered one frees its slot). Per-source limiting is a speed bump — a device id is a claim, not a proof — and the global cap is the wall.
- `InviteId` is idempotent per opener: the same source re-asking gets the existing decision; a different source naming the same id is `InviteConflict`.
- Client timeout (100 s) must stay longer than the server's hold (90 s + a short commit grace) so a hang cannot outlive the window in which it can be answered.
- Nothing is written to disk before a person accepts. The pre-issued `GrantToken` lives in memory on the answering side; the peer row, the issued token and the stored grant are all made inside `AcceptInviteAsync`, which *is* the commit. Every non-accepted outcome — decline, expiry, withdraw, an exception — releases the grant the inviter minted, and only when this call minted it.
- The kind of an accepting machine is settled as `windows` regardless of what the invitation claimed, because a device that answers this question runs a listener; believing "phone" here would file a computer under a kind the room never dials.
- 408 answers `invite_expired` and 409 answers `invite_conflict`, but `LanApi.FromStatusCode` maps 409 to `Conflict` since the status is shared — the reason token is what names an invitation conflict. `Lan.InviteTooOld` is the honest reading of a 404 from this route: the other build predates invitations, so use the pairing code.

## The room's reply carries the tables, and no route is added

- `LanLogReply` gained `Members`, `PendingInvites` and `Invitable` as optional trailing fields. There is deliberately **no** `/members` route: one more enumerable surface is one more permission boundary the screen does not draw, and the poll already reaches every member.
- `LanMemberWire` has no address field and must never gain one. `LanInviteWire` carries an address and a port, because that is how a machine that cannot be dialled learns whom to ask about a file.
- `PendingInvites` is filtered by `auth.Contact.DeviceId`, so a member never sees a question put to somebody else.
- Protocol version stays **1**: `web/src/hooks/useSession.ts` hard-refuses any other number. Every field added here is optional and `System.Text.Json` ignores unknown ones on the way back, which is what makes an old peer a wording problem rather than a failure.

## Belonging after an invitation

- A phone that accepts keeps talking to the computer it paired with; its page origin never changes. Bytes travel through that machine — its upload lands there and is pulled from there, an announcement reaches it through that machine's `/attachments/{id}/receive`. Direct reachability is not attempted and is not required for this to work.
- An accepted relayed invitation leaves one row on the inviter: `Kind=phone`, port 0, `GatewayDeviceId` naming the machine that served the page, written by `LanPeerClient.InviteAsync`. It is a participation record, never a dial target.
- Unsolvable by design, and accepted: two members inviting the same phone leave two independent memberships. The room has no leader, so there is nothing to deduplicate against.
