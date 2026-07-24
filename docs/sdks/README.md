# SDKs

The Honua SDKs are typed clients for the same server you run from the [quickstart](../get-started/quickstart.md). They wrap the protocols Honua already speaks — GeoServices REST (FeatureServer), OGC API Features, STAC, OData, vector tiles, and the admin control plane — so you call methods instead of hand-building URLs. Everything an SDK does, the [HTTP API](../reference/README.md) can do too; the SDKs add types, paging, retries, and authentication handling.

The three first-party server SDKs are generated and tested against the same admin OpenAPI contract and released as a coordinated set. Separate experimental mobile projects provide a reusable MAUI control foundation and a complete field app — see [Ecosystem & SDKs](../concepts/ecosystem.md) for the repository map and the [SDK-to-server compatibility rules](../concepts/ecosystem.md#sdk-to-server-compatibility).

## Pick your SDK

| SDK | Package | Latest | Runtime | Start here |
|---|---|---|---|---|
| **.NET** | `Honua.Sdk` (NuGet) | 1.2.1 | net10.0 | [.NET getting started](dotnet/getting-started.md) |
| **Python** | `honua-sdk` (PyPI) | 0.1.4 | Python ≥ 3.11 | [Python getting started](python/getting-started.md) |
| **JavaScript / TypeScript** | `@honua/sdk-js` (npm) | 0.0.14-alpha | Node ≥ 20 | [JavaScript getting started](javascript/getting-started.md) |
| Mobile controls (.NET MAUI) | [honua-mobile](https://github.com/honua-io/honua-mobile) | Experimental (Apache-2.0) | .NET MAUI | Reusable SDK and map/control foundation; repo README |
| Field collection app | [honua-collect](https://github.com/honua-io/honua-collect) | Experimental (ELv2) | .NET MAUI | Full end-user app built on `honua-mobile`; repo README |

The SDK package lines are pre-release. Pin exact versions and validate against your target server before broad rollout. All three SDKs expose a runtime capability handshake (`GET /api/v1/admin/capabilities`) so clients negotiate features instead of inferring them from version numbers.

## Authentication

Every SDK authenticates the same way the server does — see [Authenticate clients](../guides/secure/authentication.md) for the full picture. In short:

- **API key** — sent as the `X-API-Key` header. Use a scoped key for automation; the admin password is the root key for `/api/v1/admin/*`. This is the default path for the SDKs and what the getting-started pages use.
- **Bearer token** — `Authorization: Bearer <token>` for OIDC sign-in. Each SDK takes a static token or a refreshable token provider.
- **ArcGIS portal tokens** — for Esri clients; not needed when you use an SDK directly.

Mint a scoped key once and reuse it across SDKs:

In the authorized [API explorer](../reference/openapi-and-explorer.md), run `POST /api/v1/admin/api-keys` with this body:

```json
{
  "name": "sdk-quickstart",
  "permissions": [],
  "expiresAt": null
}
```

The response's `data.key` is shown once — store it as the API key your SDK client uses.

## What the SDKs share

All three follow the same shape so concepts transfer between languages:

- A **client** is constructed with a base URL and credentials, then reused.
- A **protocol-neutral data API** — `Source` / `Query` / `Result` (Python and JavaScript) — lets you query any published layer the same way regardless of whether it is served as a FeatureServer, OGC API Features collection, or STAC collection.
- **Protocol-specific clients** (FeatureServer, OGC Features, STAC, OData) are available as escape hatches when you want the native shape of one protocol.
- An **admin client** wraps the control plane (`/api/v1/admin/*`) for connections, imports, layers, and keys.

## Common tasks

Each SDK has a short common-tasks page covering the two most common reads — query a FeatureServer layer and run a STAC search:

- [.NET common tasks](dotnet/common-tasks.md)
- [Python common tasks](python/common-tasks.md)
- [JavaScript common tasks](javascript/common-tasks.md)

The underlying protocols are documented under [Reference → Protocols](../reference/README.md): [GeoServices REST](../reference/protocols/geoservices-rest.md), [OGC APIs](../reference/protocols/ogc-apis.md), and [STAC](../reference/protocols/stac.md). For generated OpenAPI equivalents of these calls, see [Query features](../guides/query-analyze/query-features.md).
