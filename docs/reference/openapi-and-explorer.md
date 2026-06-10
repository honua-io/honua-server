# OpenAPI and the API explorer

Honua serves OpenAPI documents at runtime for each protocol surface, ships pinned spec bundles in the repository, and can host an interactive explorer at `/docs`. This page lists each and when to use which.

## Runtime OpenAPI endpoints

| Path | Describes |
| --- | --- |
| `GET /openapi.json` | OGC API Features (the default document). |
| `GET /ogc/coverages/openapi.json` | OGC API Coverages. |
| `GET /ogc/tiles/openapi.json` | OGC API Tiles. |
| `GET /ogc/maps/openapi.json` | OGC API Maps. |
| `GET /ogc/styles/openapi.json` | OGC API Styles. |
| `GET /ogc/processes/openapi.json` | OGC API Processes. |
| `GET /stac/openapi.json` | STAC API. |
| `GET /api/v1/admin/openapi.json` | Admin/control-plane API v1. |

Runtime documents reflect the running build and base URL — use them for client generation against a specific deployment and for OGC conformance tooling (the `service-desc` links in each landing page point at them).

## Interactive explorer (`/docs`)

A Scalar-based explorer is mapped at `/docs` with documents for Features, Coverages, Tiles, Maps, Processes, and the Admin API.

| Variable | Default | Purpose |
| --- | --- | --- |
| `HONUA_SERVE_API_DOCS` (`ServeApiDocs`) | `true` in Development, `false` otherwise | Serve the explorer at `/docs`. |

Set `HONUA_SERVE_API_DOCS=true` to enable it in staging/production deployments; it is read-only UI over the runtime documents above.

## Checked-in spec bundles

Pinned, reviewed OpenAPI bundles live at [`docs/developer/api-specs/`](../developer/api-specs/README.md):

| File | Contract |
| --- | --- |
| `admin-api.json` | Admin API v1 — the governance baseline; CI compares the runtime contract against it and fails on breaking changes ([versioning policy](versioning-and-support.md)). SDKs are generated from this file. |
| `ogc-api-features.json` | OGC API Features. |
| `ogc-api-tiles.json` | OGC API Tiles. |
| `ogc-api-coverages.json` | OGC API Coverages. |

## When to use which

| Need | Use |
| --- | --- |
| Generate a client for your deployment | Runtime `*/openapi.json` from that deployment. |
| Generate the official admin SDKs / diff for breaking changes | Checked-in `docs/developer/api-specs/admin-api.json`. |
| Explore and try requests interactively | `/docs` (enable `HONUA_SERVE_API_DOCS`). |
| Audit what changed between releases | Diff the checked-in bundles across tags. |

## Related pages

- [Versioning and support](versioning-and-support.md)
- [Admin API overview](admin-api/overview.md)
- [Control plane migration guide](control-plane-migration-guide.md) — SDK generation from the admin spec.
