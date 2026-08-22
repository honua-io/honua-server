# ADR-0077: Model Routing Is a Client Choice — Proxy Default for the Browser, Client-Direct Elsewhere

## Status

Accepted (2026-08-17). Records decision **D7** (renumbered from "D4"; the
canonical registry is [honua-release#80](https://github.com/honua-io/honua-release/issues/80)),
extracted from the Studio/MCP convergence epic
([#3220](https://github.com/honua-io/honua-server/issues/3220)) and tracked as
[#3254](https://github.com/honua-io/honua-server/issues/3254).

Pairs with [ADR-0076](0076-retire-server-side-generation-families.md), which
records **D8** (renumbered from "D5"). The two are related and independent:
ADR-0076 decides that the server performs no inference *of its own*; this ADR
decides how the client's inference request is *routed*. Neither depends on the
other landing first.

## Context

ADR-0076 established the property the product claims:

> **The server performs no model inference of its own as part of executing a
> capability.** It may forward a client's inference request through an
> explicitly configured proxy.

The second sentence is the one this ADR is about. `StudioAiProxy`
(`src/Honua.Ai/Features/StudioAiProxy/`, #3000) relays a provider-neutral chat
turn — tools, tool-choice and tool-result round trips included — to Anthropic,
an OpenAI-compatible endpoint, or Bedrock, using operator-held credentials, over
SSE at `POST /api/v1/studio/ai/chat`. honua-studio#2 REQ-003 made that the
default. The epic recorded the counter-position as an open decision: a
client-direct default is the more server-agnostic posture, because a conformant
third-party server has no proxy at all.

The decision was left as prose inside an epic, where it gated two repositories
without being owned or dated. It is recorded here.

### What is actually at stake

The adoption goal is that authoring is not locked to honua-server. That goal is
purchased by the **tool plane**, not by the model traffic: composition happens
through MCP tools against any conformant server. Model routing is a separate
axis, and conflating the two is what made this look like a single either/or.

Three arguments for a blanket client-direct default were weighed and are
recorded here with what was found, because two of them do not survive contact
with the code.

**"Every new provider is a server release."** Largely false.
`StudioAiProxyProviderOptions.Kind` accepts `openai` with an arbitrary
`Endpoint`, which covers OpenRouter, LiteLLM, Ollama, vLLM and LM Studio by
configuration alone; `anthropic` and `bedrock` adapters already exist. Provider
breadth here is a config file, not a release train.

**"The server should not be in the AI path by default."** Already true.
`StudioAiProxyConfiguration.Enabled` defaults to `false`, and the proxy
self-gates on it. A stock honua-server relays nothing until an operator turns it
on and configures a provider. What was *not* structurally true is the separate
unconditional registration of the generation services — that is ADR-0076's
subject, not routing's.

**"A conformant third-party server has no proxy."** True, and decisive — for
that host. It argues that client-direct must be a supported mode. It does not
argue that a browser talking to a Honua deployment should default to it.

### Why the browser is the case that differs

Client-direct in a browser means a long-lived provider API key in web storage.
No major provider issues a short-lived, scope-limited token that would make this
safe, so any XSS, hostile extension, or shared machine yields unbounded spend on
the key holder's account and full read of the conversation. Bedrock-direct from
a browser is worse on the axis that motivated client-direct in the first place:
it requires a Cognito identity pool and SigV4 signing in the client — more
Honua-specific plumbing than the proxy, not less.

The hosted-demo case makes it concrete: a demo.honua.io visitor holds no key at
all, and the 2026.1 Studio definition of done is a nightly `@live` journey run
by exactly such a visitor. A blanket client-direct default would put the
flagship proof on the non-default path.

## Decision

**Model routing is a client choice, not a server mandate.** `StudioAgentSession`
takes a transport; the server-proxy transport and a direct-provider transport
are peers.

- **Browser Studio, hosted or multi-user → server proxy.** A browser cannot
  custody a provider credential, and a shared deployment needs one place to hold
  keys, meter spend, and rate-limit.
- **Non-browser hosts → client-direct**, and this is a **supported production
  mode**, not a development affordance. It covers local MCP hosts (Claude
  Desktop, Claude Code, agent processes), single-operator self-hosts where the
  operator holds their own key, and third-party conformant servers that expose
  the MCP tool plane and no proxy.
- **Invariant: no authoring capability may be reachable only through the
  proxy.** Every `honua_studio_*` composition tool is on the MCP surface and
  stays there. If a capability can only be exercised by routing model traffic
  through honua-server, that is a defect against this ADR.
- `StudioAiProxyConfiguration.Enabled` stays `false` by default. The proxy is
  opt-in per deployment.

Recording this promotes client-direct from the "dev-only" wording used in
grooming to a first-class mode. The reason is that a local MCP host relaying
model traffic through a map server is not a configuration anyone will choose,
and that host is the actual server-agnostic adoption surface.

### What the proxy is for

Stated positively, so it is not read as vestigial: operator key custody
(including *no* key at all under a Bedrock instance role), spend audit via
`IAuditLog`, rate limiting (`RateLimitAttribute(30)` on `/chat`), egress
control, and BYOM-less demos and trials where the end user has no provider
credential of their own.

### The gap this exposes

`honua-sdk-js/src/studio-agent/` ships exactly one transport, `SseChatTransport`,
which targets the proxy. The mode this ADR declares supported therefore has no
implementation today, and honua-server is in practice a hard dependency of
authoring in every host. Tracked as
[honua-sdk-js#1348](https://github.com/honua-io/honua-sdk-js/issues/1348) —
2026.2, and small, because `ai-contract.ts` is already provider-neutral. This
ADR is what makes that ticket a correctness item rather than an enhancement.

## Consequences

### Preserved

- honua-studio#2 REQ-003 stands for the surface it was written about, amended to
  name that surface: the proxy is the default **for the hosted browser app**.
- The proxy, unchanged in scope. #3303 continues to widen its authorization from
  admin-only to `Studio:EndUserAuthorization`, which is what makes the browser
  default usable by a non-admin demo user.
- BYOM in both directions: an operator points the proxy at their own provider,
  or a client-direct host uses its own credential.

### Accepted costs

- Two transports to keep behaviourally identical. A tool-calling turn must
  behave the same on either, which is an explicit acceptance criterion on
  honua-sdk-js#1348 rather than an assumption.
- The browser default keeps honua-server in the AI path for the hosted app,
  which is a deliberate trade of server-agnosticism for credential safety on the
  one host that cannot hold a credential.
- Documentation must not present the two transports as interchangeable
  everywhere. The browser guardrail is part of the docs contract.

### What this does not decide

- No model and no provider is chosen. That is D2 (LLM access on demo) and
  deployment configuration.
- Nothing about ADR-0076's retirement of the generation families is reopened.
- Bedrock direct-from-browser stays out of scope. The proxy is the answer there.
