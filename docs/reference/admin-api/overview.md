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
| Operations, observability, customer alerts (Preview) | `/api/v1/admin/observability`, `/api/v1/admin/alerts` | [Operations](../../guides/deploy/backup-and-restore.md), [Monitoring](../../guides/deploy/monitoring.md) |
| 3D scene registry and generation | `/api/v1/admin/scenes` | [Publish 3D scenes](../../guides/publish/publish-3d-scenes.md) |
| Runtime configuration reference | `/api/v1/admin/config` | [Environment variables](../configuration/environment-variables.md) |

Console control-plane surfaces (`/api/v1/console/*`, including workflow packages) use the same admin authorization posture.

## Capability manifest

`GET /api/v1/capabilities/manifest` returns a neutral runtime capability manifest for Console, MCP, QGIS, native hosts, and SDK clients. Authentication is optional; anonymous callers receive the public/default view. Optional `environment` and `workspaceId` query parameters scope the result.

> Open `/api/v1/capabilities/manifest?environment=prod` in a browser.

Each capability record reports:

| Field | Meaning |
|---|---|
| `supported` | The server build registers the backing implementation. |
| `available` | Usable for this request after configuration, environment, authentication, license, and policy checks. |
| `reasonCode` | Present only when unavailable; stable values include `unsupported`, `experimental-disabled`, `disabled-by-configuration`, `license-required`, `entitlement-inactive`, `insufficient-policy`, `environment-unavailable`, and `workspace-scope-required`. Preview surfaces are opt-in through `Capabilities:Experimental:<capability-id>:Enabled=true`. |
| `lifecycle` | Product lifecycle classification. Customer alerting (`alerts.geofence`), realtime feature streams, and SensorThings report `preview` for 2026.1. Alerting qualification evidence does not promote it to GA. |
| `optInRequired` | Whether the capability must be explicitly enabled. Preview realtime capabilities remain declared but unavailable with `disabled-by-configuration` until opted in. |

The document also carries `transports` (REST, GeoServices, OGC, OData, STAC, tiles, gRPC, MCP, QGIS, mTLS), `limits` (query, analysis, upload, and job limits), and `policies` (license and entitlement state). The manifest is informational only — operation endpoints remain the source of truth for authorization and resource checks. Do not persist it as an authorization cache.

Customer alert zones, rules, evaluation, and delivery channels are Preview in
2026.1. Enable both `Capabilities:Experimental:alerts.geofence:Enabled=true` and
`Alerts:Enabled=true` before use. Startup reports the Preview opt-in through the
existing feature-status log event; this opt-in does not constitute a GA
availability or support commitment.

The [2026.1 operator ruling](https://github.com/honua-io/honua-release/issues/268)
does not relax the mandatory [domain-audit integrity](https://github.com/honua-io/honua-server/issues/3865)
or [fail-closed tenant isolation](https://github.com/honua-io/honua-server/issues/3859)
release gates on this Preview surface.

Control-plane SDKs should instead call `GET /api/v1/admin/capabilities` once per session and branch on its `data.compatibility` object (server version, control-plane major, feature flags). The capabilities handshake is readable anonymously so `checkCompatibility()` can run before credentials exist; every other admin endpoint requires authentication.

## OpenAPI specs

| Spec | Location |
|---|---|
| Runtime admin API spec | `GET /api/v1/admin/openapi.json` |
| Checked-in bundle (pinned, used for SDK generation) | [`docs/developer/api-specs/admin-api.json`](../../developer/api-specs/admin-api.json) |

## Versioning

The admin API follows the control-plane versioning and deprecation policy in [Versioning and support](../versioning-and-support.md).

## Reviewing Admin operation proposals

New Admin operation proposals include the HTTP operation, accepted tenant,
connection/service target, selected fields, and declared parameter values in the
reviewable `diff`. For example, a layer-filter proposal identifies the layer and
its proposed permanent-filter expression. The review distinguishes dry-run
validation from execution. Approval replay verifies the complete plan seal
internally; neither REST nor MCP exposes that private seal or replay payload.

Credentials, opaque bodies, malformed JSON, and undeclared values are marked as
redacted. Known secret references remain visible so the reviewer can identify the
selected credential without seeing its secret. URL credentials and query strings
are removed from displayed URLs. Review warnings identify these omissions; the
complete execution payload is not returned by proposal-detail endpoints.

Proposals created before this projection was available retain their original
sealed plan. Re-propose such work to obtain the target-and-value review rather
than relying on a generic legacy summary.

## Canonical operation requests

Admin release and operate requests use the required inputs declared by the
operation catalog. Validation and dry-run reject missing or blank required
values, missing required fields within structured inputs, and missing conditional
cache targets. Metadata prevalidation requires a target environment and exactly
one persisted package ID or inline release package. This input validation does
not establish that a referenced package exists or replace live compatibility
prevalidation.

Supply declared text directly: a title of `2026`, `true`, or `null` stays a JSON
string when sent to the Admin API. Supply numbers, booleans, arrays, and objects
as JSON in the operation parameter value. Pagination and service filters are
forwarded as query parameters, including values containing reserved characters.

Operate validation also enforces declared scalar types, enum and format constraints,
and explicit nullability of supplied objects, collections, and collection entries.
Optional nullable values remain valid when their target scope does not require them.
