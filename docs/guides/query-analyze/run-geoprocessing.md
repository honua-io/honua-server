# Run geoprocessing

Discover a server-side process, run a bounded geometry operation synchronously, or submit a larger operation as a job — over OGC API Processes, with the same canonical runtime reachable through the ArcGIS-compatible GPServer adapter.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) and an API key — process execution requires an authenticated caller with the process-execute grant ([authentication](../secure/authentication.md)). Analytic built-ins require `Process.Execute`; imports, catalog mutations, and durable sinks additionally require `Process.ExecuteMutatingProcess`; operator-supplied code additionally requires `Process.ExecuteCustomCode`.

Process discovery is open. OGC directly projects all 86 non-source/non-sink built-ins plus the canonical plan runner. Deterministic `geometry.*` operations and `conversion.geometry-format` advertise both `sync-execute` and `async-execute`; all other processes remain asynchronous. The full operation catalog with every parameter is in the [geoprocessing operations reference](../../reference/geoprocessing-operations.md).

## Steps

1. List the available processes. The list contains the canonical `honua-geoprocessing` plan runner plus individually-projected catalog processes such as `geometry.buffer`, `geometry.clip`, `geometry.dissolve`, `analytics.spatial-join`, and `analytics.density`:

   Open `http://localhost:8080/ogc/processes/processes` in a browser.

2. Describe the process you want — the response lists its inputs with schemas:

   Open `http://localhost:8080/ogc/processes/processes/geometry.buffer` in a browser.

   `geometry.buffer` takes `wkb` (either base64-encoded WKB or a GeoJSON geometry), `srid`, `distance` in the input CRS's coordinate units, and an optional `geodesic` flag.

3. Execute a bounded geometry operation synchronously. With no `Prefer` header, sync-capable processes default to synchronous execution. This request asks for the single result as raw GeoJSON and returns `200 OK` with `Content-Type: application/geo+json`:

   In the authorized [API explorer](../../reference/openapi-and-explorer.md), run `POST /ogc/processes/processes/geometry.buffer/execution` with this body:

   ```json
   {
     "inputs": {
       "wkb": { "type": "Point", "coordinates": [-122.4194, 37.7749] },
       "srid": 4326,
       "distance": 0.005
     },
     "response": "raw"
   }
   ```

   EPSG:4326 coordinates are degrees, so the distance above is degrees, not meters. Project to a metric CRS before applying a metric planar distance.

4. For a job response, add `Prefer: respond-async` (or choose an async-only process). The response is `201 Created`, carries `Preference-Applied: respond-async`, and points its `Location` header at the job. Poll until `status` reaches `successful` (or `failed`/`dismissed`):

   Run `GET /ogc/processes/jobs/{jobId}` in the explorer.

5. Fetch the results document for a terminal job, and dismiss the job when you are done with it:

   Run `GET /ogc/processes/jobs/{jobId}/results`, then `DELETE /ogc/processes/jobs/{jobId}` in the explorer.

   Catalog processes return document-mode artifact references when the runtime publishes results; `GET /ogc/processes/jobs` lists your recent jobs.

The same catalog is exposed Esri-style for ArcGIS clients: `GET /rest/services/{serviceId}/GPServer` lists tasks, the task name is the process id (`/rest/services/{serviceId}/GPServer/geometry.buffer`), and the standard `submitJob` / `jobs/{jobId}` / `jobs/{jobId}/results/{paramName}` / `jobs/{jobId}/cancel` operations drive the same job runtime. A fresh instance ships a default `geoprocessing` service so the facade works out of the box — e.g. `GET /rest/services/geoprocessing/GPServer` — and every published service also exposes GPServer (so `{serviceId}` can be any service you have published). To turn the default service off, set `Geoprocessing:SeedDefaultService=false` (env `HONUA_GEOPROCESSING_SEED_DEFAULT_SERVICE=false`). Deterministic single-geometry tasks (the `geometry.*` family and `conversion.geometry-format`) also accept the synchronous `execute` route (`POST`/`GET /rest/services/{serviceId}/GPServer/geometry.buffer/execute`), which runs the task inline through that same runtime and returns the Esri execute envelope (`results` + `messages`) on the same request. Async-only tasks reject `execute` with a 400 capability message pointing back at `submitJob`.

## Verify

Run `GET /ogc/processes/jobs/{jobId}` again in the explorer.

Expected (trimmed):

```json
{ "processID": "geometry.buffer", "jobID": "0123456789abcdef",
  "status": "successful", "progress": 100 }
```

## Troubleshoot

- **401 on execution** — discovery is anonymous but `POST .../execution` is not; send your `X-API-Key` (or bearer token).
- **403** — the identity authenticates but lacks `Process.Execute` or the additional `Process.ExecuteMutatingProcess` / `Process.ExecuteCustomCode` grant required by the selected execution tier.
- **404 for `source.*` or `sink.*`** — connectors compose canonical plans and are intentionally not standalone OGC processes. Every other built-in is directly projected.
- **422 for `Prefer: respond-sync`** — the selected process is async-only. Use `Prefer: respond-async`; only `geometry.*` and `conversion.geometry-format` support synchronous execution.
- **422 for `response: raw`** — raw mode requires a sync-capable process with exactly one output. Use document mode or an async job for multi-output processes.
- **Job stuck in `accepted`** — the job queue needs the durable job substrate (Redis) to be healthy; see [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Run geoprocessing locally and prototype your own GP process](gp-local-dev-quickstart.md)
- [Author a geoprocessing process](gp-devkit-authoring.md) — write your own process with the GP Devkit
- [Automate workflows](automate-workflows.md)
- [Geoprocessing operations reference](../../reference/geoprocessing-operations.md)
- [Connect AI agents over MCP](../connect/ai-agents-mcp.md)
