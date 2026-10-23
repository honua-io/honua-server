# Public Inspector cancellation observation

The server owns this browser runner and independent database/wire observer.
The canonical MCP certification adapter consumes these files from an immutable,
authenticated server source. The public installed Inspector launcher, connection
configuration, argument editor, Execute Tool and Cancel buttons perform the
client interaction. No private Inspector client methods, protocol-history
injection or generated cancellation frame is used.

Inspector's Cancel button aborts its local tool promise. This needs a distinct
proof contract from the official SDK test, which keeps its local promise open
and never uses an AbortSignal. A positive Inspector proof requires all of:

- The exact authored query reaches a real PostgreSQL relation-lock blocker.
- The actual client sends a session-bound, typed `notifications/cancelled` while
  its original upstream request is still open. Its envelope must declare JSON-RPC
  `2.0` and omit an own `id`, including `null`. The proxy never shields aborts.
- The actual UI initialization binds that session and negotiated protocol
  version; the consumer compares it with its reported client protocol identity.
- The public Cancel button disappears and Execute Tool is enabled again,
  independently observing that the local UI settled. The dropped-notification
  control must also intercept the real correlated notification before refusing
  proof; an inert button or absent wire activity cannot satisfy that control.
- Exactly one correlated server response contains protocol code -32800 and
  typed code `cancelled`, without a result.
- An independent connection sees the blocked query and its relation locks cease
  while the blocker is still held. Acknowledgement or local UI completion alone
  is insufficient.
- The authored rows remain unchanged and another actual Inspector query returns
  data that the consuming adapter checks against the authored fixture.
- The blocker, UI process, browser and wire observer are cleaned up independently.

Observation is bounded to 40 seconds; database operations keep the existing
observer's three-second child budget. Frames are limited to four MiB, with
independent bounded compressed-body decoding. No redirect is followed. Private
connection configuration is mode 0600 and removed at cleanup; launcher output
may contain a local token and is bounded and discarded.

`inspector-browser-cancellation.integration.mjs` runs the real public Inspector
2.10.1 UI and real PostGIS against an authored control peer. That peer cancels
its database command only after the actual notification. A second execution
drops that notification: the UI may settle locally, but PostgreSQL remains
blocked after more than three server monitor periods, so the proof must fail.
These controls never qualify a Honua candidate, AWS target or another Inspector
version. The existing workflow retains separate exact-source counts and marks
them `candidateQualified: false`.

Seven authored malformed-envelope controls require refusal before forwarding or
recording cancellation, with the original request pending until owned cleanup.
They cover missing and incorrectly typed/versioned `jsonrpc` values and an own
`id` containing null, a number or a string.

The canonical adapter still needs candidate-bound execution of this helper.
Inspector initial ping and unknown-tool error addressability remain separate
open obligations; this slice does not promote those checks or change the
release denominator, client versions or governed facets.
