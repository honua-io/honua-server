# Ecosystem

Honua is developed as a family of repositories around this server. This page maps what lives where and how the pieces version against each other.

## Repository map

| Repository | What it is |
|---|---|
| [honua-server](https://github.com/honua-io/honua-server) | The server runtime (this repo): protocol adapters, canonical pipelines, admin API, conformance and test infrastructure |
| [honua-console](https://github.com/honua-io/honua-console) | Web admin UI — Studio (map building and styling), Catalog, Operate, and Share surfaces over the admin API. UI documentation coming soon. |
| [honua-sdk-js](https://github.com/honua-io/honua-sdk-js) | JavaScript/TypeScript SDKs, including the MCP server package, an ArcGIS compatibility layer, and the `honua-migrate` codemod for porting ArcGIS JS code |
| [honua-sdk-dotnet](https://github.com/honua-io/honua-sdk-dotnet) | .NET SDKs (`Honua.Sdk.*` packages, admin + gRPC clients) |
| [honua-sdk-python](https://github.com/honua-io/honua-sdk-python) | Python SDK for the control plane and data APIs |
| [honua-mobile](https://github.com/honua-io/honua-mobile) | .NET MAUI-first mobile SDK with GeoPackage/offline field-collection foundation |
| [honua-helm](https://github.com/honua-io/honua-helm) | Helm chart for Kubernetes deployment |
| honua-iac (private) | Terraform modules, environments, and validation CI — available to customers through support |
| [geospatial-grpc](https://github.com/honua-io/geospatial-grpc) | Open gRPC protocol definitions (`geospatial.v1`) for feature services, spatial types, and forms — the canonical `.proto` source the server consumes |
| [geospatial-mcp](https://github.com/honua-io/geospatial-mcp) | Open geospatial MCP standard for analyst, map, and app-builder agent workflows |

The server consumes generated gRPC bindings from the published `Geospatial.Grpc` package; wire-contract changes land in `geospatial-grpc` first. The MCP surface at `/mcp` implements the `geospatial-mcp` standard. See [Protocols](protocols.md).

## SDK-to-server compatibility

The JavaScript/TypeScript, Python, and .NET admin SDKs are generated from the same curated admin OpenAPI contract and released as one versioned set. The compatibility rules:

- A server and its SDKs must share the same admin API major. The current major is `v1`.
- CI continuously tests a 3×3 matrix — the last three server refs against the last three SDK sets — and any failing supported cell is a regression. The machine-readable source of truth is [`sdk-compatibility-versions.json`](../developer/sdk-compatibility-versions.json).
- Match release channels: `stable` server with `stable` SDKs, `beta` with `beta`, `preview` with `preview`. Do not mix pre-release SDKs into a production server line.
- For a runtime handshake, SDK clients use `GET /api/v1/admin/capabilities` rather than inferring features from version numbers — see the [admin API overview](../reference/admin-api/overview.md).

Current SDK package lines are pre-release (alpha); pin exact versions and validate against your target server before broad rollout.

## Deployment tooling

[honua-helm](https://github.com/honua-io/honua-helm) and the private Terraform modules (honua-iac, available to customers) package the server for Kubernetes and for AWS/Azure infrastructure respectively. They are infrastructure surfaces around the same container image documented in [Architecture](architecture.md); start with the [deployment guides](../guides/deploy/docker-compose.md).
