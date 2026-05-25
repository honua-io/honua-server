# Capability Manifest

`GET /api/v1/capabilities/manifest` returns the server capability manifest for the current request scope. It is a neutral discovery contract for Console, MCP, QGIS plugins, native hosts, and SDK clients. It is not an authorization grant, and it does not replace the admin-only `GET /api/v1/admin/capabilities` endpoint used by control-plane SDKs.

## Request

| Part | Value |
|---|---|
| Method | `GET` |
| Path | `/api/v1/capabilities/manifest` |
| Auth | Optional; anonymous callers receive the public/default tenant view |
| Query | `environment`, `workspaceId` |
| Response types | `application/json`, `application/vnd.honua.capability-manifest+json` |
| Cache policy | `Cache-Control: no-store`, `Pragma: no-cache`, `Expires: 0` |

`environment` and `workspaceId` are optional scope hints. When supplied they are trimmed and must be non-empty, 128 characters or fewer, and limited to ASCII letters, digits, `-`, `_`, `.`, `:`, and `@`. Empty, oversized, or unsafe identifiers return a safe `400` response. Unknown or currently unavailable environments do not fail the request; the manifest returns `environment.available=false` and marks environment-scoped capabilities unavailable with `reasonCode=environment-unavailable`.

Clients can request the Honua media type when they want to distinguish this contract from generic JSON:

```bash
curl -H "Accept: application/vnd.honua.capability-manifest+json" \
  "https://your-honua-server.example/api/v1/capabilities/manifest?environment=prod&workspaceId=field-team"
```

If the `Accept` header contains `application/vnd.honua.capability-manifest+json`, the response uses that content type. Otherwise the endpoint returns `application/json`.

## Document Shape

The response has `schemaVersion=honua.capability_manifest.v1` and includes:

| Property | Purpose |
|---|---|
| `issuedAt` | UTC timestamp for the generated view. |
| `scope` | Tenant source, tenant id, optional environment/workspace, workspace availability, and authentication state. |
| `server` | Server/API/metadata schema version and deployment environment. |
| `environment` | Requested environment state, current revision, and availability reason when unavailable. |
| `packages` | Supported metadata, release, GitOps, map, app, storage, and publication package families. |
| `capabilities` | Flat capability records with `id`, `category`, `supported`, `available`, `reasonCode`, entitlement key(s), minimum edition, and UI message key. |
| `transports` | REST, GeoServices, OGC HTTP, OData, STAC, tiles, gRPC, gRPC-Web, native gRPC, MCP, QGIS, and mTLS transport availability. |
| `limits` | Runtime limits for previews, query, analysis, publication, jobs, uploads, streaming, edits, geometry, and attachments. |
| `policies` | License/entitlement state, caller capability strings, and the non-authorizing manifest notice. |
| `links` | Related surfaces, including the manifest itself, feature streaming capabilities, and admin capabilities. |

Optional nullable fields are omitted when null. Clients should treat an absent optional field the same as `null`.

Example response excerpt, with required sections such as `packages` and `limits` omitted for brevity:

```json
{
  "schemaVersion": "honua.capability_manifest.v1",
  "issuedAt": "2026-05-24T21:00:00Z",
  "scope": {
    "tenantId": "tenant-a",
    "tenantSource": "Default",
    "environment": "prod",
    "workspaceId": "field-team",
    "workspaceAvailable": true,
    "authenticated": true
  },
  "server": {
    "serverVersion": "1.0.0",
    "apiVersion": "v1",
    "metadataApiVersion": "metadata.honua.io/v2alpha1",
    "metadataSchemaVersion": "2.0.0-alpha.1",
    "deploymentEnvironment": "Production"
  },
  "environment": {
    "environmentId": "prod",
    "requested": true,
    "available": true,
    "revision": 42,
    "loadedAt": "2026-05-24T20:55:00Z"
  },
  "capabilities": [
    {
      "id": "publication.metadata-release",
      "category": "publication",
      "supported": true,
      "available": true,
      "messageKey": "capabilities.publication.metadata-release.available"
    }
  ],
  "transports": {
    "items": [
      {
        "id": "mcp",
        "supported": true,
        "available": true,
        "messageKey": "transports.mcp.available"
      }
    ],
    "mtlsMode": "optional",
    "forwardedClientCertificateEnabled": false
  },
  "policies": {
    "currentEdition": "Pro",
    "licenseValidationState": "Valid",
    "licenseValid": true,
    "callerCapabilities": ["catalog.publish"],
    "entitlements": [
      {
        "key": "import.file",
        "active": true,
        "minimumEdition": "Pro"
      }
    ],
    "authorizationNotice": "Manifest availability is informational only; operation endpoints remain the source of truth for authorization, tenant, environment, license, and resource checks."
  },
  "links": [
    {
      "rel": "self",
      "href": "/api/v1/capabilities/manifest",
      "type": "application/vnd.honua.capability-manifest+json"
    }
  ]
}
```

## Capability IDs

The initial `honua.capability_manifest.v1` document emits these capability ids:

| Category | IDs |
|---|---|
| `packages` | `package.metadata-v2`, `package.release-package`, `package.gitops-manifest`, `package.map`, `package.app` |
| `temporal` | `temporal.filtering`, `temporal.extent-discovery`, `temporal.histogram`, `temporal.time-series-tiles` |
| `sync` | `sync.offline` |
| `realtime` | `realtime.feature-streams` |
| `alerts` | `alerts.geofence` |
| `jobs` | `jobs.runner` |
| `gitops` | `gitops.release-manifest` |
| `transports` | `transport.grpc`, `transport.grpc-web`, `transport.native-grpc`, `transport.mcp`, `transport.qgis` |
| `security` | `security.mtls` |
| `preview` | `preview.file-import` |
| `query` | `query.features` |
| `analysis` | `analysis.spatial` |
| `publication` | `publication.metadata-release` |
| `upload` | `upload.file` |
| `edit` | `edit.features` |

Each capability record uses:

| Field | Meaning |
|---|---|
| `supported` | Whether this server build has the backing implementation registered. |
| `available` | Whether the capability is usable for this request after configuration, environment, workspace, authentication, license, entitlement, and policy checks. |
| `reasonCode` | Present only when unavailable. Use this for deterministic control state. |
| `entitlementKey` | Present for edition-gated capabilities with a single runtime entitlement gate. |
| `entitlementKeys` | Present for aggregate capabilities that require multiple runtime entitlements. |
| `minimumEdition` | Present when an entitlement has a known minimum edition. |
| `messageKey` | Stable UI copy key. Available capabilities use `capabilities.{id}.available`; unavailable capabilities use `capabilities.{id}.{reasonCode}`. |

Transport ids under `transports.items` are `rest-http`, `geoservices-rest`, `ogc-http`, `odata`, `stac`, `tiles`, `grpc`, `grpc-web`, `native-grpc`, `mcp`, `qgis`, and `mtls`. Transport message keys use the `transports.{id}.*` prefix. The top-level `transports.mtlsMode` value is one of `disabled`, `optional`, `required-for-native`, `required-for-admin`, or `required-for-environment`.

Package families under `packages.families` are `metadata-v2-graph`, `metadata-release-package`, `gitops-metadata-release-manifest`, `map-package`, and `app-package`. `packages.storageFamilies` and `packages.publicationFamilies` are generated from the Metadata v2 enum values so clients can render storage/publication choices without duplicating server-side lists.

`analysis.spatial` is an aggregate spatial analytics capability. It requires `features.query` policy and all four endpoint entitlements: `analytics.clustering`, `analytics.spatial-join`, `analytics.buffer-aggregate`, and `analytics.density`. If any required analytics entitlement is inactive, the aggregate capability is unavailable and the individual entitlement states remain visible under `policies.entitlements`.

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

## Analysis Limits

The `limits.analysis` block mirrors the spatial analytics endpoint validators. Meter-based fields are expressed in meters after any endpoint-specific unit conversion:

| Field | Meaning |
|---|---|
| `maxInputFeatures` | Maximum input feature count processed by analytics queries. |
| `maxClusters` | Maximum cluster count returned by clustering queries. |
| `maxDbscanEpsMeters` | Maximum DBSCAN epsilon distance. |
| `maxKMeansK` | Maximum K-Means partition count. |
| `maxBufferDistanceMeters` | Maximum buffer distance after unit conversion. |
| `minDensityCellSizeMeters` | Minimum density cell size. |
| `maxDensityCellSizeMeters` | Maximum density cell size. |
| `maxDensityCells` | Maximum density cell count returned by density queries. |
| `maxDWithinDistanceMeters` | Maximum spatial-join `dwithin` distance. |
| `maxH3CellsPerQuery` | Maximum H3 cells per query for H3-enabled query surfaces. |
| `maxSpatialOperations` | Maximum spatial operations allowed in query filters. |
| `maxJoins` | Maximum joins allowed by the shared query limit policy. |
