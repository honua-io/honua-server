# Control Plane API Migration Guide

This guide covers migration for the Honua control-plane/admin API only.

For SDK-specific support windows and release-channel expectations, see
[Server + SDK Compatibility Matrix](../concepts/ecosystem.md). For the
per-repository release-note and migration-guide template, see
[SDK Migration Guide Template](../internal/developer/sdk-migration-template.md).

## Migration Baseline

Before regenerating or upgrading SDK artifacts:

1. Confirm the target server release channel and admin API major.
2. Select the matching SDK line for JavaScript/TypeScript, Python, or .NET.
3. Review release notes for admin contract changes, deprecations, auth changes,
   and SDK regeneration requirements.
4. Continue with the generation and validation steps below.

## Quickstart: Generate SDKs

Validate contract and generate SDK artifacts from the curated admin OpenAPI spec:

```bash
./scripts/ci/validate-openapi-contracts.sh
./scripts/sdk/generate-control-plane-sdks.sh
```

Artifacts are written to `artifacts/control-plane-sdks/`:
- TypeScript (`typescript-fetch`) tarball
- Python tarball
- .NET C# tarball
- `manifest.json` and `SHA256SUMS.txt`

CI also generates these artifacts in `control-plane-sdk-governance.yml`, and release builds attach them to release assets.

## SDK Usage Examples

### TypeScript

```ts
import { Configuration, ConnectionsApi } from "./typescript";

const config = new Configuration({
  basePath: "https://your-honua.example.com/api/v1/admin",
  headers: { "X-API-Key": process.env.HONUA_ADMIN_API_KEY ?? "" }
});

const api = new ConnectionsApi(config);
const connections = await api.getConnections();
console.log(connections);
```

### Python

```python
from honua_control_plane_sdk import Configuration, ApiClient
from honua_control_plane_sdk.api.connections_api import ConnectionsApi

config = Configuration(
    host="https://your-honua.example.com/api/v1/admin"
)
config.api_key["ApiKeyAuth"] = "your-api-key"

with ApiClient(config) as client:
    api = ConnectionsApi(client)
    print(api.get_connections())
```

### .NET (C#)

```csharp
using Honua.ControlPlane.Sdk.Api;
using Honua.ControlPlane.Sdk.Client;

var config = new Configuration
{
    BasePath = "https://your-honua.example.com/api/v1/admin",
    DefaultHeaders = { ["X-API-Key"] = "your-api-key" }
};

var api = new ConnectionsApi(config);
var connections = api.GetConnections();
Console.WriteLine(connections);
```

## Breaking Change Upgrade Flow

1. Detect breakage early:

```bash
OPENAPI_BASE_REF=origin/trunk ./scripts/ci/validate-openapi-contracts.sh
```

2. If breakage is intentional, update:
- `docs/developer/SDK_COMPATIBILITY_MATRIX.md`
- `docs/developer/CONTROL_PLANE_VERSIONING_POLICY.md`
- `docs/operator/CONTROL_PLANE_API.md`
- release checklist compatibility notes

3. Regenerate SDK artifacts and update client integrations.

4. Verify write-path behavior against your automation workflows (publish/update/import operations).

## Removed Contract Paths

Endpoints removed from the published admin OpenAPI spec because they were already
removed at runtime (spec-vs-route honesty corrections, not live-surface removals).
`validate-openapi-contracts.sh` reports these as breaking by design — they are
acknowledged intentional removals; a maintainer greens the governance lane by
running with `OPENAPI_ALLOW_BREAKING_CHANGES=true`.

| Removed path/schema | Removed at runtime in | Correction | Replacement |
|---|---|---|---|
| `POST /api/v1/admin/manifest/apply` (`ManifestApplyRequest`, `ApiResponseManifestApplyResult`, `ManifestApplyResult`, `ManifestApplySummary`, `ManifestApplyEntry`) | #1035 metadata cutover | #2822 | GitOps release manifests; no mutating manifest-apply route remains |
| `GET /api/v1/admin/manifest` (`ApiResponseMetadataManifest`, `MetadataManifest`) | #1035 metadata cutover | #2822 | Read-only `GET /api/v1/admin/metadata/release-packages/{packageId}/gitops-manifest` and `GET /api/v1/capabilities/manifest` |
| `GET`/`POST`/`PUT`/`DELETE` `/api/v1/admin/gitops/watch`, `GET /api/v1/admin/gitops/changes`, `GET /api/v1/admin/gitops/changes/{id}`, `GET /api/v1/admin/gitops/changes/{id}/diff` (`ApiResponseGitOpsWatchConfigResponse`, `GitOpsWatchConfigRequest`, `GitOpsWatchConfigResponse`, `ApiResponseGitOpsChangeRecordResponse`, `ApiResponseGitOpsChangeRecordResponseArray`, `GitOpsChangeRecordResponse`, `ApiResponseGitOpsChangeDiffResponse`, `GitOpsChangeDiffResponse`) | #1035 cutover (see the `EndpointRegistry.AdminAlerts` removal comment) | #3051 | GitOps release packages under `/api/v1/admin/metadata/release-packages/**` |
| `GET`/`POST` `/api/v1/admin/metadata/resources`, `GET`/`PUT`/`DELETE` `/api/v1/admin/metadata/resources/{kind}/{namespace}/{name}` (`ApiResponseMetadataResource`, `ApiResponseMetadataResourceArray`, `MetadataResource`, `MetadataResourceIdentifier`, `ResourceMetadata`) | #1035 cutover (see the `EndpointRegistry.AdminMetadata` removal comment) | #3051 | Metadata v2 release packages and the layer authoring routes under `/api/v1/admin/metadata/layers/**` |

Clients calling these paths already receive `404`; removing them from the spec
prevents generated SDKs from emitting calls that cannot succeed.

## Deprecation Rules

Deprecations must follow `docs/developer/CONTROL_PLANE_VERSIONING_POLICY.md`:
- announce and document replacements
- preserve deprecated operations during grace period
- remove only in next major path (except emergency security cases)

## SDK Handoff Baseline

When a server change affects generated control-plane clients or SDK runtime
checks, update all three places in the same rollout window:
- this migration guide
- [Server + SDK Compatibility Matrix](../concepts/ecosystem.md)
- [SDK Migration Guide Template](../internal/developer/sdk-migration-template.md)
- the affected SDK changelog and migration notes
