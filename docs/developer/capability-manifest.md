# Capability Manifest

`GET /api/v1/capabilities/manifest` returns the server capability manifest for the current request scope. It is a neutral discovery contract for Console, MCP, QGIS plugins, native hosts, and SDK clients. It is not an authorization grant, and it does not replace the admin-only `GET /api/v1/admin/capabilities` endpoint.

## Request

| Part | Value |
|---|---|
| Method | `GET` |
| Path | `/api/v1/capabilities/manifest` |
| Auth | Optional; anonymous callers receive the public/default tenant view |
| Query | `environment`, `workspaceId` |
| Response types | `application/json`, `application/vnd.honua.capability-manifest+json` |
| Cache policy | `Cache-Control: no-store`, `Pragma: no-cache`, `Expires: 0` |

`environment` and `workspaceId` are optional scope hints. When supplied, each value is trimmed and must be non-empty, 128 characters or fewer, and limited to ASCII letters, digits, `-`, `_`, `.`, `:`, and `@`. Invalid identifiers return a safe `400` response without provider details. Unknown or currently unavailable environments do not fail the request; the manifest returns `environment.available=false` and marks environment-scoped capabilities unavailable with `reasonCode=environment-unavailable`.

The endpoint returns `application/vnd.honua.capability-manifest+json` when the `Accept` header contains that media type. All other successful requests return `application/json`.

## Document Shape

The response has `schemaVersion=honua.capability_manifest.v1` and includes:

| Property | Purpose |
|---|---|
| `issuedAt` | UTC timestamp for the generated view. |
| `scope` | Tenant source, tenant id, optional environment/workspace, workspace availability, and authentication state. |
| `server` | Server/API/metadata schema version and deployment environment. |
| `environment` | Requested environment state, current revision, and availability reason when unavailable. |
| `packages` | Supported metadata, release, GitOps, map, app, storage, and publication package families. |
| `capabilities` | Flat capability records with `id`, `category`, `supported`, `available`, `reasonCode`, entitlement key, minimum edition, and UI message key. |
| `transports` | REST, GeoServices, OGC HTTP, OData, STAC, tiles, gRPC, gRPC-Web, native gRPC, MCP, QGIS, and mTLS transport availability. |
| `limits` | Runtime limits for previews, query, analysis, publication, jobs, uploads, streaming, edits, geometry, and attachments. |
| `policies` | License/entitlement state, caller capability strings, and the non-authorizing manifest notice. |
| `links` | Related surfaces, including the manifest itself, feature streaming capabilities, and admin capabilities. |

Reason codes are stable client-facing strings:

| Reason | Meaning |
|---|---|
| `unsupported` | The deployed server does not register the backing service. |
| `disabled-by-configuration` | The feature exists but is disabled by server configuration. |
| `license-required` | The current license state or edition does not satisfy the feature. |
| `entitlement-inactive` | The edition can support the feature, but the entitlement is inactive. |
| `insufficient-policy` | The caller is unauthenticated, lacks the required policy capability, or cannot use the requested workspace. |
| `environment-unavailable` | The requested environment snapshot is absent or unavailable. |
| `workspace-scope-required` | The capability needs a workspace scope and none was supplied. |

Workspace-scoped requests report `scope.workspaceAvailable` and, when unavailable, `scope.workspaceReasonCode=insufficient-policy`. Workspace-required capabilities report `workspace-scope-required` when no workspace was supplied and `insufficient-policy` when the caller cannot use the requested workspace.

## Projection Notes

Clients should treat `supported=false` as a reason to hide or explain controls that cannot work on this server build. Treat `supported=true` and `available=false` as a state that may become available after policy, license, environment, workspace, or configuration changes. Do not infer authorization from `available=true`; operation endpoints still enforce RBAC, tenant, license, resource, and environment checks.

SDKs should expose the raw manifest plus helper lookups by `capabilities[].id` and `transports.items[].id`. Console can use `messageKey` for copy selection and `reasonCode` for deterministic control state. MCP, QGIS, and native hosts should read the same document rather than maintaining separate feature matrices.

The manifest is generated per request. Do not persist it as an authorization cache; refresh it when tenant, workspace, environment, user, license, or server configuration changes.
