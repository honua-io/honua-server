# ADR-0061: Map the clarification envelope onto MCP-native elicitation

Status: Accepted
Date: 2026-07-07

## Context

Honua's grounding surface asks the operator to disambiguate a natural-language
goal through a **clarification envelope**: `honua_ground_candidates` /
`honua_clarify_intent` return a `ClarificationRequest` (intentId, reason codes,
and a list of typed questions — single-select, multi-select, free-text, or a
confirmation/approval question), and the caller replays the answers through
`honua_clarify_intent` (ADR-0027). This is a Honua-proprietary shape carried in
the tool result's `structuredContent.clarification`.

MCP 2025-06-18 standardized **elicitation**: a client that declares the
`elicitation` capability at `initialize` can render a server-supplied
`requestedSchema` (a flat object of primitive properties) as a native input
form. Issue #1954 listed "map Honua's clarification envelope onto MCP-native
elicitation" among its acceptance criteria but never delivered it; #2484 tracks
the deferred work.

Two constraints shape the design:

- **ADR-0028 / the deterministic-server model.** The model/agent runs
  client-side; the Honua MCP server is a deterministic, stateless-per-turn tool
  provider. It does not own the interaction loop.
- **The Streamable-HTTP transport is request/response for `tools/call` plus a
  one-way server→client SSE notification channel.** It has no machinery to issue
  a server-initiated JSON-RPC *request* and await a correlated client *response*
  mid-tool-call. A literal server-initiated `elicitation/create` round-trip
  would require a new bidirectional request/response subsystem.

## Decision

Map the clarification envelope onto the MCP elicitation **payload**, capability-
detected per session, and hand it back to the client inside the tool result
rather than performing a server-initiated round-trip.

1. **Capability detection at `initialize`.** When the client's `capabilities`
   object advertises `elicitation` (`"elicitation": {}`), the session issued on
   the `Mcp-Session-Id` header records an `ElicitationSupported` flag
   (`McpSessionManager`). The flag is negotiated once at handshake and read on
   later `tools/call`s via the session id.

2. **Projection in the grounding tools.** When a grounding/clarify turn produces
   a clarification and the calling session supports elicitation, the tool emits
   an `elicitation` object — the MCP `elicitation/create` `params` shape
   (`message` + `requestedSchema`) — and clears the proprietary `clarification`
   envelope (exactly one is populated). The client renders the form, collects
   answers, and replays them through `honua_clarify_intent` (answers keyed by the
   same `questionId`, so the round-trip is mechanical).

3. **Graceful fallback.** A session that did not advertise elicitation, a
   stateless request with no session id, or a clarification that is not
   representable in the elicitation subset keeps the existing `clarification`
   envelope unchanged. The default (no-elicitation) response is byte-identical to
   today's behavior.

4. **Question-kind mapping** (elicitation permits only flat primitive schemas):
   - single-select with options → `string` with `enum` + `enumNames`
   - free-text (and a degenerate option-less single-select) → `string`
   - confirmation/approval → `boolean`
   - **multi-select → not representable** (it needs an array, which the subset
     forbids). Such envelopes fall back to the proprietary shape even when the
     client supports elicitation.

## Rationale / deviation from a literal reading

"Uses MCP elicitation" is realized as *the client executing an
elicitation the server supplied*, not as a server-initiated
`elicitation/create` RPC. This is the sound interpretation for a deterministic,
client-driven tool server on a request/response transport: the server produces a
standards-shaped elicitation the elicitation-capable client renders natively,
instead of a proprietary envelope it would have to special-case. It satisfies
#2484's acceptance criteria (capability-detected, graceful fallback, tested)
without building a bidirectional request/response subsystem that the transport
and the deterministic-server model do not call for.

## Consequences

- The grounding output gains an optional `elicitation` field alongside the
  optional `clarification` field; the two are mutually exclusive on a turn. The
  published `outputSchema` documents both. Input schemas and the vendored
  `geospatial-mcp` conformance schemas are untouched.
- No new endpoint, migration, or transport method; the change is additive and
  backward-compatible.

## Follow-ups (geospatial-mcp standard)

Recommended, not implemented here:

- The `geospatial-mcp` standard should bless the tool-result-embedded elicitation
  hand-off (server supplies `elicitation/create` params in the grounding result
  for a client to execute) so reference clients handle it uniformly, and clarify
  how elicitation `content` maps back onto `clarify_intent.response.answers`
  (including boolean confirmation → answer string).
- Consider a representation for **multi-select** clarifications, which the
  current elicitation subset (flat primitives, no arrays) cannot express.
- If a future transport/profile supports server-initiated request/response, a
  literal `elicitation/create` round-trip could supersede the embedded hand-off.
