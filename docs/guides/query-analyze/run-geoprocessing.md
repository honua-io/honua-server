# Run geoprocessing

Discover a server-side process, submit it as an asynchronous job, poll its status, and fetch results — over OGC API Processes, with the same catalog reachable through the ArcGIS-compatible GPServer adapter.

**Prerequisites:** a running server ([quickstart](../../get-started/quickstart.md)) and an API key — process execution requires an authenticated caller with the process-execute grant ([authentication](../secure/authentication.md)).

Process discovery is open; execution is always asynchronous (`jobControlOptions: ["async-execute"]`). The full operation catalog with every parameter is in the [geoprocessing operations reference](../../reference/geoprocessing-operations.md).

## Steps

1. List the available processes. The list contains the canonical `honua-geoprocessing` plan runner plus individually-projected catalog processes such as `geometry.buffer`, `geometry.clip`, `geometry.dissolve`, `analytics.spatial-join`, and `analytics.density`:

   ```bash
   BASE=http://localhost:8080
   KEY=my-api-key
   curl -s "$BASE/ogc/processes/processes"
   ```

2. Describe the process you want — the response lists its inputs with schemas:

   ```bash
   curl -s "$BASE/ogc/processes/processes/geometry.buffer"
   ```

   `geometry.buffer` takes `wkb` (base64-encoded WKB geometry), `srid`, `distance` (meters), and an optional `geodesic` flag.

3. Execute it. The request returns `201 Created` with a job status document and a `Location` header pointing at the job:

   ```bash
   curl -si -X POST "$BASE/ogc/processes/processes/geometry.buffer/execution" \
     -H "X-API-Key: $KEY" -H "Content-Type: application/json" \
     -H "Prefer: respond-async" -d '{
     "inputs": {
       "wkb": "AQEAAABQ/Bhz15pewNDVVuwv40JA",
       "srid": 4326,
       "distance": 500
     }
   }'
   ```

   The `wkb` value above is `POINT(-122.4194 37.7749)`. Only `"response": "document"` mode is supported; omit `response` or set it to `document`.

4. Poll the job until `status` reaches `successful` (or `failed`/`dismissed`). Take `JOB` from the `Location` header or the `jobID` field:

   ```bash
   JOB=0123456789abcdef
   curl -s "$BASE/ogc/processes/jobs/$JOB" -H "X-API-Key: $KEY"
   ```

5. Fetch the results document for a terminal job, and dismiss the job when you are done with it:

   ```bash
   curl -s "$BASE/ogc/processes/jobs/$JOB/results" -H "X-API-Key: $KEY"
   curl -s -X DELETE "$BASE/ogc/processes/jobs/$JOB" -H "X-API-Key: $KEY"
   ```

   Catalog processes return document-mode artifact references when the runtime publishes results; `GET /ogc/processes/jobs` lists your recent jobs.

The same catalog is exposed Esri-style for ArcGIS clients: `GET /rest/services/{serviceId}/GPServer` lists tasks, the task name is the process id (`/rest/services/{serviceId}/GPServer/geometry.buffer`), and the standard `submitJob` / `jobs/{jobId}` / `jobs/{jobId}/results/{paramName}` / `jobs/{jobId}/cancel` operations drive the same job runtime.

## Verify

```bash
curl -s "$BASE/ogc/processes/jobs/$JOB" -H "X-API-Key: $KEY"
```

Expected (trimmed):

```json
{ "processID": "geometry.buffer", "jobID": "0123456789abcdef",
  "status": "successful", "progress": 100 }
```

## Troubleshoot

- **401 on execution** — discovery is anonymous but `POST .../execution` is not; send your `X-API-Key` (or bearer token).
- **403** — the identity authenticates but lacks the process-execute operator grant.
- **404 for a process id you saw in the full catalog** — only first-slice vector processes are projected through OGC API Processes; others run via the canonical `honua-geoprocessing` plan process or are listed in the [reference](../../reference/geoprocessing-operations.md).
- **501 `Unsupported response mode`** — only `document` response mode is implemented; remove `"response": "raw"`.
- **Job stuck in `accepted`** — the job queue needs the durable job substrate (Redis) to be healthy; see [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Automate workflows](automate-workflows.md)
- [Geoprocessing operations reference](../../reference/geoprocessing-operations.md)
- [Connect AI agents over MCP](../connect/ai-agents-mcp.md)
