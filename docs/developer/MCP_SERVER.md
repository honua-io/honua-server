# MCP Server

Honua ships an MCP server package in the `honua-sdk-js` repository (path `mcp`, package `@honua/mcp-server`) so AI clients can safely discover services, inspect layer schema, and run filtered geospatial queries.

That SDK-hosted package is the current focused discovery/query MCP surface.
The forward-looking AI operator MCP surface for planning, execution,
publishing, packaging, and deployment is owned canonically by `honua-server`
as the server-side implementation of the `geospatial-mcp` standard. The SDK
MCP package may proxy or federate that server-owned surface later, but it is
not the semantic source of truth for operator workflows.

This document covers both the SDK-hosted discovery/query surface and, in
[Operator Surface](#operator-surface), the server-owned operator surface that
implements the taxonomy defined in the archived
[AI Operator Contract](../archive/developer/AI_OPERATOR_CONTRACT.md).

For the archived AI-first analyst and builder design notes, see:

- [AI Operator Contract](../archive/developer/AI_OPERATOR_CONTRACT.md)
- [Deterministic Operator Workflow Results](../archive/developer/DETERMINISTIC_OPERATOR_WORKFLOW_RESULTS.md)

## Capabilities

- Service discovery: list available services and metadata
- Layer introspection: fetch field/schema details for a layer
- Query workflows: query features, counts, extents, and statistics
- Transport choice: `grpc-web` (default) or `rest`

## Runtime Configuration

Set these environment variables before launching the MCP server:

- `HONUA_BASE_URL` (required): absolute server URL, for example `https://honua.example.com`
- `HONUA_TRANSPORT` (optional): `grpc-web` (default) or `rest`
- `HONUA_API_KEY` (optional): API key if your deployment requires it
- `HONUA_TIMEOUT_MS` (optional): request timeout in milliseconds (default `30000`)
- `HONUA_RETRY_MAX_RETRIES` (optional): retry attempts for transient failures (default `2`)

When `HONUA_API_KEY` is set, use `https://` for non-localhost servers.

## Source Repository

- GitHub: `https://github.com/honua-io/honua-sdk-js`
- Package path: `mcp/`

## Run

```bash
git clone https://github.com/honua-io/honua-sdk-js.git
cd honua-sdk-js/mcp
npm ci
npm run build
HONUA_BASE_URL="https://honua.example.com" HONUA_TRANSPORT="grpc-web" node dist/src/index.js
```

## Exposed MCP Tools

- `honua_list_services`
- `honua_describe_layer`
- `honua_query_features`
- `honua_count_features`
- `honua_get_extent`
- `honua_statistics`

## Exposed MCP Resources

- `honua://services`
- `honua://services/{encodedServiceId}/layers/{layerId}`

## Certification

> **Not yet active.** The server-side CI jobs and seed data are landed, but the SDK-side certification scripts (`test:certification`, `test:certification:artifact`, `test:llm-smoke`) are not yet present in `honua-sdk-js` `trunk`. Until those scripts land, the CI jobs skip with a warning annotation and **no certification artifacts are produced**. See [Known gaps](#known-gaps).

Once the SDK-side scripts are available, Honua's CI will run an MCP certification lane on every push and pull request to `trunk` (and on manual dispatch). The suite exercises all 6 tools and 2 resources across both `grpc-web` and `rest` transports. When the `test:certification:artifact` script is also present, the lane produces machine-readable (JSON) and human-readable (Markdown) evidence artifacts; if only `test:certification` is landed, tests run but a CI warning notes the missing artifacts.

| Area | What is tested |
|------|----------------|
| Tools | `honua_list_services`, `honua_describe_layer`, `honua_query_features`, `honua_count_features`, `honua_get_extent`, `honua_statistics` |
| Resources | `honua://services`, `honua://services/{encodedServiceId}/layers/{layerId}` |
| Transports | `grpc-web`, `rest` (CI matrix, one run each) |
| Cross-cutting | Auth (skipped under dev-auth — see [Known gaps](#known-gaps)), timeout (`HonuaTimeoutError`), retry (429/5xx backoff), failure cases (bad serviceId, bad layerId, invalid WHERE) |

Certification artifacts are uploaded per-transport as `mcp-certification-{transport}` with 30-day retention. These are separate from the [Client Template Version Matrix](../gis/CLIENT_TEMPLATE_VERSION_MATRIX.md).

A non-blocking LLM smoke lane runs after certification passes, connecting OpenAI `gpt-4o` to the MCP server to prove the interface is usable by an actual agent. Smoke transcripts are stored in a separate `mcp-llm-smoke-transcripts` artifact.

See [MCP Certification](../contributor/mcp-certification.md) for contributor guidance on seed data, CI jobs, and test structure.

### Known gaps

- Auth certification is skipped when `HONUA_DEV_AUTH=true` (CI default). Full auth certification requires a non-dev-auth server lane.
- Cache-invalidation testing deferred until anonymous writes are available in dev auth mode.
- C# SDK interop lane deferred to follow-up work.
- Certification test code lives in `honua-sdk-js`. The SDK ref is controlled by the `MCP_SDK_REF` env var in `ci.yml`. When set to a branch name, certification is useful for development but artifacts are not reproducible release evidence. Pin `MCP_SDK_REF` to a specific tag or commit SHA for release-grade certification; `workflow_dispatch` `sdk_ref` overrides for one-off replays.
- MCP certification and LLM smoke jobs skip cleanly (with a CI warning annotation) when the required `test:certification` / `test:llm-smoke` scripts are not yet present in the checked-out SDK ref.

## Notes

- Prefer `grpc-web` for performance when available.
- Use server-side auth controls (API key or OIDC) exactly as you do for direct client calls.

## Operator Surface

The server-owned operator surface lives in `src/Honua.Server/Features/Mcp/`
and implements the geospatial-mcp taxonomy described in the archived
[AI Operator Contract](../archive/developer/AI_OPERATOR_CONTRACT.md#mcp-contract-families).
It is a thin adapter over `IGeoprocessingJobService` — the same transport-neutral
domain service the gRPC `ProcessService` and the GeoServices `GPServer`
adapter delegate to — so every protocol enforces identical authorization and
validation rules.

### Endpoint

- **Route**: `POST /mcp`
- **Wire**: JSON-RPC 2.0 (single requests and batches)
- **Methods**: `initialize`, `notifications/initialized`, `tools/list`, `tools/call`, `resources/list`, `resources/templates/list`, `resources/read`

Request responses use HTTP 200 with a JSON-RPC envelope — protocol-level
JSON-RPC errors are returned in the response envelope's `error` object
rather than as HTTP status codes. MCP notifications (a `notifications/*`
method without an `id`) instead return HTTP 202 Accepted with an empty
body, as required by the MCP HTTP transport. A non-`notifications/*`
method that omits `id`, or a malformed envelope the server cannot
deserialize, is surfaced as `invalid_request` (`-32600`) with `id: null`
rather than silently accepted — clients need an explicit signal when
their payload is rejected, and the `notifications/*` prefix is the only
form MCP 2025-03-26 treats as a notification.
`tools/call` and `resources/read` require an authenticated principal and
return an `unauthenticated` error otherwise. The authentication gate runs
*before* param parsing, tool-name matching, or resource-URI matching, so
anonymous callers see the `unauthenticated` signal even when their payload
is malformed or targets an unknown tool/URI — this keeps reauth the single
recoverable path instead of masking it with `invalid_argument` or
`not_found`. `tools/call` surfaces the signal through the isError envelope
(`result.isError: true`, `result.structuredContent.code: "unauthenticated"`)
to stay consistent with the MCP 2025-03-26 tool-execution error contract;
`resources/read` surfaces it as a JSON-RPC error. `initialize`,
`tools/list`, `resources/list`, and `resources/templates/list` are
handshake methods that do not require authentication.

#### Request framing and ids

Each HTTP POST carries either a single JSON-RPC request object or a
JSON-RPC 2.0 batch array. Batch handling follows §6 of the spec:

- An empty batch array is itself an invalid request; the server responds
  with a single `invalid_request` response object whose `id` is `null`.
- Each non-object element in the batch produces one `invalid_request`
  response in the reply array with `id: null`.
- Notifications in the batch are processed but do not produce a response
  entry. If every element was a notification, the server returns HTTP 202
  with no body instead of an empty JSON array.
- Mixed batches return a JSON array whose length equals the number of
  non-notification requests.
- Per MCP 2025-03-26 lifecycle, the `initialize` request MUST NOT be part
  of a JSON-RPC batch. If any element of the batch carries
  `"method": "initialize"`, the server rejects the entire batch with a
  single `invalid_request` response object whose `id` is `null`.

Per MCP 2025-03-26 a JSON-RPC request `id` MUST be either a string or an
integer. Explicit `null`, booleans, fractional numbers, arrays, and objects
are rejected with `invalid_request` (`-32600`). An absent `id` only marks
the message as a notification when the method carries the
`notifications/*` prefix — a non-`notifications/*` method that omits
`id` is a malformed request and is rejected with `invalid_request`
(`id: null`) rather than silently accepted. Conversely, a
`notifications/*` message that carries an `id` is also rejected with
`invalid_request`; notifications MUST NOT include an `id` field.

### Lifecycle and version negotiation

The MCP lifecycle is `initialize` (request/response) followed by
`notifications/initialized` (notification, no response):

1. **`initialize`** — the client MUST send `params` containing
   `protocolVersion`, `capabilities`, and `clientInfo.name`. Missing or
   non-object `capabilities`, blank `protocolVersion`, or missing
   `clientInfo.name` returns `invalid_argument`. The `initialize` request
   MUST NOT be part of a JSON-RPC batch; see
   [Request framing and ids](#request-framing-and-ids).
2. The server negotiates the protocol revision: if the client's
   `protocolVersion` matches one the server supports it is echoed back;
   otherwise the server replies with its latest supported revision so the
   client can decide whether to continue. The current build supports
   `2025-03-26`.
3. **`notifications/initialized`** — once the client accepts the negotiated
   version it sends this notification (no `id`). The server returns
   HTTP 202 with no body. Unknown `notifications/*` methods are also
   accepted silently so forward-compatible clients can layer in new
   notification types.

### Response Framing

- Successful JSON-RPC responses omit `error`; failures omit `result`.
- Payloads use camelCase and omit `null` properties.
- `initialize` returns:
  - `protocolVersion: "2025-03-26"`
  - `serverInfo.name: "honua.operator.mcp"`
  - `serverInfo.version: "v1"`
  - `capabilities.tools.listChanged: false`
  - `capabilities.resources.listChanged: false`
- Successful `tools/call` responses return the same payload twice:
  - `result.structuredContent` contains the typed JSON object
  - `result.content[0].text` contains that same JSON serialized as text
- Successful `resources/read` responses return `result.contents`, which
  currently contains one `application/json` block whose `text` field is the
  serialized resource body.
- `tools/list` returns descriptors sorted by tool name; `resources/list`
  returns descriptors sorted by resource URI. Parameterized resource URIs
  (for example `honua://jobs/{jobId}`) appear only on
  `resources/templates/list` under `result.resourceTemplates[*].uriTemplate`
  per MCP 2025-03-26; `resources/list` exposes only concrete, directly
  addressable URIs.
- Tool descriptors expose a static `inputSchema` JSON Schema document.
  Resource descriptors currently all advertise `mimeType: "application/json"`.

Example `tools/call` success shape:

```json
{
  "jsonrpc": "2.0",
  "id": "run-1",
  "result": {
    "content": [
      {
        "type": "text",
        "text": "{\"jobId\":\"job-xyz\",\"status\":\"Queued\",\"createdAt\":\"2026-04-18T12:00:00+00:00\",\"resourceUri\":\"honua://jobs/job-xyz\"}"
      }
    ],
    "isError": false,
    "structuredContent": {
      "jobId": "job-xyz",
      "status": "Queued",
      "createdAt": "2026-04-18T12:00:00+00:00",
      "resourceUri": "honua://jobs/job-xyz"
    }
  }
}
```

Example `tools/call` execution-failure shape (tool-level error inside
`result`, not a JSON-RPC protocol error):

```json
{
  "jsonrpc": "2.0",
  "id": "run-1",
  "result": {
    "content": [
      {
        "type": "text",
        "text": "{\"status\":\"error\",\"code\":\"invalid_argument\",\"message\":\"Cancel-job requires a non-empty jobId.\"}"
      }
    ],
    "isError": true,
    "structuredContent": {
      "status": "error",
      "code": "invalid_argument",
      "message": "Cancel-job requires a non-empty jobId."
    }
  }
}
```

Example `resources/read` success shape:

```json
{
  "jsonrpc": "2.0",
  "id": "job-1",
  "result": {
    "contents": [
      {
        "uri": "honua://jobs/job-xyz",
        "mimeType": "application/json",
        "text": "{\"jobId\":\"job-xyz\",\"status\":\"Running\",\"createdAt\":\"2026-04-18T12:00:00+00:00\",\"updatedAt\":\"2026-04-18T12:02:00+00:00\",\"percentComplete\":50.5,\"currentPhase\":\"executing\",\"warnings\":[\"layer projection assumed\"],\"resultsUri\":\"honua://jobs/job-xyz/results\"}"
      }
    ]
  }
}
```

### Request examples

Initialize the MCP session and discover server capabilities:

```json
{
  "jsonrpc": "2.0",
  "id": "hello-1",
  "method": "initialize",
  "params": {
    "protocolVersion": "2025-03-26",
    "capabilities": {},
    "clientInfo": {
      "name": "honua-operator-cli",
      "version": "1.0.0"
    }
  }
}
```

Acknowledge the negotiated session before issuing any other calls. The
server returns HTTP 202 with no body:

```json
{
  "jsonrpc": "2.0",
  "method": "notifications/initialized"
}
```

Submit a plan for execution:

```json
{
  "jsonrpc": "2.0",
  "id": "run-1",
  "method": "tools/call",
  "params": {
    "name": "honua_execute_plan",
    "arguments": {
      "plan": {
        "planId": "plan-1",
        "steps": [
          {
            "stepId": "buffer-1",
            "kind": "Geoprocess",
            "processId": "geometry.buffer",
            "inputs": {
              "wkb": "AQEAAAAAAAAAAAAAAAAAAAAAAAAA",
              "srid": "4326",
              "distance": "100"
            }
          }
        ],
        "outputs": [
          "FeatureLayer",
          "Map"
        ]
      },
      "idempotencyKey": "plan-1-exec"
    }
  }
}
```

Read the job lifecycle resource returned by `honua_execute_plan`:

```json
{
  "jsonrpc": "2.0",
  "id": "job-1",
  "method": "resources/read",
  "params": {
    "uri": "honua://jobs/job-xyz"
  }
}
```

Submit a batch — two tool calls in a single HTTP request. The server
returns a JSON array with one response per non-notification element:

```json
[
  {"jsonrpc": "2.0", "id": 1, "method": "tools/list"},
  {"jsonrpc": "2.0", "id": "a", "method": "resources/templates/list"}
]
```

List parameterized resource URIs:

```json
{
  "jsonrpc": "2.0",
  "id": "tpl-1",
  "method": "resources/templates/list"
}
```

### Tools

| Tool | Status | Domain delegate | Workflow family |
|------|--------|-----------------|-----------------|
| `honua_validate_plan` | functional | `IGeoprocessingJobService.ValidatePlan` | `planning` |
| `honua_dry_run_plan` | functional | `IGeoprocessingJobService.DryRunPlan` | `planning` |
| `honua_execute_plan` | functional | `IGeoprocessingJobService.SubmitJobAsync` | `execution` |
| `honua_cancel_job` | functional | `IGeoprocessingJobService.CancelJobAsync` | `lifecycle` |
| `honua_plan_analysis` | contract stub | blocked by `honua.planner.service` | `planning` |
| `honua_ground_candidates` | functional | `IGroundingService.GroundAsync` | `planning` |
| `honua_clarify_intent` | functional | `IGroundingService.GroundAsync` | `planning` |

Stub tools still enforce authentication and the same operator-grant
authorization as their functional counterparts (via
`IGeoprocessingJobService.EnsureCallerAuthorized`) before returning a
structured `not_implemented` envelope with `blockedBy`, `contract`, and
`nextSteps` fields so operators can bind today and pick up behavior when
the upstream service lands. Authenticated callers without the required
grant receive a `permission_denied` error, matching the functional tools.
The grounding tools delegate to `IGroundingService`, which layers
catalog discovery on top of the same authorization graph via
`IOperatorAuthorizationEvaluator` — see
[GROUNDING.md](GROUNDING.md) for the full pipeline.

#### Tool payload notes

- `honua_validate_plan` and `honua_dry_run_plan` accept `{ "plan": ... }`.
  The validator is designed to report `EMPTY_PLAN_ID` and `EMPTY_STEPS` as
  structured violations, so the published schema for these tools does not
  require `plan.planId` or `plan.steps` at the top level. Steps that *are*
  supplied must still provide `stepId` and `kind`; unsupported step kinds
  or output artifact kinds fail with `invalid_argument` before the domain
  service is invoked.
- `honua_execute_plan` publishes a stricter variant of the same schema:
  `plan.planId` and `plan.steps` are required and `plan.steps` carries
  `minItems: 1`, matching the server-side guard in
  `SubmitJobAsync`. Schema-driven MCP clients therefore block execute
  payloads the server would always reject while still being able to submit
  partial plans to the validator.
- `plan.steps[*].kind` accepts the canonical step kinds
  `QueryFeatures`, `Geoprocess`, `Aggregate`, `RenderMap`, and `Export`
  (case-insensitive).
- `plan.outputs[*]` accepts the canonical artifact kinds `Scalar`,
  `FeatureLayer`, `Table`, `Raster`, `File`, `Report`, `Map`, and
  `AppBundle` (case-insensitive).
- `honua_validate_plan` returns
  `{ isExecutable, requiresApproval, violations, warnings }`.
- `honua_dry_run_plan` returns
  `{ estimatedDurationSeconds, estimatedArtifacts, sideEffects }`.
- `honua_execute_plan` accepts an optional `idempotencyKey`. Blank or
  whitespace keys are normalized to `null` before delegation. Success returns
  `{ jobId, status, createdAt, resourceUri }`.
- `honua_cancel_job` accepts `{ jobId }`. Blank job ids fail with
  `invalid_argument`. Success returns
  `{ jobId, status: "cancellation_requested", cancellationRequested: true }`.
- `honua_plan_analysis` accepts `{}` and returns
  `{ status: "not_implemented", tool, blockedBy, contract, nextSteps }`.
- `honua_ground_candidates` accepts
  `{ goal, workflowFamilyHint?, constraints?, explicitInputs?, assumptionPolicy?, context?, intentId? }`
  and returns
  `{ workflowFamily, draftIntent, candidates, clarification?, engine }`.
  `workflowFamily.value` is one of `Analyze`, `PublishData`, `BuildApp`,
  `AutomateDeploy`. `draftIntent` carries a canonical intent envelope with
  optional `analysis` and `publishing` blocks per the drafted family.
- `honua_clarify_intent` accepts the same shape plus a required
  `{ intentId, response: { answers: Record<string, string[]> } }`. The
  published schema also marks `goal` as required, matching the server
  mapper which rejects blank goals with `invalid_argument`. The tool
  requires the same `(Catalog, Discover)` authorization grant as
  `honua_ground_candidates` — both halves of the grounding flow
  delegate to `IGroundingService.GroundAsync`, so asymmetric permissions
  would let a caller start grounding but fail to answer its own
  clarification envelope. The service is stateless — callers carry
  `goal`, `constraints`, and `explicitInputs` forward across turns; the
  tool mapper copies `intentId` into both `request.intentId` and
  `response.intentId` so `IGroundingService` can enforce intent-id
  parity and reject answers targeting a different intent with
  `invalid_argument`. Answers are *applied* rather than merely
  acknowledged: `workflow_family` overrides the classifier
  (confidence `1.0`, evidence `clarification`), `dataset.selection` and
  `process.selection` pin the chosen candidate to the front of its
  ranking (unknown ids fail with `invalid_argument`), `publish.target`
  flows into the drafted `PublishIntent`, and `param.<name>` answers
  skip the matching parameter-gap clarification and surface as
  `param.<name>=<value>` entries on `provenance.assumptions`. See
  [GROUNDING.md](GROUNDING.md) for the material-ambiguity rule set and
  clarification reason codes.

### Resources

| URI / template | Surface | Status | Description |
|----------------|---------|--------|-------------|
| `honua://jobs/{jobId}` | `resources/templates/list` | functional | Job lifecycle record — status, phase, percent complete, warnings, link to results |
| `honua://jobs/{jobId}/results` | `resources/templates/list` | functional | Delegates to `IGeoprocessingJobService.GetJobResultsAsync`. Enforces auth and terminal-state preconditions, and returns the `AnalysisResultPackage` envelope when a stored package exists. |
| `honua://workspaces/{workspaceId}` | `resources/templates/list` | contract stub | Stable template pending workspace store |
| `honua://catalog/processes` | `resources/list` | contract stub | Stable URI pending catalog service |
| `honua://published-services` | `resources/list` | gated (opt-in) | Reads `IPublishedServiceStore`. Not advertised by the default composition; gated behind `AddMcpPromotionSurface` and canonical persistence. |
| `honua://published-services/{serviceId}` | `resources/templates/list` | gated (opt-in) | Reads `IPublishedServiceStore`; returns `not_found` when the record is absent. Payload shape is stable so clients can bind once the surface is wired. |
| `honua://deployments` | `resources/list` | gated (opt-in) | Reads `IDeploymentStore`. Not advertised by the default composition; gated behind `AddMcpPromotionSurface` and canonical persistence. |
| `honua://deployments/{deploymentId}` | `resources/templates/list` | gated (opt-in) | Reads `IDeploymentStore`; returns `not_found` when the record is absent. Payload shape is stable so clients can bind once the surface is wired. |
| `honua://map-packages` | `resources/list` | gated (opt-in) | Reverse lookup against `IDeploymentStore`. Not advertised by the default composition; gated behind `AddMcpPromotionSurface` and canonical persistence. |
| `honua://map-packages/{packageId}` | `resources/templates/list` | gated (opt-in) | Reverse lookup against `IDeploymentStore`; returns `not_found` when no currently-published deployment references the package. Packages have no standalone store; the view is derived from deployments that reference the package. |
| `honua://app-packages` | `resources/list` | gated (opt-in) | Reverse lookup against `IDeploymentStore`. Not advertised by the default composition; gated behind `AddMcpPromotionSurface` and canonical persistence. |
| `honua://app-packages/{packageId}` | `resources/templates/list` | gated (opt-in) | Reverse lookup against `IDeploymentStore`; returns `not_found` when no currently-published deployment references the package. |

The promotion-surface resources are functional handlers — they do not implement
`IStubMcpResource`, and when advertised the dispatcher tags successful reads as
`status=ok` on `honua.mcp.resource.read`, distinct from the
`status=not_implemented` emitted by true contract stubs such as
`honua://workspaces/{workspaceId}` and `honua://catalog/processes`. Handler
code, URIs, payload shapes, and authorization are fixed so agents and
`honua-devops-29` can integrate against the wire contract. The handlers are
not wired into the default composition today, because canonical
`IPublishedServiceStore` and `IDeploymentStore` persistence has not shipped
yet — wiring them against empty process-local state would advertise a URI
surface that returns nothing useful and risks masking the gap. Hosts that have
registered canonical persistence call `services.AddMcpPromotionSurface()`
after `AddMcpOperatorSurface()` to register the five promotion resource
handlers; `AddMcpPromotionSurface` does not register any fallback stores, so
an unwired composition cannot accidentally advertise an empty promotion
surface.

`honua://jobs/{jobId}/results` is the reserved output channel for the
map-package artifact. The wire shape is stable so clients can bind today;
`mapPackageId`, `artifacts`, `workspaceRefs`, and `provenance` will flow
through from `AnalysisResultPackage` when the execution engine exposes a
stored package. Until result storage lands, the canonical
`IGeoprocessingJobService.GetJobResultsAsync` implementation still returns
`not_found` after validating that the job exists and has reached a terminal
state, and the MCP resource mirrors that behavior rather than fabricating a
second lifecycle model.

#### Resource payload notes

- `honua://jobs/{jobId}` returns
  `{ jobId, status, createdAt, updatedAt, completedAt?, percentComplete?, currentPhase?, errorMessage?, warnings, resultsUri }`.
- `honua://jobs/{jobId}/results` returns
  `{ jobId, resultPackageId, status, summary, artifacts, workspaceRefs, mapPackageId?, appPackageId?, assumptions, provenance, errors }`
  when `GetJobResultsAsync` succeeds.
- `artifacts[*]` includes
  `{ artifactId, kind, label, uri?, contentType?, metadata }`.
- `workspaceRefs[*]` includes
  `{ workspaceId, kind, label, uri?, expiresAt?, resourceUri }`.
  `resourceUri` points back to `honua://workspaces/{workspaceId}` even when
  the backing workspace `uri` is an external storage URI.
- `provenance` includes
  `{ sources, processDefinitions, assumptions, clarificationsAsked, clarificationsAnswered, executedAt?, generatedArtifactIds }`.
- `errors[*]` includes `{ kind, message, stepId?, violations? }`.
- `honua://jobs/{jobId}/results` returns a JSON-RPC error instead of a stub
  envelope when the canonical job service rejects the request:
  `failed_precondition` for non-terminal jobs, `not_found` for missing jobs,
  and currently `not_found` for terminal jobs whose result package has not yet
  been stored.
- `notImplementedReason` is omitted on successful
  `honua://jobs/{jobId}/results` payloads; only stub resources emit it.
- `honua://workspaces/{workspaceId}` returns
  `{ workspaceId, kind, label, status: "not_implemented", notImplementedReason }`
  with nullable fields such as `uri` and `expiresAt` omitted until the
  workspace store lands.
- `honua://catalog/processes` returns
  `{ catalogVersion, status: "not_implemented", processes: [], notImplementedReason }`.

#### Promotion-surface payload notes

The published-service, deployment, and package resources share provenance
conventions, and published-service / deployment resources additionally carry
monotonic ETags so agents can poll those surfaces for lifecycle changes
without subscribing. A subscription surface is not in scope today; the
audit-trail plus monotonic ETag (where exposed) is the observability contract.

- **Authorization.** Every promotion-surface read goes through
  `IGeoprocessingJobService.EnsureCallerAuthorized`, which routes to
  `OperatorAuthorizationEvaluator`. The evaluator matches a grant's
  `service` against the `OperatorResourceType` enum via
  `Enum.TryParse`, so the accepted service tokens are the enum names
  (case-insensitive, no hyphens): `PublishedService`, `Deployment`, and
  `Package`. Published-service reads require `PublishedService` with
  `Read`; deployment reads require `Deployment` with `Read`; map/app
  package reads and the package list roots require `Package` with
  `Read`. The same vocabulary and grants cover the gRPC, GPServer, and
  MCP protocols.
- **ETag.** Published-service and deployment reads — both the detail views
  and the summary items returned inside `honua://published-services` /
  `honua://deployments` list envelopes — return a weak ETag of the form
  `W/"{updatedAtTicks:hex}-{status}"`. For deployments the timestamp is
  the maximum of `updatedAt` and the last `transitions[*].at`, so a new
  audit entry always advances the tag. Status is included so lifecycle
  flips (e.g. `Active` → `Suspended`) invalidate clients even when the
  record's timestamp clock hasn't advanced yet. Map/app package views,
  package summary items, and the list-root envelopes themselves do not
  carry an ETag: packages are derived from deployment reverse-lookups
  with no canonical lifecycle timestamp of their own, and list envelopes
  expose per-item ETags so agents poll the individual service or
  deployment resource rather than a rolled-up digest. Full MCP
  `notifications/resources/updated` is deferred.
- **Provenance edges** live under `provenance` on the detail views and
  mirror `McpHostedProvenance`: `originatingIntentId`, `resultPackageId`,
  `publishedServiceResourceUri`, and `supersededByDeploymentResourceUri`.
  Edges not applicable to the surface (e.g. no superseding deployment) are
  omitted. The full hosted-deployment set reachable from a published
  service or package is exposed on the view itself via `deploymentCount`
  and `deploymentResourceUris` rather than a single-parent edge, since
  there is no canonical single parent deployment.
- **Hosted-deployment lists.** `deploymentResourceUris` on published-service
  and package detail views is filtered to deployments whose
  `publicationState` is `Published` — the single state the Deployment
  lifecycle uses to mark a deployment as currently routable — and is sorted
  by resource URI (stable ordinal order) so reads are deterministic
  regardless of the store's reverse-lookup ordering. `deploymentCount`
  reflects the same filtered set. Draft, Scheduled, Provisioning, and
  RollingOut deployments are still in flight and therefore excluded;
  Retired, Superseded, Failed, and Cancelled deployments are terminal and
  therefore excluded. The same filter drives the list-root and detail
  contracts so index visibility and `not_found` decisions agree.
- **Active-only list roots.** The service and deployment list roots
  narrow past their store-side `ListActiveAsync` semantics (which excludes
  only decommissioned services / retired+superseded deployments) to the
  truly live subsets — `PublishedServiceStatus.Active` and
  `DeploymentPublicationState.Published`. Map/app package roots group over
  the same published-deployment projection, so a package backed only by
  Failed, Cancelled, or pre-serving deployments does not appear in the
  index and returns `not_found` when read directly.
- **Pagination.** List-root reads cap results at 50 items by default
  (max 200). When the canonical store returns more items, the response
  sets `truncated: true`; a scrolling cursor is not part of v1 — agents
  requiring full enumeration should filter server-side via the canonical
  publishing / deployment APIs. Items are ordinal-sorted by their
  identifier (`serviceId`, `deploymentId`, or package id) before the cap
  is applied so the truncated prefix is stable across calls regardless
  of the backing store's iteration order.

- `honua://published-services/{serviceId}` returns
  `{ serviceId, resourceUri, status, sourceKind, sourceId, targetKind, endpoint?, publishedAt, lastRefreshedAt?, updatedAt, etag, artifacts, warnings, deploymentCount, deploymentResourceUris, provenance }`.
  A service with no currently-published hosted deployments returns
  `deploymentCount: 0` and an empty `deploymentResourceUris`; the record
  still reads so agents can inspect suspended, refresh-failed, or
  in-provisioning services.
- `honua://published-services` returns
  `{ resourceUri, count, truncated, items: [{ serviceId, resourceUri, status, targetKind, updatedAt, etag }] }`.
- `honua://deployments/{deploymentId}` returns
  `{ deploymentId, resourceUri, status, publicationState, rolloutState, sourceKind, sourceId, sourceResourceUri?, targetId, targetKind, hostingMode, environment?, routePrefix?, publicUrl?, runtimeHealth, createdAt, updatedAt, activatedAt?, retiredAt?, failureReason?, etag, transitions, provenance }`.
  `transitions[*]` includes `{ from, to, at, rolloutState?, reason? }` in
  append-only order.
- `honua://deployments` returns
  `{ resourceUri, count, truncated, items: [{ deploymentId, resourceUri, status, publicationState, sourceKind, targetId, updatedAt, etag }] }`.
- `honua://map-packages/{packageId}` and
  `honua://app-packages/{packageId}` return
  `{ packageKind, packageId, resourceUri, deploymentCount, deploymentResourceUris, provenance }`.
  Packages have no standalone record on the server; the read is a reverse
  lookup against the deployment store. A package that is referenced by no
  currently-published deployment (`publicationState = Published`) returns
  `not_found` — Failed, Cancelled, Draft, Scheduled, Provisioning, and
  RollingOut deployments do not satisfy the visibility contract.
- `honua://map-packages` and `honua://app-packages` return
  `{ resourceUri, packageKind, count, truncated, items: [{ packageId, resourceUri, deploymentCount }] }`.

### Error envelope

MCP 2025-03-26 distinguishes protocol-level errors from tool-execution
errors. Transport and dispatch failures surface as JSON-RPC errors in the
response envelope's `error` field; tool-execution failures (auth, approval,
validation, domain exceptions thrown inside a tool) are reported inside
`result` with `isError: true` and a structured `structuredContent` envelope
so clients can drive retry, re-auth, and approval flows without parsing
protocol-level errors.

#### Protocol-level errors (JSON-RPC `error`)

Returned on the JSON-RPC envelope for parse failures, framing errors,
unknown methods, unknown tools, unknown resource URIs, and for any failure
in `resources/read` (which has no result-level `isError` hook).

| Failure | `data.code` | JSON-RPC code |
|---------|-------------|---------------|
| Body is not valid JSON | `invalid_argument` | `-32700` |
| Body is valid JSON but not a valid JSON-RPC envelope (bad `jsonrpc`, invalid `id`, empty batch, non-object batch element) | `invalid_argument` | `-32600` |
| Unknown JSON-RPC method | `not_found` | `-32601` |
| Unknown tool name, missing tool name, or malformed `params` | `invalid_argument` | `-32602` |
| Unknown resource URI (`resources/read`) | `not_found` | `-32002` |
| `resources/read` — `GeoprocessingNotFoundException` | `not_found` | `-32002` |
| `resources/read` — `GeoprocessingValidationException` | `invalid_argument` | `-32602` |
| `resources/read` — other domain exception | see tool-execution table (data.code column) | `-32000` |

#### Tool-execution errors (`result.isError: true`)

Returned from `tools/call` when the tool raises a domain or auth exception.
The JSON-RPC envelope contains `result.isError: true`; the error envelope
is embedded in `result.structuredContent` (and mirrored in the `text` block
of `result.content`).

| Exception | `structuredContent.code` | Extra signals |
|-----------|--------------------------|---------------|
| `GeoprocessingAuthorizationException(requiresAuthentication: true)` | `unauthenticated` | `requiresReauthentication: true` |
| `GeoprocessingAuthorizationException(false)` | `permission_denied` | |
| `GeoprocessingApprovalRequiredException` | `failed_precondition` | `approvalRequired: true`, `policyRef` |
| `GeoprocessingPreconditionFailedException` | `failed_precondition` | |
| `GeoprocessingNotFoundException` | `not_found` | |
| `GeoprocessingValidationException` | `invalid_argument` | |
| `GeoprocessingStoreUnavailableException` | `unavailable` | `retryable: true` |
| `GeoprocessingIdempotencyConflictException` | `already_exists` | |
| `GroundingException(EmptyGoal \| UnsupportedWorkflowFamily)` | `invalid_argument` | |
| `GroundingException(UnknownIntent)` | `not_found` | |
| `GroundingException(CatalogUnavailable)` | `unavailable` | `retryable: true` |
| anything else | `internal` | |

`structuredContent` always includes `status: "error"`, `code`, and
`message`; optional fields (`requiresReauthentication`, `approvalRequired`,
`policyRef`, `conflictingJobId`, `retryable`, `violations`) are present
only when the domain signal applies. Stub tools that return
`not_implemented` remain a successful tool result — they are not routed
through this error path.

### Telemetry

- `honua.mcp.tool.call` — emitted today, tagged by `tool_name`, `status`, `workflow_family`
- `honua.mcp.resource.read` — emitted today, tagged by `resource_family`, `status`
- `honua.mcp.boundary.rejection` — reserved counter tagged by `rejection_reason` for future taxonomy non-goal rejections
- `honua.grounding.result` — emitted on every successful grounding pass, tagged by `engine`, `workflow_family`, and `clarified` (`"true"` / `"false"`), so the honua-server-734 eval harness can watch engine mix and clarification rate

The dispatcher tags the ambient activity with `honua.protocol = "Mcp"`
and `honua.operation = <method>` as soon as the JSON-RPC method has been
validated, so `initialize`, `tools/list`, `resources/list`, and
`resources/templates/list` spans — plus the anonymous auth short-circuits
in `tools/call` and `resources/read` — all roll up alongside gRPC and
GPServer traffic. Concrete tool and resource handlers override the
operation tag with their operation name (e.g. `ExecutePlan`, `GetJob`).

Contract-first stub tools and resources increment
`honua.mcp.tool.call` / `honua.mcp.resource.read` with
`status = "not_implemented"`, so dashboards can distinguish stubs from
functional paths without inspecting response bodies. Functional tools and
resources emit `status = "ok"` on success and `status = "error"` on
failure.

When the dispatcher rejects `tools/call` or `resources/read` for an
anonymous caller — before the tool or URI is resolved — it emits the
same counters with sentinel tag values so the auth-denial path stays
observable:

- `honua.mcp.tool.call` → `tool_name = "unknown"`, `status = "error"`, `workflow_family = "unknown"`
- `honua.mcp.resource.read` → `resource_family = "unknown"`, `status = "error"`

Both paths also emit `McpLog.AuthorizationDenied` with the JSON-RPC
method as the `target` and `authenticated = false`.

### Source

- Vertical slice: `src/Honua.Server/Features/Mcp/`
- Tools: `src/Honua.Server/Features/Mcp/Tools/`
- Resources: `src/Honua.Server/Features/Mcp/Resources/`
- Tests: `tests/Honua.Server.Tests/Features/Mcp/`
