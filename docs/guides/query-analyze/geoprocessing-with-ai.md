# Geoprocessing with AI

Set up Honua, configure and run geoprocessing through an AI agent, then turn the result into a saved map, app, or dashboard draft in Studio. This is the 2026.1 delivery arc: deployment and administration first, analysis second, saved experiences third.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)), an authenticated agent connected to [Honua MCP](../connect/ai-agents-mcp.md), and at least one published layer. For container and cloud setup, use [Docker Compose](../deploy/docker-compose.md) or [cloud deployments](../deploy/cloud-deployments.md).

> **Capability truth.** Honua has 98 built-in processes. OGC API Processes directly exposes the 86 non-source/non-sink built-ins plus the canonical plan runner. `geometry.*` and `conversion.geometry-format` support sync and async OGC execution; other processes are async. The six direct MCP analysis verbs are opt-in. Native GDAL/PDAL processes still require a native worker. AI does not bypass authorization, admission, edition checks, destructive-operation approval, or Studio publication review.

## 1. Enable the analysis profile

The base MCP profile remains the safe default. Add `analysis` explicitly when the agent should receive the direct geoprocessing verbs:

```yaml
services:
  honua-server:
    environment:
      Mcp__Profiles__0: base
      Mcp__Profiles__1: analysis
```

The `initialize` response then advertises `capabilities.profiles: ["analysis", "base"]`, and `tools/list` includes:

- `honua_buffer_features`
- `honua_overlay_features`
- `honua_summarize_statistics`
- `honua_reproject_features`
- `honua_join_features`
- `honua_export_dataset`

Restart the service after changing the profile. Keep Redis and the geoprocessing worker topology healthy for asynchronous jobs; add the native worker before selecting a process marked **native** in the [operation catalog](../../reference/geoprocessing-operations.md).

## 2. Prove the OGC geometry path

Before delegating to an agent, exercise the same job service through OGC API Processes. A GeoJSON geometry is accepted wherever the process catalog declares a WKB parameter. This sync-capable, single-output call defaults to synchronous execution and returns raw GeoJSON:

```bash
curl -sS -X POST \
  -H "X-API-Key: $HONUA_API_KEY" \
  -H "Content-Type: application/json" \
  http://localhost:8080/ogc/processes/processes/geometry.buffer/execution \
  -d '{
    "inputs": {
      "wkb": {"type":"Point","coordinates":[-122.4194,37.7749]},
      "srid": 4326,
      "distance": 0.005
    },
    "response": "raw"
  }'
```

Use `Prefer: respond-async` to force a job even for a sync-capable process. Async calls return `201`, a `Location` header, and `Preference-Applied: respond-async`; poll that job and fetch its results as shown in [run geoprocessing](run-geoprocessing.md).

## 3. Ask the agent to analyze data

The direct verbs accept standard geospatial-mcp `LayerRef` and `ArtifactRef` shapes. For example, ask the agent to buffer arterial roads by 500 meters. The corresponding tool call is:

```json
{
  "jsonrpc": "2.0",
  "id": 20,
  "method": "tools/call",
  "params": {
    "name": "honua_buffer_features",
    "arguments": {
      "source": { "serviceId": "county_roads", "layerId": 0 },
      "distance": 500,
      "unit": "meters",
      "dissolve": true,
      "where": "road_class = 'arterial'"
    }
  }
}
```

The tool submits one canonical analysis plan through the shared geoprocessing job service and returns `jobId`, `status`, `resourceUri`, and any immediately available artifacts. Poll `honua://jobs/{jobId}` and read `honua://jobs/{jobId}/results` after completion. Feed a returned `artifactId` into a later overlay, summary, reprojection, join, or export call; use `honua_plan_analysis` plus `honua_execute_plan` when dependencies require a multi-step DAG rather than a direct verb.

All five derived-analysis verbs advertise `readOnlyHint: true`: they compute new results without editing source features. `honua_export_dataset` advertises a non-destructive, idempotent write because it materializes a downloadable artifact. There is no AI-facing feature-edit tool.

### Use familiar GPServer task names

For an Esri-oriented migration, add `esri-gp` to `Mcp__Profiles` and use
`honua_esri_gp_list_tasks` followed by `honua_esri_gp_describe_task`. The list
includes both canonical ids and familiar aliases such as `Buffer`, `Intersect`,
`Project`, `Slope`, and `ZonalStatisticsAsTable`. Description output is built
from the same task projection GPServer serves, so input names, GP data types,
choices, defaults, and output bindings cannot drift between the AI and Esri
surfaces.

`honua_esri_gp_execute_task` takes `serviceId`, `taskName`, and the described
`parameters` object. It submits through the same `IGeoprocessingJobService` and
returns a `honua://jobs/{jobId}` handle; authorization, destructive-process
approval, admission, job ownership, artifact bindings, and telemetry remain in
that shared runtime. The generic execution tool is marked potentially
destructive because aliases include governed data-management tasks.

Aliases are name-level conveniences over Honua process contracts. They do not
pretend that every task has ArcPy's exact parameter signature, and this profile
does not discover or execute tools hosted by an external ArcGIS Server.

## 4. Inspect with the JS SDK

Install `honua-sdk-js` and use its CLI to inspect the source or a promoted result through the same published-service surface:

```bash
npm install --global @honua/sdk-js
export HONUA_BASE_URL=http://localhost:8080
export HONUA_API_KEY=your-key
honua services
honua layers county_roads
honua query county_roads/0 --where "road_class = 'arterial'" --limit 5 --format geojson
```

For application code, use the [honua-sdk-js guide](https://github.com/honua-io/honua-sdk-js/blob/main/docs/guide.md). The SDK handles published layers and map runtimes; OGC and MCP remain the analysis submission surfaces. Promote a completed result with `honua_publish_result` before treating it as a durable published layer.

## 5. Save a map, app, or dashboard in Studio

Create a Studio lifecycle draft with `honua_studio_create_draft`, add the promoted result with `honua_studio_add_layer`, set its style and viewport, add widgets or interactions, then call `honua_studio_validate_draft` and `honua_studio_preview_draft`. Every mutation must include the latest returned `generation`; re-fetch and retry after a stale-generation error.

The agent edits the same server-owned draft that the Studio browser opens. Saving creates an immutable content version; publishing and exposure remain a human-confirmed Studio or admin action. See the [Studio AI proxy](../run-studio-ai-proxy.md) for model-provider setup and the [Studio package lifecycle](../../internal/admin-api/studio-package-lifecycle.md) for the draft/version/publish contract.

## Approval and deployment posture

- Analytic built-ins require `Process.Execute`; mutating or durable sink plans additionally require `Process.ExecuteMutatingProcess`.
- Destructive processes such as delete-features and calculate-field stay behind the shared operator approval gate on OGC, MCP, GPServer, and gRPC.
- Native raster, surface, OGR, and point-cloud work requires the native worker; the serving image does not silently emulate it.
- Keep the `analysis` MCP profile off for agents that only discover/query data. Profile selection changes advertisement, not authorization.
- Studio composition can be agent-assisted, but publication, sharing, and embedding are reviewed human actions.

The practical arc is now one continuous, governed surface: deploy Honua with AI connectivity, configure services and run GP with AI, inspect or consume results with `honua-sdk-js`, then compose and save maps, apps, and dashboards in Studio.
