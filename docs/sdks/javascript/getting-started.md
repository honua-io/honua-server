# Get started with the JavaScript SDK

Install the Honua JavaScript/TypeScript SDK, construct a client, authenticate with an API key, and make your first feature query.

**Prerequisites:** A running Honua server ([quickstart](../../get-started/quickstart.md)) with at least one published layer ([publish layers](../../guides/publish/publish-layers.md)), Node.js 20 or newer, and an API key (see [Authenticate clients](../../guides/secure/authentication.md) — the SDK landing page shows how to [mint a scoped key](../README.md#authentication)).

The SDK ships as `@honua/sdk-js` on npm. It is **ESM-only** and targets **Node ≥ 20**. The current release is **0.0.14-alpha** — pin the exact version and expect breaking changes in minor releases for symbols marked experimental. A companion [MCP server](https://github.com/honua-io/honua-sdk-js) package (`@honua/mcp-server`) exposes the same surface to AI agents (see [AI agents (MCP)](../../guides/connect/ai-agents-mcp.md)). ArcGIS code migration is owned by [`honua-migrate`](https://github.com/honua-io/honua-migrate) (see [ArcGIS apps & SDKs](../../guides/migrate/arcgis-apps-and-sdks.md)).

## Steps

### 1. Install the package

```bash
npm install @honua/sdk-js
```

For a build-less browser page you can import it from a CDN instead:

```html
<script type="module">
  import { HonuaClient } from "https://esm.sh/@honua/sdk-js/browser";
</script>
```

### 2. Construct a client

`HonuaClient` is constructed with the server base URL and credentials. The API key is sent as the `X-API-Key` header. Import it from the `honua` subpath:

```ts
import { HonuaClient } from "@honua/sdk-js/honua";

const client = new HonuaClient({
  baseUrl: "http://localhost:8080",
  apiKey: process.env.HONUA_API_KEY,   // sent as X-API-Key
  transport: "rest",                    // or "grpc-web"
});
```

For OIDC, pass `bearerToken: "..."` or an `auth` callback that returns refreshable credentials. Browser apps that talk to a public layer can omit credentials entirely.

### 3. Make your first call

Optionally check server compatibility, then query a published layer. This uses the GeoServices/FeatureServer shape — swap `serviceId`/`layerId` for one of your own layers:

```ts
const { supported, reasons } = await client.checkCompatibility();
if (!supported) throw new Error(`Unsupported Honua server: ${reasons.join("; ")}`);

const { features } = await client.queryFeatures({
  serviceId: "default",
  layerId: 0,
  where: "1=1",
  outFields: ["*"],
  returnGeometry: true,
  resultRecordCount: 25,
});

console.log(`Returned ${features.length} features`);
for (const f of features.slice(0, 5)) console.log(f.attributes);
```

## Verify

```ts
console.log(`Returned ${features.length} features`);
```

A wrong or missing API key surfaces as an authentication error from the client. Confirm `HONUA_API_KEY` is set, then rerun the authenticated query above.

## Available surfaces

`@honua/sdk-js` ships several entrypoints; import only what you need:

| Import | Provides |
|---|---|
| `@honua/sdk-js/honua` | `HonuaClient` plus native wrappers — `HonuaFeatureLayer`, `HonuaStacSearch`, `HonuaOgcFeatures`, `HonuaWms`, `HonuaOdataEntitySet`, … |
| `@honua/sdk-js/contract` | Protocol-neutral `Dataset` / `Source` / `Query` / `Result` and source factories |
| `@honua/sdk-js/esri-compat` | ArcGIS compatibility layer for gradual migration |
| [`@honua/honua-migrate`](https://github.com/honua-io/honua-migrate) | Separate scanner, codemod, content, and reconciliation package |

## Troubleshoot

| Symptom | Fix |
|---|---|
| Authentication error / 401 | `apiKey` is unset or wrong; set it and rerun the authenticated SDK query above. |
| `ERR_REQUIRE_ESM` / import errors | The SDK is ESM-only; use `import`, `"type": "module"`, and Node ≥ 20. |
| `checkCompatibility()` reports unsupported | The server is older than the SDK's minimum supported version; align release channels per the [compatibility rules](../../concepts/ecosystem.md#sdk-to-server-compatibility). |
| CORS errors in the browser | Add your page origin to the server's allowed origins (see the [quickstart](../../get-started/quickstart.md) CORS note). |

More general failures: [Troubleshooting](../../guides/deploy/troubleshooting.md).

## Next steps

- [JavaScript common tasks](common-tasks.md) — query a FeatureServer layer and run a STAC search
- [honua-sdk-js on GitHub](https://github.com/honua-io/honua-sdk-js) — full package list, MCP server, and codemod
- [MapLibre web maps](../../guides/connect/maplibre-web-maps.md) — render SDK results on a map
- [SDK overview](../README.md)
