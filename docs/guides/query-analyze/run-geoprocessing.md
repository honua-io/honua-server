---
type: guide
title: "Run geoprocessing"
description: "Discover a server-side process, execute it synchronously or submit it as an asynchronous job, and fetch results — over OGC API Processes, with the same catalog reachable through the ArcGIS-compatible GPServer adapter."
resource: "honua://capability/process.geoprocessing"
---
# Run geoprocessing

Discover a server-side process, execute it synchronously or submit it as an asynchronous job, and fetch results — over OGC API Processes, with the same catalog reachable through the ArcGIS-compatible GPServer adapter.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) and an API key — process execution requires an authenticated caller with the process-execute grant ([authentication](../secure/authentication.md)). Analytic built-ins require `Process.Execute`; imports, catalog mutations, and durable sinks additionally require `Process.ExecuteMutatingProcess`; operator-supplied code additionally requires `Process.ExecuteCustomCode`.

Process discovery is open. Omit `Prefer` for bounded synchronous execution when the process advertises `sync-execute`; send `Prefer: respond-async` for durable asynchronous execution. `respond-sync` is not a defined preference and is not acknowledged. The full operation catalog with every parameter is in the [geoprocessing operations reference](../../reference/geoprocessing-operations.md).

## Steps

1. List the available processes. The list contains the canonical `honua-geoprocessing` plan runner plus individually-projected catalog processes such as `geometry.buffer`, `geometry.clip`, `geometry.dissolve`, `analytics.spatial-join`, and `analytics.density`:

   Open `http://localhost:8080/ogc/processes/processes` in a browser.

2. Describe the process you want — the response lists its inputs with schemas:

   Open `http://localhost:8080/ogc/processes/processes/geometry.buffer` in a browser.

   `geometry.buffer` takes `wkb`, `srid`, `distance`, and an optional `geodesic` flag. Despite the legacy parameter name, the OGC route accepts either base64-encoded WKB or a GeoJSON geometry object. `distance` is expressed in the input CRS units, so project geographic coordinates before requesting a metric buffer.

3. Execute it. The request returns `201 Created` with a job status document and a `Location` header pointing at the job:

   In the authorized [API explorer](../../reference/openapi-and-explorer.md), run `POST /ogc/processes/processes/geometry.buffer/execution` with `Prefer: respond-async` and this body:

   ```json
   {
     "inputs": {
       "wkb": "AQEAAABQ/Bhz15pewNDVVuwv40JA",
       "srid": 4326,
       "distance": 500
     }
   }
   ```

   The `wkb` value above is `POINT(-122.4194 37.7749)`. Omit `response` or set it to `document` for inline qualified output values. Set `"response": "raw"` to retrieve native output bytes from the results route; multiple selected outputs use `multipart/related`. Both response modes work asynchronously. For a process advertising `sync-execute`, omit `Prefer` to run synchronously with the same response negotiation.

4. Poll the job until `status` reaches `successful` (or `failed`/`dismissed`). Take `JOB` from the `Location` header or the `jobID` field:

   Run `GET /ogc/processes/jobs/{jobId}` in the explorer.

5. Fetch the results document for a terminal job, and dismiss the job when you are done with it:

   Run `GET /ogc/processes/jobs/{jobId}/results`, then `DELETE /ogc/processes/jobs/{jobId}` in the explorer.

   Catalog processes return inline qualified values in document mode, or native/multipart content when raw mode was requested; `GET /ogc/processes/jobs` lists your recent jobs.

The same catalog is exposed Esri-style for ArcGIS clients: `GET /rest/services/{serviceId}/GPServer` publishes deterministic Python-safe task names (`Honua_` followed by the uppercase UTF-8 hex encoding of the canonical process id), together with unambiguous Esri-conventional aliases such as `Buffer`. Existing canonical REST paths remain compatible aliases: `/GPServer/geometry.buffer` and `/GPServer/Buffer` address the same process. OGC identifiers and the canonical catalog are unchanged. These aliases are a name-level convenience, not a task-specific wire adapter: task info for `Buffer` publishes the canonical `wkb`, `srid`, and numeric `distance` inputs, not Esri Buffer's feature-record-set, linear-unit, and dissolve-option contract. Clients must use the published task metadata.

The standard `submitJob` / `jobs/{jobId}` / `jobs/{jobId}/results/{paramName}` / `jobs/{jobId}/cancel` operations drive the same job runtime. Successful jobs advertise their named result parameters, and the per-parameter result route reads the stored result-package value and Esri data type. A fresh instance ships a default `geoprocessing` service so the facade works out of the box — e.g. `GET /rest/services/geoprocessing/GPServer` — and every published service also exposes GPServer (so `{serviceId}` can be any service you have published). To turn the default service off, set `Geoprocessing:SeedDefaultService=false` (env `HONUA_GEOPROCESSING_SEED_DEFAULT_SERVICE=false`). Deterministic single-geometry tasks (the `geometry.*` family and `conversion.geometry-format`) also accept the synchronous `execute` route (`POST`/`GET /rest/services/{serviceId}/GPServer/geometry.buffer/execute`), which runs the task inline through that same runtime and returns the Esri execute envelope (`results` + `messages`) on the same request. A synchronous run that fails or is cancelled returns the GeoServices error envelope (`{"error":{"code":500,...}}`, with the job id, terminal status, and failure message in `details`) instead of an empty `results` list, so Esri clients see the failure. Async-only tasks reject `execute` with a 400 capability message pointing back at `submitJob`.

Both `execute` and `submitJob` accept the Esri 10.6.1+ `context` parameter as the JSON form of the environment controls: `context.outSR` and `context.processSR` behave exactly like `env:outSR` and `env:processSR` (a WKID or a `{"wkid":...}` object). An empty `extent` is accepted; any other `context` property returns a 400 naming it rather than being ignored, and a `context` spatial reference that disagrees with the matching `env:*` value is rejected.

Layer-scoped task parameters published as `GPFeatureRecordSetLayer`, including the
`input` and `clip` parameters of `Clip`, accept an Esri FeatureSet JSON object in
the form parameter. Supply `features` with `attributes` and Esri `geometry`
objects, plus the collection's `spatialReference`. Every feature and attribute row
is retained. Z ordinates are supported; measured FeatureSets are rejected explicitly
because the canonical GeoJSON collection format cannot preserve M ordinates.
Single-geometry tasks still require one geometry and do not flatten multiple
features into a single geometry.

GP result `value` follows the
[Esri data-type contract](https://developers.arcgis.com/rest/services-reference/enterprise/gp-data-types/).
Inline vector outputs return a FeatureSet object with `fields`, `features`,
`geometryType`, `spatialReference`, and dimensional flags. Table outputs return
records with attributes. Stored feature, raster, and file outputs return an object
such as `{"url":"https://example.test/result"}`. Both synchronous `execute` and
asynchronous result retrieval use these shapes.
Empty results retain the input-derived fields and geometry type. Canonical GeoJSON inputs use
WGS 84 when no explicit working reference is supplied; equivalent Esri Web Mercator WKIDs are
normalized before comparison. Merge rejects incompatible geometry types before creating a job.
An unavailable artifact remains an explicit fallback label rather than a fictitious download URL.


### SOAP remote toolbox execution

ArcPy discovers the same catalog through `POST /services/{serviceId}/GPServer`.
The SOAP adapter supports `SubmitJob`, `Execute`, `GetJobStatus`, `GetJobMessages`,
`GetJobResult`, `GetJobToolName`, and `CancelJob` through the same authorized job
handlers as REST. Job ownership, tenant and service/task binding checks apply.
Failed synchronous execution produces a SOAP fault; cancellation succeeds only
when the canonical runtime confirms it. The mixed catalog continues to advertise
asynchronous execution, while SOAP `Execute` accepts sync-eligible tasks.

SOAP execution currently supports scalar `GPString`, `GPLong`, `GPDouble`,
`GPBoolean`, and `GPDate` values. Complex SOAP feature/raster/file/multivalue
payloads are rejected explicitly. REST's FeatureSet and artifact contracts above
remain available. Result options accept URL transport, no densification, and
boolean ReturnData/UpdateValues controls for scalar outputs; unsupported options
return faults. Supported SOAP spatial-reference environment controls map to the
same REST environment validation. ArcPy's six unchanged defaults (Z/M "Same As
Input", random seed 0/ACM599, autoCommit 1000, CONVERT_UNITS cell-size projection,
and NONE nodata) are accepted only for synchronous geometry tasks with scalar
outputs, where those defaults do not alter the result. Changed controls remain
subject to canonical validation.

Installed-client execution observations are retained with the GPServer tests in
`Fixtures/EsriToolboxReplay`. These establish remote scalar execution, including
an independently verified rectangle area; a native Pro desktop compatibility
claim additionally requires fresh candidate and desktop UI receipts.

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
- **404 for a process id you saw in the full catalog** — the OGC route projects catalog processes classified as executable jobs. Protocol-only, workflow-only, and unavailable entries are described in the [reference](../../reference/geoprocessing-operations.md).
- **400 `Invalid response mode`**: the canonical `honua-geoprocessing` plan process requires document mode because it has no declared value outputs. Catalog processes support raw results with synchronous or asynchronous execution.
- **413 when fetching results**: the selected results exceed the configured `Geoprocessing:Executors:MaxArtifactBytes` response limit.
- **Job stuck in `accepted`** — the job queue needs the durable job substrate (Redis) to be healthy; see [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Run geoprocessing locally and prototype your own GP process](gp-local-dev-quickstart.md)
- [Author a geoprocessing process](gp-devkit-authoring.md) — write your own process with the GP Devkit
- [Automate workflows](automate-workflows.md)
- [Geoprocessing operations reference](../../reference/geoprocessing-operations.md)
- [Connect AI agents over MCP](../connect/ai-agents-mcp.md)

### Bounded layer execution

Layer-sourced buffer, dissolve, simplify, project, spatial-join and enrichment jobs
apply the configured input limits while reading. Both join layers are bounded.
`Limits:Analytics:MaxInputFeatures`, `Limits:Analytics:MaxInputBytes`,
`Limits:Geometry:MaxGeometrySize` and `Limits:Geometry:MaxVerticesPerGeometry`
remain authoritative. Non-ASCII text is charged in UTF-8 bytes, and nested
attribute values count toward the input budget.

The shared `Geoprocessing:Executors` configuration also limits computation:

| Setting | Default | Enforced behavior |
| --- | ---: | --- |
| `MaxLayerVertices` | 100,000 | Cumulative vertices per input layer and buffered intermediate set; stops the read/buffer loop on overflow. |
| `MaxTopologyWork` | 4,000,000 | Before managed topology, rejects squared vertex count for each buffer input, squared total vertex count for dissolve/simplify and buffered unions, or the product of both layers' vertex counts for joins/enrichment. This conservative estimate bounds admission even when a spatial index would later reduce the actual work. |
| `MaxLayerExecutionSeconds` | 300 | Cancels layer reading, computation between topology calls, and serialization; a smaller job deadline still applies. |
| `MaxArtifactBytes` | 52,428,800 | Stops UTF-8 serialization as the byte ceiling is reached, including attribute expansion; publishes no partial artifact. |

An individual managed topology call cannot be interrupted. Its admitted work is
bounded before entry; cancellation is observed as soon as it returns. These
settings do not promise a hard wall-clock interrupt inside NetTopologySuite.
Qualify the selected topology and limits under the worker's actual CPU and memory
constraints before raising them. A failed resource budget returns its setting
name and guidance to narrow the selection or simplify the input before resubmission.
The job is failed, never silently truncated or reported as a partial success.

The [layer resource qualification fixture](../../../tests/dotnet/Honua.Server.Tests/Features/Geoprocessing/Execution/LayerResourceQualification.md)
documents the constrained deployment, independent geometry oracle, serving probes,
and the currently failing manifest-pinned candidate receipt.
