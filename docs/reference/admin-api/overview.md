# Admin API overview

The admin API is Honua's control-plane REST surface at `/api/v1/admin/*`. It powers the Admin UI and Honua Console and supports headless automation: connections, layer publishing, imports, styles, access control, and licensing. It is separate from the geospatial data-access APIs (OGC, GeoServices, OData).

## Authentication

All admin endpoints require authentication. Send `X-API-Key` with the admin password or a scoped API key, or `Authorization: Bearer <jwt>` when OIDC is enabled. A valid mapped client certificate can also authenticate when mTLS is configured. See [Authentication](../../guides/secure/authentication.md).

## Endpoint groups

| Group | Base path | Reference |
|---|---|---|
| Connections, tables, layers, service settings | `/api/v1/admin/connections`, `/api/v1/admin/services` | [Connections and layers](connections-and-layers.md) |
| File, URL, migration, and raster imports; jobs | `/api/v1/admin/import`, `/api/v1/admin/operations`, `/api/v1/admin/jobs`, `/api/v1/admin/tile-operations` | [Imports and jobs](imports-and-jobs.md) |
| Layer styles, SLD, suggestions, themes | `/api/v1/admin/metadata/layers/{layerId}/style` | [Styles](styles.md) |
| API keys, roles, users, OIDC providers, license | `/api/v1/admin/api-keys`, `/api/v1/admin/roles`, `/api/v1/admin/users`, `/api/v1/admin/oidc`, `/api/v1/admin/license` | [Users, roles, and licensing](users-roles-licensing.md) |
| Form packages and submissions | `/api/v1/admin/forms`, `/api/v1/forms` | [Forms](forms.md) |
| Deploy operations and rollback | `/api/v1/admin/deploy` | [Upgrade and rollback](../../guides/deploy/upgrade-and-rollback.md) |
| Operations, observability, alerts | `/api/v1/admin/observability`, `/api/v1/admin/alerts` | [Operations](../../guides/deploy/backup-and-restore.md), [Monitoring](../../guides/deploy/monitoring.md) |
| 3D scene registry and generation | `/api/v1/admin/scenes` | [Publish 3D scenes](../../guides/publish/publish-3d-scenes.md) |
| Runtime configuration reference | `/api/v1/admin/config` | [Environment variables](../configuration/environment-variables.md) |

Console control-plane surfaces (`/api/v1/console/*`, including workflow packages) use the same admin authorization posture.

## Capability manifest

`GET /api/v1/capabilities/manifest` returns a neutral runtime capability manifest for Console, MCP, QGIS, native hosts, and SDK clients. Authentication is optional; anonymous callers receive the public/default view. Optional `environment` and `workspaceId` query parameters scope the result.

```bash
HONUA_URL=https://honua.example.com
curl "$HONUA_URL/api/v1/capabilities/manifest?environment=prod"
```

Each capability record reports:

| Field | Meaning |
|---|---|
| `supported` | The server build registers the backing implementation. |
| `available` | Usable for this request after configuration, environment, authentication, license, and policy checks. |
| `reasonCode` | Present only when unavailable; stable values include `unsupported`, `disabled-by-configuration`, `license-required`, `entitlement-inactive`, `insufficient-policy`, `environment-unavailable`, and `workspace-scope-required`. |

The document also carries `transports` (REST, GeoServices, OGC, OData, STAC, tiles, gRPC, MCP, QGIS, mTLS), `limits` (query, analysis, upload, and job limits), and `policies` (license and entitlement state). The manifest is informational only — operation endpoints remain the source of truth for authorization and resource checks. Do not persist it as an authorization cache.

Control-plane SDKs should instead call the admin-only `GET /api/v1/admin/capabilities` once per session and branch on its `data.compatibility` object (server version, control-plane major, feature flags).

## OpenAPI specs

| Spec | Location |
|---|---|
| Runtime admin API spec | `GET /api/v1/admin/openapi.json` |
| Checked-in bundle (pinned, used for SDK generation) | [`docs/developer/api-specs/admin-api.json`](../../developer/api-specs/admin-api.json) |

## Versioning

The admin API follows the control-plane versioning and deprecation policy in [Versioning and support](../versioning-and-support.md).
