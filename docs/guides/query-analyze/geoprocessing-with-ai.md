# Geoprocessing with AI

Use one governed `geometry.buffer` operation across OGC API Processes, Honua's MCP plan tools, and the JavaScript SDK, then hand the result artifact to Studio. This is stage two of the terminal journey: complete the [server setup and control-plane walkthrough](https://github.com/honua-io/honua-server/issues/3364) first, and continue with the [Studio save and reopen journey](https://github.com/honua-io/honua-server/issues/3305) when that documentation lands.

> [!IMPORTANT]
> **Capability truth.** `process.geoprocessing` and `process.ogc-api-processes` are Community capabilities. Process discovery is open; execution requires an authenticated identity with `Process.Execute`. Redis-backed durable job storage is required for asynchronous execution. The bounded `geometry.buffer` operation is non-destructive and does not require approval. Mutating or destructive catalog operations require their additional grant and may enter the human approval lane. The direct geospatial-mcp analysis-profile verb `buffer_features` is not shipped: [#3269](https://github.com/honua-io/honua-server/issues/3269) blocks that shortcut. Use the shipped `honua_validate_plan` and `honua_execute_plan` tools below.

## Before you start

Set the server URL and API key without putting the credential in the guide or a checked-in file:

```bash
export HONUA_URL=http://localhost:8080
export HONUA_API_KEY='<your API key>'
```

The examples use `POINT(-122.4194 37.7749)` in EPSG:4326 and a planar distance of `0.00025` degrees. Use a projected CRS for a distance in meters.

## 1. Discover and describe the process

Confirm that the candidate publishes the process, its execution modes, and both accepted geometry representations:

```bash
curl -fsS "$HONUA_URL/ogc/processes/processes" |
  jq '.processes[] | select(.id == "geometry.buffer")'

curl -fsS "$HONUA_URL/ogc/processes/processes/geometry.buffer" |
  jq '{id, jobControlOptions, inputs, outputs}'
```

The `wkb` input name is retained for compatibility, but its OGC schema accepts either base64 WKB or a GeoJSON geometry object (`application/geo+json`).

## 2. Run the GeoJSON buffer through OGC

Omit `Prefer` for the advertised synchronous mode:

```bash
curl -fsS \
  -H "X-API-Key: $HONUA_API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"inputs":{"wkb":{"type":"Point","coordinates":[-122.4194,37.7749]},"srid":4326,"distance":0.00025,"geodesic":false}}' \
  "$HONUA_URL/ogc/processes/processes/geometry.buffer/execution" |
  jq '.outputFeatureLayer | {id, kind, href, type}'
```

Keep the returned artifact `id` and `href`. The `href` is a GeoJSON data URI in the bounded inline result; it is the artifact consumed by the later Studio stage. For durable execution, send `Prefer: respond-async`, poll the returned job URL, and read `/ogc/processes/jobs/{jobId}/results`.

## 3. Run the same governed operation through MCP

The shipped MCP surface executes canonical plans. Its plan schema currently string-encodes process inputs, and catalog validation for this base-plan path requires base64 WKB. This is a transport-shape distinction, not a claim that OGC accepts only WKB.

Call `honua_validate_plan` first with this `plan` value, then pass the same value to `honua_execute_plan`:

```json
{
  "planId": "gp-ai-journey-buffer",
  "steps": [
    {
      "stepId": "buffer-point",
      "kind": "Geoprocess",
      "processId": "geometry.buffer",
      "inputs": {
        "wkb": "AQEAAABQ/Bhz15pewNDVVuwv40JA",
        "srid": "4326",
        "distance": "0.00025",
        "geodesic": "false"
      },
      "dependsOn": []
    }
  ],
  "outputs": ["FeatureLayer"]
}
```

`honua_validate_plan` must return `isExecutable: true`. `honua_execute_plan` returns a `jobId` and `honua://jobs/{jobId}`. Read that resource until the status is terminal, then read `honua://jobs/{jobId}/results`. Preserve the result artifact reference for Studio.

Do not call or document `buffer_features` yet. It is deliberately absent from `tools/list`, even if an operator configures the `analysis` profile, until [#3269](https://github.com/honua-io/honua-server/issues/3269) delivers executable implementations. The adjudicated export fixture uses GeoJSON or FileGDB; GeoPackage is not part of this journey.

## 4. Consume it with the JavaScript SDK

The SDK performs discovery and gates execution on the candidate's advertised conformance classes and `jobControlOptions`:

```ts
import { HonuaClient } from "@honua/sdk-js";

const client = new HonuaClient({
  baseUrl: process.env.HONUA_URL!,
  apiKey: process.env.HONUA_API_KEY!,
});

const discovery = client.ogcProcesses();
const conformance = await discovery.conformance();
const processes = client.ogcProcesses({ conformance, capabilityPolicy: "advertised" });
const description = await processes.describe("geometry.buffer");

const run = await processes.execute({
  processId: "geometry.buffer",
  mode: "async",
  jobControlOptions: description.jobControlOptions ?? [],
  inputs: {
    wkb: "AQEAAABQ/Bhz15pewNDVVuwv40JA",
    srid: 4326,
    distance: 0.00025,
    geodesic: false,
  },
});

const result = await run.results();
const artifact = result.outputs.outputFeatureLayer;
```

The SDK handles either legal synchronous or asynchronous response shape. It does not send the non-standard `Prefer: respond-sync`. Cancellation must remain disabled when `dismiss` is not advertised.

## 5. Continue in Studio

Add the GeoJSON result referenced by `artifact.href` to the same server-resident Studio draft, or promote a durable MCP result with `honua_publish_result` and add the returned hosted layer. Save the artifact id, job id, draft id, and draft generation together so the next stage can prove it is using this run rather than a fixture.

The complete Studio authoring/run UI is a 2026.2 surface. Until [#3305](https://github.com/honua-io/honua-server/issues/3305) lands, use the existing [Studio AI proxy guide](../run-studio-ai-proxy.md) and the `honua_studio_*` MCP lifecycle described in [Connect AI agents](../connect/ai-agents-mcp.md). Console job/result inspection is optional and does not define completion of this terminal path.

## Deferred surfaces

Python, .NET, Batch, QGIS, broad GPServer task parity, direct analysis-profile verbs, and Console authoring/run UI are 2026.2 or separately tracked work. They are not requirements for this bounded OGC/MCP/JavaScript journey.

## Troubleshoot

- `401` means execution was attempted without a valid identity.
- `403` means the identity lacks `Process.Execute` or an additional grant required by the selected process.
- `503` on an asynchronous request means the durable job substrate is unavailable.
- An MCP `invalid_argument` saying `expected base64-encoded WKB` means GeoJSON was passed through the base plan's string-valued input map. Use GeoJSON on the OGC route; use the advertised base64 WKB shape for the shipped MCP plan path.
- A missing `buffer_features` tool is expected until #3269 lands; do not enable it by configuration or substitute GeoPackage.
