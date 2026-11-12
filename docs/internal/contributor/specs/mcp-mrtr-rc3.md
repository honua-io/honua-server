# MCP Multi Round-Trip Requests for clarification and approval (rc.3)

Status: Implemented (design approved by main, 12 Nov). Tracks honua-server #6922. Release 2026.1-rc.3. Paths are relative to the repository
root and refer to `trunk` at `fa41cff55b`. Founder decision #7021 (5 Nov): an agent-initiated change needs
one human's approval in every edition, normally the requesting user, given in their MCP client through
MRTR; a Console approval by the same user is the fallback for clients without MRTR.

## 1. Protocol status (scope step 1)

- **SEP-2322 "Multi Round-Trip Requests"** is Final (Standards Track). It is part of the `2026-07-28`
  revision, which honua-server already serves statelessly (`McpModernProtocol.Version`,
  `server/discover`, `McpModernResults.Complete`).
- **Incomplete result.** `tools/call`, `prompts/get`, `resources/read` and `tasks` payload requests may
  return `{ "resultType": "input_required", "inputRequests": { "<key>": { "method", "params" } },
  "requestState": "<opaque string>" }`. Every other method MUST NOT. An input request is
  `elicitation/create`, `sampling/createMessage` or `roots/list`. On SSE it is the last message.
- **Retry.** The client sends a new request (new JSON-RPC `id`, same method and arguments) with
  `params.inputResponses` keyed like `inputRequests` and `params.requestState` echoed byte for byte.
  The client MUST NOT parse or modify `requestState` and MUST NOT send one it did not receive.
- **requestState rules.** The client is untrusted: the server MUST validate the state, SHOULD protect
  its integrity and confidentiality, and MUST bind user-specific state to the authenticated user. The
  SEP sets no expiry or size limit; section 4 sets ours. Results of a request that carries
  `inputResponses` or `requestState` are never cached (already true in `McpModernResults.IsCacheable`).
- **Negotiation.** There is no separate MRTR capability. A `2026-07-28` request MAY receive
  `input_required`. Each input request kind is offered only when the request's
  `io.modelcontextprotocol/clientCapabilities` metadata declares it (`elicitation`, with `form` and/or
  `url` modes). Legacy `initialize` sessions (2025-06-18, 2025-03-26) keep today's behaviour.

## 2. Today on trunk

| Path | Stateful (legacy session) | Stateless (`2026-07-28`) |
|---|---|---|
| Clarification (`GroundCandidatesTool`, `ClarifyIntentTool`) | `ClarificationElicitationMapper.ProjectOntoOutput` puts an elicitation schema in the tool output when `McpSessionStore.ElicitationSupported` | `ClientSupportsElicitation` reads only the session header, so it always returns the proprietary `clarification` envelope |
| Approval (`PublishedOperationTool.RequiresProposalSubmission`, the dispatcher policy decision) | Agent receives `RequiresApproval` with a `proposalId`; the human approves in Console (#7147 human-session verifier); agent polls the proposal | Same |

## 3. Design

### 3.1 One emitter
`McpModernResults.InputRequired` writes the incomplete result beside `Complete`. A tool never writes
`resultType` itself: it returns `McpRequestContinuation.InputRequired(...)`, an `McpToolsCallResult`
whose non-serialised `InputRequired` carries the input requests and the minted state, and
`McpDataAccessSurface` serialises it. `McpRequestContinuation` exists only for modern requests on a
server with a signing secret, and tools offer a kind only when `McpElicitationModes` (read from the
request's `clientCapabilities`) declares it, so legacy sessions keep their existing output.
The dispatcher verifies any `requestState` before the tool runs, and only for tools that implement
`IMcpInputRequiredTool` and resume that state kind (code in `Mcp/Mrtr/`).

### 3.2 Clarification
- If form elicitation is declared (`elicitation: {}` counts as form), `ClarificationElicitationMapper.TryMap`
  builds the `elicitation/create` params under key `clarify`. `requestState` carries the intent id
  and every answer given so far (so a second round does not lose the first), and is bound to the
  tool name and a hash of the canonical arguments. If the answers outgrow the state, the envelope
  is returned instead.
- On retry the tool reads `inputResponses.clarify`. `accept` resumes the intent with the answers, as
  if they had come through `honua_clarify_intent`; `decline` or `cancel` returns the envelope.
- Questions the form subset cannot represent (multi-select) keep the envelope, as today.
- No server-side state. Any replica, Lambda included, can complete the retry.

### 3.3 Approval
- **Truth is the proposal row.** `PostgresOperationProposalStore` holds status, `Version` (the
  compare-and-set generation), `RequesterId`, `PolicyRevision` and `ExpiresAt`. Nothing about an
  approval lives in process memory or only in `requestState`.
- When an agent call reaches the existing `RequiresApproval` decision, the tool creates the proposal
  as today. If `elicitation.url` is declared and `Mcp:ApprovalConsoleUrl` is set, it returns
  `input_required` with key `approve`: a URL-mode elicitation to `<console>/inbox?proposalId=<id>`
  (the Console deep link). `requestState` carries the proposal id and is bound to the principal, the
  tool and its arguments.
- The person approves in that page with an interactive human session. This is the #7147
  `IOperationHumanApprovalVerifier` path, so the rule that API keys and agent bearers cannot approve
  stays in one place. Per #7021 the requester may approve their own proposal there.
- On retry the server ignores the content of `inputResponses.approve` as evidence. It reloads the
  proposal and checks its operation, requester and tenant against the caller. A Console approval
  already claims and runs the operation (`OperationGateway`), so the retry never executes anything:
  it reports the row and never asks again (a stateless server cannot tell when the person has
  finished in Console, so re-asking would loop). Pending maps to today's `RequiresApproval` result;
  executing or submitted to `Running`; succeeded to `Completed`; rejected and cancelled to those
  statuses; failed and rolled back to `Failed` with a message saying which. An answer from the MCP client is never
  itself an approval, which matches `docs/guides/secure/approve-agent-changes.md`.
- Clients that declare only `form` mode, or no elicitation, get today's `RequiresApproval` and
  `proposalId` result and use Console. Behaviour is unchanged.
- The seams are the #7402 types (`AgentApprovalRule`, `OperatorApprovalGate`,
  `PublishedOperationTool`). This work wraps them and changes none of their decisions.

### 3.4 Why not form-mode approval
A form answer is produced by the MCP client process, which the agent drives, so the server cannot
tell a person's click from a scripted reply. URL mode sends the person to a Honua-authenticated
page, where the existing human-session check applies. Further analysis is in the private tracker.

## 4. requestState format

- Shape: `hr1.` + base64url(version, kind, issued-at, expires-at, nonce, principal binding,
  SHA-256 of tool name and canonical arguments, reference) + an HMAC-SHA256 tag. This follows `McpStatelessSessionTokens`: HKDF over the same secret, with distinct
  info strings `honua.mcp.request-state.v1/tag` and `/principal`. Every replica derives the same keys
  from configuration, and none keeps per-request memory.
- Payload holds identifiers, hashes and clarification answers, never proposal content or secrets.
  Size cap 1 KiB (reference up to 640 bytes). Lifetime 15 minutes for clarification and 1 hour for
  approval. Without a signing secret MRTR is off. Number text in arguments is hashed as sent, so a
  client must not reformat numbers between the two calls.
- Refuse the request (`-32602`, `invalid_request_state`) when the tag is wrong, the state has expired,
  the principal differs, the kind is wrong, the tool or arguments hash differs, the size is over
  the cap, `inputResponses` arrive without a state, or a legacy request carries either field. A
  refused state never falls through to a fresh, non-MRTR execution.

## 5. Tests (scope step 3)

- Units: emitter shape and no caching; capability gating per kind; requestState tamper, expiry,
  principal swap and argument swap; clarification resume; approval retry while pending, approved,
  rejected, expired and version-superseded.
- Contract, stateless: two `WebApplicationFactory` hosts over one PostgreSQL. The first call goes to A
  and the retry to B, with no session header, for both clarification and approval. A Lambda-shaped
  host (no hosted services) completes the same retry.
- Regression: legacy session elicitation projection and the envelope fallback are unchanged. The
  `RequiresApproval` result for clients without `elicitation.url` is unchanged.

## 6. Delivery

Items 1-5 land in one commit for #6922 (lane rule: one commit per ticket): emitter, capability reader
and codec; clarification; approval on the #7402 seams; tests; docs. The two-replica check is a unit
test (two codecs and two service providers with one secret). The two-host PostgreSQL contract test
and a Lambda-shaped host test remain open in #6922.
6. Follow-ups outside this repo (scope step 4, step 5 and the installed-client checks): geospatial-mcp
   spec and certification, an AI-arc approval round-trip scenario, and Claude Code and Codex checks
   once those clients declare `elicitation.url`.
