---
type: guide
title: "Connect AI agents to Honua over MCP"
description: "Point any MCP-capable agent (Claude Code, Claude Desktop, or your own client) at Honua's built-in MCP endpoint to plan, validate, dry-run, and execute geoprocessing work with the same authorization rules as every other protocol."
resource: "honua://capability/ai.mcp-discovery"
resources:
  - "honua://capability/ai.agent-operations"
  - "honua://capability/ai.approval-workflows"
---
# Connect AI agents to Honua over MCP

Honua's agent endpoint is `POST /mcp`: JSON-RPC 2.0 over Streamable HTTP, MCP protocol revision `2025-03-26`. Any MCP-capable agent connects to it and gets the same tools, the same authorization checks, and the same approval rules as every other protocol. An agent can discover the catalog, query features, plan and run geoprocessing, observe operational health, compose Studio drafts, and *propose* control-plane changes. It never approves its own proposals, and no feature-edit tool is ever advertised.

Which tool calls an agent may make is an edition boundary: reading is Community, changing anything is Pro, and organisational approval policy is Enterprise. See [What an agent can do in each edition](agent-capabilities-and-editions.md).

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) and a published layer ([publish layers](../publish/publish-layers.md)). Tool calls require an authenticated identity ([authentication](../secure/authentication.md)).

The handshake methods (`initialize`, `tools/list`, `resources/list`, `resources/templates/list`) are open; `tools/call` and `resources/read` require an authenticated principal plus the matching operator grant. Authentication accepts `X-API-Key` and OAuth bearer tokens (`Authorization: Bearer`); when both are present, the bearer token is evaluated first.

## Connect a client

**Claude Code.** Add the server to the project's `.mcp.json`:

```json
{
  "mcpServers": {
    "honua": {
      "type": "http",
      "url": "http://localhost:8080/mcp",
      "headers": { "X-API-Key": "${HONUA_API_KEY}" }
    }
  }
}
```

**Claude Desktop** launches MCP servers over stdio, so it reaches a deployment through the `honua-mcp-proxy` bridge shipped in `@honua/mcp-server`. The configuration is in [Drive Studio from Claude Desktop](../../studio/drive-from-claude-desktop.md).

**Any other client** that speaks the MCP HTTP transport works the same way: send credentials as the `X-API-Key` header (or your deployment's bearer token) on every request. The initialize handshake is the standard one:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "initialize",
  "params": {
    "protocolVersion": "2025-03-26",
    "capabilities": {},
    "clientInfo": { "name": "honua-docs", "version": "1" }
  }
}
```

### Verify

Run `POST /mcp` with `{"jsonrpc":"2.0","id":2,"method":"tools/list"}`.

The response lists the `honua_*` tools this deployment advertises, with JSON Schema input definitions. Which ones appear depends on the deployment profile (see [Which tools and resources appear](#which-tools-and-resources-appear-capability-gating)), so treat `tools/list` as the authoritative inventory rather than counting the families below. From your agent, "list the Honua tools and validate an empty plan" should return a structured violation list (for example `EMPTY_PLAN_ID`), not an error.

Each tool descriptor also carries MCP behavior `annotations` (`title`, `readOnlyHint`, `destructiveHint`, `idempotentHint`) and a `structuredContentSchema` describing the tool's structured result, so schema-driven clients can reason about safety and validate responses.

## What the tools do

### Plan, validate, and run geoprocessing

Start with the safe ones; everything in the planning family is read-only:

- `honua_ground_candidates` / `honua_clarify_intent` — turn a natural-language goal into a drafted intent with candidate datasets and processes.
- `honua_plan_analysis` — draft an executable plan from an intent. It replays deterministic fixtures (responses are flagged `engine: "fixture"`); the server performs no model inference of its own, so compiling an arbitrary intent into plan steps is your client agent's job. Confirm the result with `honua_validate_plan`.
- `honua_validate_plan` — static validation: returns `isExecutable`, `requiresApproval`, violations, and warnings.
- `honua_dry_run_plan` — estimates duration, artifacts, and side effects without executing.
- `honua_validate_package` / `honua_preview_package` — review a map/app package before execute or publish.

Once a plan validates:

- `honua_execute_plan` — submits the plan (supports an `idempotencyKey`); returns a `jobId` and a `honua://jobs/{jobId}` resource URI.
- `honua_cancel_job` — requests cancellation by `jobId`.

A worked example that runs one buffer through OGC API Processes, MCP, and the JavaScript SDK is in [Geoprocessing with AI](../query-analyze/geoprocessing-with-ai.md).

### Read results through resources

`resources/read` serves:

- `honua://catalog/processes` — the process catalog the planner can draw from.
- `honua://jobs/{jobId}` — live job status, phase, and percent complete.
- `honua://jobs/{jobId}/results` — the result package for a terminal job.
- `honua://jobs/{jobId}/report` — a structured analysis report for the same job.
- `honua://workspaces/{workspaceId}` — workspace lifecycle for job outputs.

Postgres-backed deployments also advertise the durable promotion catalog: `honua://published-services`, `honua://deployments`, `honua://map-packages`, and `honua://app-packages` (plus their item resources). Storeless or non-Postgres hosts omit these resources unless they register canonical durable stores.

> `honua://capability/<key>` is **not** one of these. It is an Open Knowledge Format document identity used by the published docs bundle, not an MCP resource; a `resources/read` on one returns `not_found`. Resolve it by opening the matching page under `okf/capabilities/` instead.

### Observe operations and propose changes

Read-only ops tools give an agent the same operational posture a human sees in Console:

- `honua_ops_health` and `honua://ops/health` — current operational posture.
- `honua_ops_findings` and `honua://ops/findings` — deterministic findings and recommended actions where real executors exist.
- `honua_alert_events` — Preview customer GIS alert events and ops notifications. Customer alerting requires explicit opt-in for 2026.1.
- `honua_operate_events` — fused Operate timeline events.
- `honua_supported_operation_kinds` — the live catalog of operation classes that can actually be routed.

Mutating control-plane requests use schema-closed proposal tools: `honua_propose_finding`, `honua_propose_deploy_plan`, `honua_propose_deploy_operation`, `honua_propose_rollback`, and `honua_propose_platform_release_convergence`. Each creates a proposal and returns its id; a separate authorized principal approves it in the Console inbox or with the Admin CLI. MCP does not approve its own proposals and does not accept opaque execution payloads. The full observe → diagnose → propose → approve loop is in [Operating Honua](../operate/README.md).

### Apply a catalog style

`honua_apply_style_preset` applies a catalog style to a layer. It requires admin write access in addition to the published-service publish grant, uses the `style.apply-preset` operation, and honors operator approval and `Operations:Policy` rules before changing the layer. An approval-required result has `approvalRequired: true`; when a durable proposal is created, use its returned `proposalId` and resource URI to track approval. Only a completed application returns `applied: true`. Set `dryRun: true` to validate without changing the layer; a completed preview returns `dryRun: true` and `applied: false`. Approval plans pin the service, layer, preset, publication, resource and storage binding; replay refuses a rebound target and requires a new approval request. If the binding commits but metadata reconciliation fails, the result keeps `applied: true` with a `warning`; re-apply the preset to retry.

### Compose Studio drafts

The nineteen `honua_studio_*` tools compose the same server-resident draft the Studio UI observes: create, read and update a draft; add and remove layers, widgets, controls and interaction bindings; set styles, visibility and the view; validate and preview; save an immutable version; reopen a version as a new draft; and propose a saved version for governed publication. The tool table is in [Studio MCP tools](../../studio/mcp-tools.md).

Two rules matter when driving them:

- **Every mutation is generation-checked.** Pass the `generation` last returned by `honua_studio_get_draft` or `honua_studio_create_draft`. A stale value returns `failed_precondition` with the current generation; fetch the draft again and reconcile rather than resubmitting blindly. `honua_studio_validate_draft` and `honua_studio_preview_draft` are read-only and never advance the generation, so calling them between mutations is safe.
- **Publication is not a canvas mutation.** `honua_studio_save_version` is the only way to obtain the `versionId` and `contentHash` that `honua_studio_propose_publication` requires. The proposal tool records intent and returns proposal, operation and audit identities; a separate authorized principal approves it, and the agent polls the returned `proposalUri` for the final status and active URL.

These tools authorize against their own operator-grant family (`StudioDraft`), separate from the package-review tools above, so composition access can be scoped independently. All nineteen are advertised unconditionally; a host that never composed Studio persistence still lists them but fails calls with a structured, retryable `unavailable` error.

### What is never advertised

No feature-edit tool appears in any profile, by design: an agent does not mutate source GIS records through MCP. Editing stays on the human protocol and API workflows described in [Edit features](../edit/edit-features.md).

## Two MCP surfaces

There are two MCP surfaces in the Honua platform, and it is easy to conflate them.

| | **honua-server `/mcp`** | **honua-devops operator agent** |
|---|---|---|
| Transport | HTTP `POST /mcp` (Streamable HTTP), authenticated | MCP stdio (`honua-devops --mcp`) |
| Roster | The data-access, planning, Studio and ops-evidence tools on this page, plus a dynamic `honua_op_*` tool per published operation | Operator-intelligence tools that reason over the evidence and plan diagnosis, tuning, upgrades and remediation |
| What it does | Serves geospatial data-access and Studio workflows and reads bounded operational evidence; at most it *proposes* a control-plane action a human approves | Consumes this server's evidence tools and acts on them through its own day-2 loop |
| Licensing | Open-core (ELv2), included in Community | Public source under a proprietary licence; built from source or run as a container, not installed from a package registry |

The eight ops-evidence tools on this page (`honua_ops_health`, `honua_ops_findings`, `honua_alert_events`, `honua_operate_events`, `honua_platform_release_status`, `honua_deploy_operations`, `honua_supported_operation_kinds`, `honua_propose_rollback`) are deliberately public: they expose bounded read-only facts and human-gated proposals, not operator reasoning.

A third, unrelated server exists in the same npm package as the Claude Desktop bridge: the standalone `honua-mcp` binary serves any public ArcGIS or OGC endpoint and needs no Honua deployment. See [the standalone MCP server](https://github.com/honua-io/honua-sdk-js/blob/trunk/docs/mcp-server.md).

## Workflow views (bounded discovery)

The complete catalog is intentionally broad — it exists for parity and as the expert escape hatch — but handing every descriptor to a model in one turn hurts tool-selection reliability. The server therefore publishes **named, server-authored workflow views**: bounded subsets of the *same* canonical catalog, selected by server-owned stage rules.

Discover the published views with `honua_list_capabilities`; each entry carries the view `name`, `title`, `description`, `revision`, its deterministic `revisionDigest` / `membershipDigest` / `descriptorDigest`, its `toolCount`, and the measured `descriptorBytes` / `estimatedTokens`. Clients must not keep their own list of view names or tool names.

Select a view three ways, highest precedence first:

1. **Per request** — `{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{"view":"setup"}}` (or `params._meta["honua.io/workflow-view"]`).
2. **Per session** — send `_meta: {"honua.io/workflow-view": "setup"}` in `initialize.params`; the negotiated name binds to the issued `Mcp-Session-Id` and applies to every later `tools/list` on that session.
3. **Per server profile** — set `Mcp:WorkflowViews:DefaultView`. The default is the server-authored `default` view, capped at 12 meta/workflow tools.

The shipped view is `setup`: the bounded terminal path of readiness → connect/import → publish service and layer → verify access → canonical style and render → bounded geoprocessing → Studio map/dashboard composition and lifecycle → publication submit and status. It is budget-bounded (at most 48 descriptors, 128 KiB of aggregate canonical descriptor JSON, 16 KiB per descriptor), so the whole view arrives in one page with no `nextCursor`. Its current revision includes draft read, edit, preview, immutable version save, and saved-version reopen; destructive delete and rollback operations remain outside it.

A view is **discovery, not authority**:

- Selecting one can only *narrow* what `tools/list` returns. Membership grants nothing, caches no prior allow decision, and never widens a principal's reach.
- Every `tools/call` is independently reauthenticated and reauthorized against the current actor, tenant, roles/grants, OAuth scope, and policy — whether or not the tool was discovered through a view.
- The complete paginated catalog is an explicit admin operation: pass the reserved name `full`, which overrides a session or profile default and requires an admin role. The narrowed response advertises this escape hatch in its own `_meta.fullCatalogView`.
- `honua_list_capabilities` returns at most 12 tools and 12 resources by default. Follow `nextToolCursor` and `nextResourceCursor`; `fullExport: true` is admin-only.
- `resources/read` returns at most 64,000 characters by default. A caller may explicitly request `maxChars` up to the hard ceiling of 1,000,000.
- Members carry the **exact** canonical description, annotations, and input/output schemas the full catalog serves; nothing is truncated or re-described.

Studio composition and lifecycle members also carry server-owned routing metadata in their canonical descriptor. `_meta["honua.studio"]` identifies the `honua.studio.composition` family, the `setup` view, and its revision, so terminal and SDK clients can route the live family without a hard-coded tool-name table. The metadata is identical in the full catalog and the narrowed view, and remains discovery-only.

Membership is derived from the live catalog, so an eligible server operation that appears (or disappears) at runtime joins or leaves the view with no client or SDK source-list edit. Runtime-published members are appended after the static ones so a mid-conversation `notifications/tools/list_changed` refresh does not re-sort the `tools` array and invalidate a host's prompt cache.

## Pagination

The list methods (`tools/list`, `resources/list`, `resources/templates/list`, `prompts/list`) are paginated per MCP 2025-03-26: when more entries remain the result carries an opaque `nextCursor`; pass it back as `params.cursor` to fetch the next page. A single-page result omits `nextCursor`. Treat cursors as opaque and echo them verbatim; an invalid or expired cursor returns JSON-RPC `-32602` invalid-params. Large `resources/read` documents (job results, catalogs) are chunked the same way — each page's `text` concatenates per `uri` to rebuild the full document, with `nextCursor` pointing at the next chunk.

## OAuth 2.1 bearer tokens and scope mapping

`/mcp` is an OAuth 2.1 resource server: it accepts `Authorization: Bearer` tokens validated against your configured OIDC authorities (Entra, Keycloak, Okta, Auth0, Google, or a generic provider), and advertises how to discover the authorization server through RFC 9728 protected-resource metadata at `/.well-known/oauth-protected-resource/mcp`. Honua is never the authorization server — your IdP mints the tokens; Honua only validates and consumes them.

Authorization is a two-layer intersection. The per-tool **operator grant** model remains the authority for *what a principal may do*. A bearer token's **scopes** then narrow that: a scope can only ever restrict what the principal's grants already permit — it can never widen them. So the effective authority of a bearer caller is `grants ∩ scopes`.

**This applies only to OAuth bearer tokens.** X-API-Key callers, interactive sessions, and the dev-auth bypass are not scope-governed and are unaffected.

### Scope taxonomy

Scopes are defined at operation granularity across every operator resource type (catalog, workspace, process, package, deployment, job, published service). Mint a token whose `scope` claim (space-delimited, per RFC 9068; `scp` is also read) lists the scopes the agent needs:

| Scope | Authorizes |
|---|---|
| `honua.mcp.full` | Every operation — the token is bounded only by its grants (no narrowing). Use for a full-authority agent. |
| `honua.mcp.discover` | Catalog/capability discovery (`Discover`). |
| `honua.mcp.read` | Read resource state and results (`Read`; implies `Discover`). |
| `honua.mcp.create` | Create new resources or artifacts (`Create`). |
| `honua.mcp.execute` | Execute built-in analytic tools and jobs (`Execute`). |
| `honua.mcp.execute.mutating` | Mutating built-in geoprocessing (`ExecuteMutatingProcess`; implies `Execute`). |
| `honua.mcp.execute.customcode` | Operator-supplied custom-code geoprocessing (`ExecuteCustomCode`; implies `Execute`). |
| `honua.mcp.promote` | Promote workspace artifacts (`Promote`). |
| `honua.mcp.publish` | Publish/deploy packages (`Publish`). |

The full vocabulary is advertised in the RFC 9728 metadata's `scopes_supported`.

### Studio draft grants

The operator-grant model includes a `StudioDraft` resource type — a distinct grant family from `Package` — for the Studio draft-lifecycle and composition tools. With `Studio:EndUserAuthorization:Enabled` on, non-admin end users hold `StudioDraft` grants scoped to their own drafts using the same ownership convention as the REST lifecycle API: a role grant with layer `own` authorizes every draft/content item the principal owns; a grant scoped to a concrete resource id authorizes an operator-provisioned delegate instead. Publish and rollback are additionally policy-gated operations that require their own `StudioDraft` grant even for the resource's own owner. Admin principals, and the OAuth bearer-scope narrowing above (`grants ∩ scopes`), apply unchanged.

**Fail-closed default.** A bearer token that presents **no recognized `honua.mcp.*` scope** authorizes nothing — every tool call returns `insufficient_scope` — even when its principal's grants (or `admin` role) would allow the operation. This is deliberate: least-privilege delegation is the reason to issue an agent an OAuth token rather than a shared API key. To restore full grant-bounded authority explicitly, include `honua.mcp.full`.

**Least-privilege example.** Issue an ops-monitoring agent a token scoped `honua.mcp.discover honua.mcp.read`: it can browse the catalog and read job results and ops-evidence, but a `tools/call` that submits a geoprocessing plan is denied with `insufficient_scope` — without ever touching the principal's grants.

## Harden the MCP endpoint (production)

`POST /mcp` issues a session id on `initialize` (returned on the `Mcp-Session-Id` header) and validates it on every later request. The defaults below bound host memory and bind each session to the caller so a public, anonymous-capable endpoint cannot be abused. Options live under the `Mcp` configuration section.

| Setting | Default | Purpose |
|---|---|---|
| `Mcp:ServerInitiatedStreamEnabled` | `false` | Offer the optional server-initiated `GET /mcp` SSE stream (progress / `*/list_changed`). Off by default: `GET /mcp` returns `405 Method Not Allowed` + `Allow: POST, DELETE` per the Streamable-HTTP spec, so spec-compliant SDK clients skip the stream instead of hanging it at a buffering ingress. |
| `Mcp:SessionIdleTimeout` | `00:30:00` | Sliding idle TTL. Every request (or an opened GET stream) on a session refreshes the window; an untouched session expires and is swept. Expired ids return `404`, so clients re-initialize cleanly. |
| `Mcp:MaxSessions` | `10000` | Maximum concurrently tracked sessions. Bounds memory on a public endpoint. |
| `Mcp:SessionEvictionPolicy` | `EvictLeastRecentlyUsed` | What to do at capacity: evict the least-recently-used session, or `RejectNew` (refuse `initialize` with a retryable `unavailable` error and leave live sessions untouched). |
| `Mcp:StatelessSessionFallback` | `true` | Serve a `POST /mcp` that presents a well-formed but unknown `Mcp-Session-Id` as if it were session-less instead of `404`. Session state is per instance, so this keeps spec-compliant clients working on multi-instance deployments without sticky routing. Set `false` for the strict Streamable-HTTP behavior (`404` so the client re-initializes). Malformed ids always `404`. |

**Stateless session fallback.** Session state is held in each instance's memory, so on a multi-instance deployment without sticky routing a client that ran `initialize` against one instance would strictly `404` whenever a later `POST` lands on a different instance. By default the server instead serves such a request statelessly: every `POST` is independently authenticated and authorized, and the only session-negotiated state (elicitation capability, SSE progress routing) degrades to its documented session-less fallback. No session id is echoed on these responses — only `initialize` mints. Note this also means a session id that was terminated with `DELETE /mcp` (or idle-expired) is served statelessly rather than `404`; set `Mcp:StatelessSessionFallback=false` to restore the strict spec posture.

**Server-initiated streaming.** Leave `Mcp:ServerInitiatedStreamEnabled=false` behind any ingress that buffers responses — notably serverless gateways (CloudFront → API Gateway HTTP API → Lambda), where the SDK's standalone GET stream would hang at the origin. Enable it only behind ingress that can hold a streaming response open (nginx with `proxy_buffering off`, an ALB, or a direct connection). Regardless of this flag, a `GET /mcp` stream's teardown never invalidates the session — session lifetime is bounded only by `DELETE /mcp` or the idle TTL.

**Principal binding.** A session is bound at `initialize` to the authenticated principal (or to anonymous where the endpoint allows anonymous access). The binding includes the auth scheme and principal identifier, so a bearer principal and an API-key principal cannot silently share the same `Mcp-Session-Id`. A later request that presents the id under a *different* identity is rejected with a structured `permission_denied` / `requiresReauthentication` error, so a leaked `Mcp-Session-Id` cannot be ridden by another caller.

### Rate limiting

Rate limiting stays at the edge by default (nginx/ALB/WAF). The optional app-level limiter (`RateLimiting:Enabled`, off by default) already partitions correctly for MCP: by tenant, then the authenticated principal (user/API key), falling back to source IP for anonymous traffic. Recommended opt-in config for the MCP surface:

```jsonc
{
  "RateLimiting": {
    "Enabled": true,
    "GlobalRequestsPerMinute": 120   // per principal / per IP; tune to your load
  }
}
```

Do **not** attempt to partition the limiter by `Mcp-Session-Id`: a hostile client mints a fresh session per request, so a per-session bucket would be trivially bypassable. The principal/IP partition plus the `Mcp:MaxSessions` cap and idle TTL are the memory- and abuse-control mechanisms for `initialize` bursts; the edge limiter remains the first line of defense.

## Deployment profiles and the resulting surface

The MCP surface is the same set of tools and resources everywhere; one configuration switch changes how progress is delivered. Pick the profile that matches your ingress:

| Profile | Key config | Progress delivery | `honua_plan_analysis` | Notes |
|---|---|---|---|---|
| **Baseline serverless** (recommended default) | `Mcp:ServerInitiatedStreamEnabled=false` | Poll `honua://jobs/{jobId}` for job state (no server push); `GET /mcp` → `405` | `engine:"fixture"` — a canned capability demo; hand-author plans from `honua://catalog/processes` and confirm with `honua_validate_plan` | Works behind buffering ingress (CloudFront → API Gateway HTTP API → Lambda); the SDK skips the optional standalone stream. |
| **Streaming-capable** | `Mcp:ServerInitiatedStreamEnabled=true` behind non-buffering ingress | Server-initiated `GET /mcp` SSE pushes progress + `*/list_changed` | Unchanged by this switch (always `engine:"fixture"`) | Enable only behind nginx (`proxy_buffering off`), an ALB, or a direct connection — never a buffering serverless gateway. |

Both profiles change only *how* the surface behaves, not *which* tools and resources it advertises. The read-only pre-flight tools (`honua_validate_plan`, `honua_dry_run_plan`) report the same execution reality in every profile — including that a job runs a single process, so multi-step or sync-only plans are flagged rather than silently under-executed.

### Which tools and resources appear (capability gating)

A second, independent axis *does* change the advertised roster: several tools and resources are gated on the host having composed the canonical service that backs them, so `tools/list` / `resources/list` never advertise a capability that could only fail at invocation time. The single-node Postgres server profile wires all of the rows below except where a switch is noted; minimal or serverless-function compositions may omit the data provider, the promotion stores, or a geocode/route provider. Ask the running server what it actually exposes with `honua_list_capabilities` — the table is the pre-connection map, that tool is the runtime source of truth.

| Surface | Config / composition gate | Default (Postgres server profile) | When absent |
|---|---|---|---|
| Server-push `GET /mcp` SSE stream | `Mcp:ServerInitiatedStreamEnabled=true` | Off — `GET /mcp` → `405`, clients poll `honua://jobs/{jobId}` | Off by default; see the profile table above |
| Published-operation tools (operations toolset projected as `tools/call`) | Audited Admin projection: `Mcp:PublishOperations:AdminProjection` (default `true`). Full operations catalog: `Mcp:PublishOperations:Enabled=true` | The audited Admin projection is advertised as `honua_admin_*` in the authenticated `full` catalog and in `honua_list_capabilities`, not in the bounded `default` view; the full catalog is off. The audited one-time-secret, secret-input and browser-session Admin operations are never published | Omitted when both switches are off or the operations toolset is not composed |
| Promotion resources (`honua://published-services/…`, `honua://deployments/…`, map/app packages, promotion index) | Canonical publishing + deployment persistence composed | Advertised (Postgres persistence is wired) | Omitted in compositions without canonical promotion stores |
| Analysis report resource (`honua://jobs/{jobId}/report`) | `Reporting:Enabled=true` | Advertised | Omitted when reporting is disabled |
| Geocode tools (`honua_geocode_address`, `honua_geocode_addresses`) | A geocode provider is composed (the server profile wires the Nominatim provider by default) | Advertised | Omitted when no geocode provider is composed |
| Route tool (`honua_solve_route`) | A routing provider is selected (`Routing:Provider`; `pgrouting` needs Postgres) | Advertised | Omitted when no routing provider is selected |
| Catalog / query / render / style tools (`honua_list_layers`, `honua_query_features`, `honua_describe_layer`, `honua_render_map`, style tools) | Metadata v2 graph — and, for query/render, a feature reader / raster renderer — composed by the data provider | Advertised | Omitted in compositions without a data provider |
| Dataset ingest (`honua_ingest_dataset`) | Import service composed | Advertised | Omitted without an import-capable provider |
| Platform-ops observability + deploy tools (`honua_ops_health`, `honua_ops_findings`, `honua_deploy_operations`, …) | Ops-observability / platform-ops readers composed | Advertised | Omitted in minimal hosts |

The `honua_plan_analysis`, `honua_validate_plan`, `honua_dry_run_plan`, `honua_execute_plan`, `honua_cancel_job`, `honua_list_jobs`, grounding, and `honua_list_capabilities` tools, plus the job/workspace/process-catalog/feature-catalog resources, are advertised in **every** composition — they depend only on the job runtime and embedded catalogs that are always present.

Layer discovery, layer descriptions, feature pages, and counts use the shared REST resource-access gate, including service/layer policies and per-operation grants. Discovery and schema descriptions require metadata access; feature queries and descriptions that include a row count additionally require query access. Discovery filters inaccessible layers before calculating pagination totals.

## Troubleshoot

- **`unauthenticated` on `tools/call` or `resources/read`** — handshake methods work anonymously but tool calls do not; attach the `X-API-Key` header (or token) to the client config. See [troubleshooting](../deploy/troubleshooting.md).
- **`permission_denied`** — the identity authenticates but lacks the operator grant for that tool family; grant the relevant operator permission to the calling identity.
- **`insufficient_scope`** — distinct from `permission_denied`: the identity *is* authorized by grant, but the OAuth bearer token's scopes do not cover the operation (or the token carries no recognized `honua.mcp.*` scope, which is fail-closed). Mint a token whose `scope` claim includes the scope for that operation — see [OAuth scopes](#oauth-21-bearer-tokens-and-scope-mapping). Does not apply to X-API-Key callers.
- **HTTP 202 with an empty body** — not an error: MCP notifications (`notifications/*` without an `id`) are acknowledged with 202 by design.
- **`invalid_request` (-32600)** — malformed JSON-RPC envelope; common causes are a missing `id` on a non-notification method or batching the `initialize` call (it must be sent alone).
- **Agent "succeeds" but reports a tool error** — tool failures are returned inside `result` with `isError: true` and a structured `code` (`invalid_argument`, `not_found`, `failed_precondition`, …) per the MCP error contract; read the embedded message.
- **A Studio tool returns `unavailable`** — the host has not composed Studio persistence; the tools are still listed so clients can discover them, but calls fail with a structured, retryable error.

## Next steps

- [What an agent can do in each edition](agent-capabilities-and-editions.md)
- [Drive Studio from Claude Desktop](../../studio/drive-from-claude-desktop.md)
- [Studio MCP tools](../../studio/mcp-tools.md)
- [Geoprocessing with AI](../query-analyze/geoprocessing-with-ai.md)
- [Operating Honua](../operate/README.md)
- [Run geoprocessing](../query-analyze/run-geoprocessing.md)
- [Authentication](../secure/authentication.md)
