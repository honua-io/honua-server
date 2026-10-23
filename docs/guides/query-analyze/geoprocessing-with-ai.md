---
type: guide
title: "Geoprocessing with AI"
description: "Let an MCP agent discover GP functions, validate a plan, execute it, and inspect the actual job results on a local or deployed Honua server."
---
# Geoprocessing with AI

An AI client uses Honua's MCP tools to discover processes and submit plans to the
same GP runtime used by OGC API Processes and GPServer. The client model chooses
the steps; Honua validates inputs, checks access, runs the job, and records results.

Start the [local GP stack](gp-local-dev-quickstart.md), then connect an MCP client
to `http://localhost:8080/mcp` using the [connection guide](../connect/ai-agents-mcp.md).
The sample below uses inline geometry, so it needs no published input layer.

For an agent working directly in a developer checkout, the
[devkit](gp-devkit-authoring.md) provides `list --json`, `describe --json`, and
`doctor --managed-only --json`. Catalog registration does not prove dependencies
are installed. A doctor result verifies only its reported checks; a successful
`run` and inspected output are execution evidence.

Custom Python/.NET tools can use the [local debugging sample](../../../samples/gp-custom-local/README.md).
Its receipts and progress events remain local and unsanitized. These local tools
do not add MCP capabilities or authorize a remote deployment. Discover the live
MCP surface before proposing remote inspection, retries, or deployment actions.

## Access and execution prerequisites

- Authenticate tool calls and resource reads. The local stack's admin key is
  `quickstart-admin-password`; use the configured API key or bearer identity elsewhere.
- Process execution needs `Process.Execute`. Mutating processes and custom code
  require their additional grants. Approval requirements depend on the operation
  and configured policy; the bounded buffer below is non-destructive.
- MCP execution requires `ai.spec-apply` and the applicable agent-operation
  entitlements. With license enforcement enabled, this is a Pro-or-higher path;
  Community can validate plans. The local stack supplies its Development grant.
- Async jobs need Redis. Native processes also need a matching heavy worker.

## 1. Discover the live tool and process surface

Have the client call `tools/list` and read `honua://catalog/processes` with
`resources/read`. Select a process whose `executionKind` is `Job` and whose
`supportedExecutionModes` includes `Async`. Inspect its parameter names, types,
allowed values, and output kinds before constructing inputs.

Large catalog reads can span several text chunks. Follow `nextCursor` as
`params.cursor`, concatenate the text for each resource URI, and parse the
complete document only after the last chunk.

The shipped execution tools are `honua_validate_plan`, `honua_dry_run_plan`,
`honua_execute_plan`, `honua_list_jobs`, and `honua_cancel_job`. Use the live
schemas to determine their arguments. The direct analysis verb `buffer_features`
is not shipped; configuring an analysis profile does not implement it.

The default tool view is deliberately small and can omit cancellation. Discover
views with `honua_list_capabilities`, then request `tools/list` with
`params.view: "analyze"` when that view is advertised. Check that cancellation
is available before starting a job that may need it.

If using `honua_plan_analysis`, inspect its `engine` field. `engine: "fixture"`
means a demonstration template, not a plan compiled from the user's request.
In that case the client model must construct a plan from the catalog and validate
it. A successful planning call is not evidence that GP has executed.

## 2. Validate a concrete plan

Pass the following **tool arguments** to `honua_validate_plan`. A copy is checked
in as [buffer-plan.json](../../../samples/gp-local-dev/buffer-plan.json).

```json
{
  "plan": {
    "planId": "gp-local-buffer-500m",
    "steps": [
      {
        "stepId": "buffer-point",
        "kind": "Geoprocess",
        "processId": "geometry.buffer",
        "inputs": {
          "wkb": "AQEAAAAAAAAAAAAAAAAAAAAAAAAA",
          "srid": "3857",
          "distance": "500",
          "geodesic": "false"
        },
        "dependsOn": []
      }
    ],
    "outputs": ["FeatureLayer"]
  }
}
```

This buffers `POINT(0 0)` by 500 metres. MCP's plan input map contains
string-encoded values, including base64 WKB. The OGC geometry input also accepts
a GeoJSON geometry object; do not transfer that object unchanged into the MCP
string-valued map.

Require `isExecutable: true`. Inspect validation errors and use
`honua_dry_run_plan` to inspect the plan before submitting it. Dry-run success
does not replace checking the executed result.

## 3. Execute and follow the job

Pass the same `plan` to `honua_execute_plan`. For retry-safe submission, supply
an `idempotencyKey` unique to this logical request and retain it across retries.

The response includes `jobId` and `resourceUri`. Read that returned resource with
`resources/read` until status is `Succeeded`, `Failed`, or `Cancelled`. Use a
bounded wait; on timeout inspect the job or request cancellation with
`honua_cancel_job` rather than submitting duplicates. Progress notifications can
supplement polling when supported by the client session.

On success, read `honua://jobs/{jobId}/results`. Inspect the returned `artifacts`
and retain their actual identifiers and references. Check the geometry: this
example should have bounds `[-500, -500, 500, 500]` in EPSG:3857 and area within
1% of `π × 500²` square metres. A job ID or a successful tool call alone does not
prove the requested analysis completed correctly.

A useful agent instruction is:

> Discover the available GP tools and process schema. Validate and execute the
> checked-in 500-metre buffer plan. Wait for a terminal job state, inspect the
> result geometry, and report the job ID, artifact reference, CRS, bounds, and
> whether the result matches the requested distance. Report failures explicitly.

## 4. Consume results or continue into a map

| Client surface | Result shape |
| --- | --- |
| MCP job results resource | Canonical result package with `artifacts` |
| Python GP `results(...)` | OGC output document, such as `outputFeatureLayer.value` |
| JavaScript `run.results()` | Result wrapper with the OGC document under `outputs` |

OGC qualified outputs carry `value` and `mediaType`, or a referenced `href`.
They do not acquire MCP artifact `artifactId` or `kind` fields merely because an agent
requested them. Follow the returned reference using the authenticated product
surface, and preserve the job and artifact provenance.

MCP artifacts identify themselves with `artifactId` and carry their reference in
`uri`. An inline `data:` URI contains the artifact itself; decode it according to
its media type and encoding before inspecting the geometry.

This sample's coordinates are metric. Before importing into a map that expects
WGS 84 GeoJSON, reproject to EPSG:4326; do not relabel the CRS. A completed
built-in artifact that is inline GeoJSON has no table binding. Import it with
`honua_ingest_job_artifact` (`kind` `server-job-artifact`, the job and artifact
identities, and `targetSrid`). The server reads the artifact it stored. Do not
paste the GeoJSON into the prompt, invent a browser or local-run source, or
pass a server path. A known producer CRS is kept or transformed to `targetSrid`.
`honua_ingest_dataset` is the separate inline CSV/GeoJSON path, capped at 4 MB.
`honua_publish_result` still requires a materialized table (`connectionId`,
`schema`, `table`) and refuses this inline artifact. Publish the imported table
with `honua_publish_service`, then query that layer. See [Honua Studio](../../studio/README.md)
for the map application path.

For a Python-only agent, the [SDK quickstart](gp-local-dev-quickstart.md#2-submit-and-verify-a-buffer)
is an alternative to MCP. Native functions use the same discovery and job
lifecycle after a native worker is available. AWS Batch and Kubernetes are
execution targets selected by deployment configuration, not different agent tools.
Azure remains experimental; it is outside this local/RC journey.

## Troubleshoot

| Symptom | Action |
| --- | --- |
| Authentication or authorization error | Check identity, process grants, and MCP execution entitlements. |
| `expected base64-encoded WKB` | Use the catalog's base-plan string encoding; GeoJSON objects belong on the OGC geometry input. |
| `engine: "fixture"` | Build a real plan from the catalog; do not treat the template as an intent-specific result. |
| Async substrate unavailable | Check Redis and the matching worker. |
| Job `Failed` or `Cancelled` | Inspect the recorded error; do not present its artifacts as a successful analysis. |
| Missing `buffer_features` | Use the shipped plan tools. |
