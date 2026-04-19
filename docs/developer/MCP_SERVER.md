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
- **Wire**: JSON-RPC 2.0
- **Methods**: `initialize`, `tools/list`, `tools/call`, `resources/list`, `resources/read`

Each HTTP POST carries exactly one JSON-RPC request; batch framing is
deferred. Responses always use HTTP 200 — JSON-RPC errors are returned in
the response envelope's `error` object rather than as HTTP status codes.
`tools/call` and `resources/read` require an authenticated principal and
return an `unauthenticated` error otherwise; `initialize`, `tools/list`,
and `resources/list` are handshake methods that do not require
authentication.

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
  returns descriptors sorted by resource URI.
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
  "method": "initialize"
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

### Tools

| Tool | Status | Domain delegate | Workflow family |
|------|--------|-----------------|-----------------|
| `honua_validate_plan` | functional | `IGeoprocessingJobService.ValidatePlan` | `planning` |
| `honua_dry_run_plan` | functional | `IGeoprocessingJobService.DryRunPlan` | `planning` |
| `honua_execute_plan` | functional | `IGeoprocessingJobService.SubmitJobAsync` | `execution` |
| `honua_cancel_job` | functional | `IGeoprocessingJobService.CancelJobAsync` | `lifecycle` |
| `honua_plan_analysis` | contract stub | blocked by `honua.planner.service` | `planning` |
| `honua_ground_candidates` | contract stub | blocked by `honua.grounding.service` | `planning` |
| `honua_clarify_intent` | contract stub | blocked by `honua.clarifier.service` | `planning` |

Stub tools still enforce authentication and return a structured
`not_implemented` envelope with `blockedBy`, `contract`, and `nextSteps`
fields so operators can bind today and pick up behavior when the upstream
service lands.

#### Tool payload notes

- `honua_validate_plan` and `honua_dry_run_plan` accept `{ "plan": ... }`.
  The published schema requires `plan.steps[*].stepId` and `kind`.
  Unsupported step kinds or output artifact kinds fail with
  `invalid_argument` before the domain service is invoked.
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
- `honua_plan_analysis`, `honua_ground_candidates`, and
  `honua_clarify_intent` accept `{}` and return
  `{ status: "not_implemented", tool, blockedBy, contract, nextSteps }`.

### Resources

| URI template | Status | Description |
|--------------|--------|-------------|
| `honua://jobs/{jobId}` | functional | Job lifecycle record — status, phase, percent complete, warnings, link to results |
| `honua://jobs/{jobId}/results` | functional | Delegates to `IGeoprocessingJobService.GetJobResultsAsync`. Enforces auth and terminal-state preconditions, and returns the `AnalysisResultPackage` envelope when a stored package exists. |
| `honua://workspaces/{workspaceId}` | contract stub | Stable template pending workspace store |
| `honua://catalog/processes` | contract stub | Stable URI pending catalog service |

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

### Error envelope

Domain exceptions are translated by `McpErrorMapper` into a JSON-RPC error
object whose `data` field mirrors the gRPC status vocabulary:

| Exception | `data.code` | JSON-RPC code | Extra signal |
|-----------|-------------|---------------|--------------|
| `GeoprocessingAuthorizationException(requiresAuthentication: true)` | `unauthenticated` | `-32000` | `requiresReauthentication: true` |
| `GeoprocessingAuthorizationException(false)` | `permission_denied` | `-32000` | |
| `GeoprocessingApprovalRequiredException` | `failed_precondition` | `-32000` | `approvalRequired: true`, `policyRef` |
| `GeoprocessingPreconditionFailedException` | `failed_precondition` | `-32000` | |
| `GeoprocessingNotFoundException` | `not_found` | `-32000` | |
| `GeoprocessingValidationException` | `invalid_argument` | `-32602` | |
| `GeoprocessingStoreUnavailableException` | `unavailable` | `-32000` | `retryable: true` |
| `GeoprocessingIdempotencyConflictException` | `already_exists` | `-32000` | |
| anything else | `internal` | `-32000` | |

Clients can drive retry, re-auth, and approval flows from `data.code`
without parsing human-readable strings.
Malformed JSON, missing JSON-RPC fields, missing tool/resource parameters,
and unsupported plan enums surface as `invalid_argument`; unknown methods,
tool names, and resource URIs surface as `not_found`.

### Telemetry

- `honua.mcp.tool.call` — emitted today, tagged by `tool_name`, `status`, `workflow_family`
- `honua.mcp.resource.read` — emitted today, tagged by `resource_family`, `status`
- `honua.mcp.boundary.rejection` — reserved counter tagged by `rejection_reason` for future taxonomy non-goal rejections

Activities emitted inside the surface are tagged with
`honua.protocol = "Mcp"` so spans roll up alongside gRPC and GPServer
traffic. Stub tools still increment `honua.mcp.tool.call` with `status = "ok"`
because they return structured `not_implemented` payloads rather than
JSON-RPC errors.

### Source

- Vertical slice: `src/Honua.Server/Features/Mcp/`
- Tools: `src/Honua.Server/Features/Mcp/Tools/`
- Resources: `src/Honua.Server/Features/Mcp/Resources/`
- Tests: `tests/Honua.Server.Tests/Features/Mcp/`
