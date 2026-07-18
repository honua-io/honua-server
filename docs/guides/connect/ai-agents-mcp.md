# Connect AI agents to Honua over MCP

Point any MCP-capable agent (Claude Code, Claude Desktop, or your own client) at Honua's built-in MCP endpoint to plan, validate, dry-run, and execute geoprocessing work with the same authorization rules as every other protocol.

For operations work, MCP is the agent seat in the same control loop that Console `/operate` uses. See [Operating Honua](../operate/README.md) for the observe -> diagnose -> propose -> approve model, the autonomy ladder, and the current line between shipped MCP observability tools and in-progress platform-ops tools.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) and a published layer ([publish layers](../publish/publish-layers.md)). Tool calls require an authenticated identity — see [authentication](../secure/authentication.md).

The endpoint is `POST /mcp`: JSON-RPC 2.0 over HTTP (single requests and batches), MCP protocol revision `2025-03-26`. The handshake methods (`initialize`, `tools/list`, `resources/list`, `resources/templates/list`) are open; `tools/call` and `resources/read` require an authenticated principal plus the matching operator grant.

Authentication supports both legacy `X-API-Key` and OAuth bearer tokens (`Authorization: Bearer`).
When both are present, bearer tokens are evaluated first for this route.

## Two MCP surfaces: data-access (open) vs. operator (proprietary)

There are two distinct MCP surfaces in the Honua platform, and it is easy to
conflate them. This page documents the **open** one. The boundary between them is
**evidence vs. intelligence** (ADR-0066), which is also the open-core licensing
line (ADR-0024).

| | **This repo — MCP data-access surface** | **`honua-devops` — operator surface** |
|---|---|---|
| Dispatcher | `McpDataAccessSurface` in `honua-server` | operator agent in `honua-devops` (private) |
| Transport | HTTP `POST /mcp` (Streamable HTTP), authenticated | MCP stdio (`--mcp`) |
| Roster | ~27 studio/data-access tools (query, render, style, geocode/route, plan/execute, authoring/packaging) + **8 bounded, read-only ops-*evidence* tools** | ~35 operator-*intelligence* tools |
| What it does | Serves geospatial data-access and studio workflows, and reads bounded operational **evidence**; at most it *proposes* a control-plane action that a human approves in the Console inbox (ADR-0062) | Reasons over that evidence and acts: diagnose, tune, upgrade planning with rollback gates, GitOps rollout, remediation planning. Consumes this repo's evidence tools via its `honua_observe_diagnose_propose` day-2 loop |
| Licensing | Open-core (ELv2); included in Community (ADR-0024) | Private/proprietary; **not** part of the open-core runtime promise |

The 8 ops-evidence tools (`honua_ops_health`, `honua_ops_findings`,
`honua_alert_events`, `honua_operate_events`, `honua_platform_release_status`,
`honua_deploy_operations`, `honua_supported_operation_kinds`,
`honua_propose_rollback`) are deliberately public: they expose bounded read-only
facts and human-gated proposals, not operator reasoning. This repo serves the
*evidence*; `honua-devops` supplies the *intelligence* that acts on it. Nothing
named "operator surface" ships in this repo.

## Steps

1. Confirm the endpoint answers:

   ```bash
   BASE=http://localhost:8080
   curl -s -X POST "$BASE/mcp" -H "Content-Type: application/json" -d '{
     "jsonrpc": "2.0", "id": 1, "method": "initialize",
     "params": { "protocolVersion": "2025-03-26", "capabilities": {},
                 "clientInfo": { "name": "curl", "version": "0" } }
   }'
   ```

2. Register the server with your MCP client. For Claude Code, add to the project's `.mcp.json`:

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

   Any client that speaks the MCP HTTP transport works the same way; send credentials as the `X-API-Key` header (or your deployment's bearer token) on every request.

3. Ask the agent to list tools and start with the safe ones — everything in the planning family is read-only:

   - `honua_ground_candidates` / `honua_clarify_intent` — turn a natural-language goal into a drafted intent with candidate datasets and processes
   - `honua_plan_analysis` — draft an executable plan from an intent. Runs in fixture (demo) mode by default (responses are flagged `engine: "fixture"`); [turn on the live planner](mcp-live-planner.md) to compile arbitrary intents.
   - `honua_validate_plan` — static validation: returns `isExecutable`, `requiresApproval`, violations, and warnings
   - `honua_dry_run_plan` — estimates duration, artifacts, and side effects without executing
   - `honua_validate_package` / `honua_preview_package` — review a map/app package before execute or publish

4. Execute and manage jobs once a plan validates:

   - `honua_execute_plan` — submits the plan (supports an `idempotencyKey`); returns a `jobId` and a `honua://jobs/{jobId}` resource URI
   - `honua_cancel_job` — requests cancellation by `jobId`

5. Read results through resources (`resources/read`):

   - `honua://catalog/processes` — the process catalog the planner can draw from
   - `honua://jobs/{jobId}` — live job status, phase, and percent complete
   - `honua://jobs/{jobId}/results` — the result package for a terminal job
   - `honua://jobs/{jobId}/report` — a structured analysis report for the same job
   - `honua://workspaces/{workspaceId}` — workspace lifecycle for job outputs

   Postgres-backed deployments also advertise the durable promotion catalog:
   `honua://published-services`, `honua://deployments`, `honua://map-packages`, and
   `honua://app-packages` (plus their item resources). Storeless or non-Postgres
   hosts omit these resources unless they register canonical durable stores.

6. For operational observability, use the read-only ops tools:

   - `honua_ops_health` and `honua://ops/health` - current operational posture.
   - `honua_ops_findings` and `honua://ops/findings` - deterministic findings and recommended actions where real executors exist.
   - `honua_alert_events` - GIS alert events and ops notifications.
   - `honua_operate_events` - fused Operate timeline events.

   Before proposing a mutating control-plane operation, call the read-only `honua_supported_operation_kinds` tool and choose only a returned kind. Then use `honua_propose_operation`; approval still resolves through the Console inbox, and MCP does not approve its own proposals. The `supportedKinds` field on rejected proposal responses remains for compatibility but is deprecated for discovery.

## Verify

```bash
BASE=http://localhost:8080
curl -s -X POST "$BASE/mcp" -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
```

The response lists the nine `honua_*` tools above with JSON Schema input definitions. From your agent, "list the Honua tools and validate an empty plan" should return a structured violation list (for example `EMPTY_PLAN_ID`), not an error.

Each tool descriptor also carries MCP behavior `annotations` (`title`, `readOnlyHint`, `destructiveHint`, `idempotentHint`) and a `structuredContentSchema` describing the tool's structured result, so schema-driven clients can reason about safety and validate responses.

## Pagination

The list methods (`tools/list`, `resources/list`, `resources/templates/list`, `prompts/list`) are paginated per MCP 2025-03-26: when more entries remain the result carries an opaque `nextCursor`; pass it back as `params.cursor` to fetch the next page. A single-page result omits `nextCursor`. Treat cursors as opaque and echo them verbatim; an invalid or expired cursor returns JSON-RPC `-32602` invalid-params. Large `resources/read` documents (job results, catalogs) are chunked the same way — each page's `text` concatenates per `uri` to rebuild the full document, with `nextCursor` pointing at the next chunk.

## Troubleshoot

- **`unauthenticated` on `tools/call` or `resources/read`** — handshake methods work anonymously but tool calls do not; attach the `X-API-Key` header (or token) to the client config. See [troubleshooting](../deploy/troubleshooting.md).
- **`permission_denied`** — the identity authenticates but lacks the operator grant for that tool family; grant the relevant operator permission to the calling identity.
- **`insufficient_scope`** — distinct from `permission_denied`: the identity *is* authorized by grant, but the OAuth bearer token's scopes do not cover the operation (or the token carries no recognized `honua.mcp.*` scope, which is fail-closed). Mint a token whose `scope` claim includes the scope for that operation — see [OAuth scopes](#oauth-21-bearer-tokens-and-scope-mapping). Does not apply to X-API-Key callers.
- **HTTP 202 with an empty body** — not an error: MCP notifications (`notifications/*` without an `id`) are acknowledged with 202 by design.
- **`invalid_request` (-32600)** — malformed JSON-RPC envelope; common causes are a missing `id` on a non-notification method or batching the `initialize` call (it must be sent alone).
- **Agent "succeeds" but reports a tool error** — tool failures are returned inside `result` with `isError: true` and a structured `code` (`invalid_argument`, `not_found`, `failed_precondition`, …) per the MCP error contract; read the embedded message.

A separate read-only discovery/query MCP server (`@honua/mcp-server`, from the `honua-sdk-js` repository) is also available for agents that only need to browse services and query features rather than run operator workflows.

## OAuth 2.1 bearer tokens and scope mapping

`/mcp` is an OAuth 2.1 resource server: it accepts `Authorization: Bearer` tokens
validated against your configured OIDC authorities (Entra, Keycloak, Okta, Auth0,
Google, or a generic provider), and advertises how to discover the authorization
server through RFC 9728 protected-resource metadata at
`/.well-known/oauth-protected-resource/mcp`. Honua is never the authorization
server — your IdP mints the tokens; Honua only validates and consumes them.

Authorization is a two-layer intersection. The per-tool **operator grant** model
(`EnsureCallerAuthorizedAsync`) remains the authority for *what a principal may do*.
A bearer token's **scopes** then narrow that: a scope can only ever restrict what
the principal's grants already permit — it can never widen them. So the effective
authority of a bearer caller is `grants ∩ scopes`.

**This applies only to OAuth bearer tokens.** X-API-Key callers, interactive
sessions, and the dev-auth bypass are not scope-governed and are unaffected.

### Scope taxonomy

Scopes are defined at operation granularity across every operator resource type
(catalog, workspace, process, package, deployment, job, published service). Mint a
token whose `scope` claim (space-delimited, per RFC 9068; `scp` is also read) lists
the scopes the agent needs:

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

**Fail-closed default.** A bearer token that presents **no recognized `honua.mcp.*`
scope** authorizes nothing — every tool call returns `insufficient_scope` — even
when its principal's grants (or `admin` role) would allow the operation. This is
deliberate: least-privilege delegation is the reason to issue an agent an OAuth
token rather than a shared API key. To restore full grant-bounded authority
explicitly, include `honua.mcp.full`.

**Least-privilege example.** Issue an ops-monitoring agent a token scoped
`honua.mcp.discover honua.mcp.read`: it can browse the catalog and read job
results and ops-evidence, but a `tools/call` that submits a geoprocessing plan is
denied with `insufficient_scope` — without ever touching the principal's grants.

## Harden the MCP endpoint (production)

`POST /mcp` issues a session id on `initialize` (returned on the `Mcp-Session-Id`
header) and validates it on every later request. The defaults below bound host
memory and bind each session to the caller so a public, anonymous-capable
endpoint cannot be abused. Options live under the `Mcp` configuration section.

| Setting | Default | Purpose |
|---|---|---|
| `Mcp:ServerInitiatedStreamEnabled` | `false` | Offer the optional server-initiated `GET /mcp` SSE stream (progress / `*/list_changed`). Off by default: `GET /mcp` returns `405 Method Not Allowed` + `Allow: POST, DELETE` per the Streamable-HTTP spec, so spec-compliant SDK clients skip the stream instead of hanging it at a buffering ingress. |
| `Mcp:SessionIdleTimeout` | `00:30:00` | Sliding idle TTL. Every request (or an opened GET stream) on a session refreshes the window; an untouched session expires and is swept. Expired ids return `404`, so clients re-initialize cleanly. |
| `Mcp:MaxSessions` | `10000` | Maximum concurrently tracked sessions. Bounds memory on a public endpoint. |
| `Mcp:SessionEvictionPolicy` | `EvictLeastRecentlyUsed` | What to do at capacity: evict the least-recently-used session, or `RejectNew` (refuse `initialize` with a retryable `unavailable` error and leave live sessions untouched). |

**Server-initiated streaming.** Leave `Mcp:ServerInitiatedStreamEnabled=false`
behind any ingress that buffers responses — notably serverless gateways
(CloudFront → API Gateway HTTP API → Lambda), where the SDK's standalone GET
stream would hang at the origin. Enable it only behind ingress that can hold a
streaming response open (nginx with `proxy_buffering off`, an ALB, or a direct
connection). Regardless of this flag, a `GET /mcp` stream's teardown never
invalidates the session — session lifetime is bounded only by `DELETE /mcp` or
the idle TTL.

**Principal binding.** A session is bound at `initialize` to the authenticated
principal (or to anonymous where the endpoint allows anonymous access). The
binding includes the auth scheme and principal identifier, so a bearer principal
and an API-key principal cannot silently share the same `Mcp-Session-Id`.
A later request that presents the id under a *different* identity is rejected
with a structured `permission_denied` / `requiresReauthentication` error, so a
leaked `Mcp-Session-Id` cannot be ridden by another caller. This mirrors the
existing auth posture — it adds no new authentication requirement.

### Rate limiting

Rate limiting stays at the edge by default (nginx/ALB/WAF; ADR-0004). The
optional app-level limiter (`RateLimiting:Enabled`, off by default) already
partitions correctly for MCP: by tenant, then the authenticated principal
(user/API key), falling back to source IP for anonymous traffic. Recommended
opt-in config for the MCP surface:

```jsonc
{
  "RateLimiting": {
    "Enabled": true,
    "GlobalRequestsPerMinute": 120   // per principal / per IP; tune to your load
  }
}
```

Do **not** attempt to partition the limiter by `Mcp-Session-Id`: a hostile client
mints a fresh session per request, so a per-session bucket would be trivially
bypassable. The principal/IP partition plus the `Mcp:MaxSessions` cap and idle
TTL are the memory- and abuse-control mechanisms for `initialize` bursts; the
edge limiter remains the first line of defense.

## Deployment profiles and the resulting surface

The MCP surface is the same set of tools and resources everywhere, but two
configuration switches change how progress is delivered and whether
`honua_plan_analysis` compiles real intents. Pick the profile that matches your
ingress and planner configuration:

| Profile | Key config | Progress delivery | `honua_plan_analysis` | Notes |
|---|---|---|---|---|
| **Baseline serverless** (recommended default) | `Mcp:ServerInitiatedStreamEnabled=false`; no live planner | Poll `honua://jobs/{jobId}` for job state (no server push); `GET /mcp` → `405` | `engine:"fixture"` — a canned capability demo; hand-author plans from `honua://catalog/processes` and confirm with `honua_validate_plan` | Works behind buffering ingress (CloudFront → API Gateway HTTP API → Lambda); the SDK skips the optional standalone stream. |
| **Streaming-capable** | `Mcp:ServerInitiatedStreamEnabled=true` behind non-buffering ingress | Server-initiated `GET /mcp` SSE pushes progress + `*/list_changed` | Unchanged by this switch (still fixture unless a live planner is on) | Enable only behind nginx (`proxy_buffering off`), an ALB, or a direct connection — never a buffering serverless gateway. |
| **Live planner** | `PlanAnalysis:Enabled=true` (+ provider) or `WorkflowGeneration:Enabled=true` | Follows whichever streaming profile above is set | `engine:"live"` — plans compiled from your intent | See [Turn on the live MCP planner](mcp-live-planner.md). Combine with the streaming profile for push progress. |

These three profiles change only *how* the surface behaves, not *which* tools and
resources it advertises: the progress-delivery and live-planner switches leave
`tools/list`, `resources/list`, and `prompts/list` unchanged. The difference is
operational — whether progress is pushed or polled, and whether a plan is compiled
from your intent or replayed as a demo. The read-only pre-flight tools
(`honua_validate_plan`, `honua_dry_run_plan`) report the same execution reality in
every profile — including that a job runs a single process, so multi-step or
sync-only plans are flagged rather than silently under-executed.

### Which tools and resources appear (capability gating)

A second, independent axis *does* change the advertised roster: several tools and
resources are gated on the host having composed the canonical service that backs
them, so `tools/list` / `resources/list` never advertise a capability that could
only fail at invocation time. The single-node Postgres server profile
(`docker compose up`) wires all of the rows below except where a switch is noted;
minimal or serverless-function compositions may omit the data provider, the
promotion stores, or a geocode/route provider. Ask the running server what it
actually exposes with `honua_list_capabilities` — the table is the pre-connection
map, that tool is the runtime source of truth.

| Surface | Config / composition gate | Default (Postgres server profile) | When absent |
|---|---|---|---|
| Server-push `GET /mcp` SSE stream | `Mcp:ServerInitiatedStreamEnabled=true` | Off — `GET /mcp` → `405`, clients poll `honua://jobs/{jobId}` | Off by default; see the profile table above |
| Published-operation tools (operations toolset projected as `tools/call`) | `Mcp:PublishOperations:Enabled=true` **and** operations toolset composed | Off — not advertised | Off by default |
| Promotion resources (`honua://published-services/…`, `honua://deployments/…`, map/app packages, promotion index) | Canonical publishing + deployment persistence (`IPublishedServiceStore` + `IDeploymentStore`) composed | Advertised (Postgres persistence is wired) | Omitted in compositions without canonical promotion stores |
| Analysis report resource (`honua://jobs/{jobId}/report`) | `Reporting:Enabled=true` (`IAnalysisReportService`) | Advertised | Omitted when reporting is disabled |
| Geocode tools (`honua_geocode_address`, `honua_geocode_addresses`) | A geocode provider is composed (`IGeocodeCoordinatorService`; server profile wires the Nominatim provider by default) | Advertised | Omitted when no geocode provider is composed |
| Route tool (`honua_solve_route`) | A routing provider is selected (`Routing:Provider` → `IRoutingProvider`; `pgrouting` needs Postgres) | Advertised | Omitted when no routing provider is selected |
| Catalog / query / render / style tools (`honua_list_layers`, `honua_query_features`, `honua_describe_layer`, `honua_render_map`, style tools) | Metadata v2 graph — and, for query/render, a feature reader / raster renderer — composed by the data provider | Advertised | Omitted in compositions without a data provider |
| Dataset ingest (`honua_ingest_dataset`) | Import service (`IFileImportService`) composed | Advertised | Omitted without an import-capable provider |
| Platform-ops observability + deploy tools (`honua_ops_health`, `honua_ops_findings`, `honua_deploy_operations`, …) | Ops-observability / platform-ops readers composed | Advertised | Omitted in minimal hosts |

The `honua_plan_analysis`, `honua_validate_plan`, `honua_dry_run_plan`,
`honua_execute_plan`, `honua_cancel_job`, `honua_list_jobs`, grounding, and
`honua_list_capabilities` tools, plus the job/workspace/process-catalog/feature-catalog
resources, are advertised in **every** composition — they depend only on the job
runtime and embedded catalogs that are always present. No feature-edit tool is ever
advertised, in any profile, by design (ADR-0028).

## Next steps

- [Operating Honua](../operate/README.md)
- [Turn on the live MCP planner (Honua-brings-LLM)](mcp-live-planner.md)
- [Run geoprocessing](../query-analyze/run-geoprocessing.md)
- [Authentication](../secure/authentication.md)
- [Protocol overview](../../concepts/protocols.md)
