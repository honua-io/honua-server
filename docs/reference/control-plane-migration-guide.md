---
type: reference
title: "Control Plane API Migration Guide"
description: "How to generate a client from the checked-in admin OpenAPI contract, which paths have been removed and what replaced them, and how breaking changes reach you."
---
# Control Plane API Migration Guide

This guide covers migration for the Honua control-plane/admin API only.

This guide covers the Honua control-plane (admin) API only. For SDK support windows and
release-channel expectations, see [Server + SDK compatibility](../concepts/ecosystem.md).

## Withdrawal of the COG publication endpoint

The revert of PR #5027 in PR #5042 withdraws the newly added
`POST /api/v1/admin/raster-artifacts/cog` endpoint and its
`CogArtifactDescriptor` and `CogArtifactPublishRequest` contract schemas.
The landing caused regressions in the trailing integration matrix, so its
additions are being withdrawn together before they are reintroduced.

Clients built against that trunk revision must stop calling this endpoint and
regenerate from the restored admin contract. There is no replacement admin
endpoint for this publication operation in the restored contract; defer workflows
that depend on it until a validated implementation is available. Existing raster
import and serving operations retain their previous contracts.

## Migration Baseline

Before regenerating or upgrading SDK artifacts:

1. Confirm the target server release channel and admin API major.
2. Select the matching SDK line for JavaScript/TypeScript, Python, or .NET.
3. Review release notes for admin contract changes, deprecations, auth changes,
   and SDK regeneration requirements.
4. Continue with the generation and validation steps below.

## Generate a client

The first-party SDKs (`honua-admin` on PyPI, `@honua/sdk-js` on npm, `Honua.Sdk.Admin` on
NuGet) already wrap this API. To generate your own client, use the checked-in admin
OpenAPI contract with [OpenAPI Generator](https://openapi-generator.tech/):

```bash
openapi-generator generate -i docs/developer/api-specs/admin-api.json -g typescript-fetch -o ./honua-admin-client
```

The same file is served by a running deployment at `GET /api/v1/admin/openapi.json`. The
examples below show generated clients of that shape.

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

## When a breaking change ships

Breaking changes to the admin API arrive only in a new major path (`/api/v2/admin/*`),
except for emergency security fixes, and are announced in release notes with their
replacements ([versioning and support](versioning-and-support.md)). After upgrading:

1. Read the release notes for admin contract changes, deprecations and authentication changes.
2. Regenerate your client from the new contract and update your integrations.
3. Verify write-path behaviour against your automation (publish, update and import operations).

## Removed Contract Paths

Endpoints removed from the published admin OpenAPI spec because they were already
removed at runtime. Clients calling these paths already receive `404`; removing them from
the spec prevents generated clients from emitting calls that cannot succeed.

| Removed path/schema | Replacement |
|---|---|
| `POST /api/v1/admin/manifest/apply` (`ManifestApplyRequest`, `ApiResponseManifestApplyResult`, `ManifestApplyResult`, `ManifestApplySummary`, `ManifestApplyEntry`) | GitOps release manifests; no mutating manifest-apply route remains |
| `GET /api/v1/admin/manifest` (`ApiResponseMetadataManifest`, `MetadataManifest`) | Read-only `GET /api/v1/admin/metadata/release-packages/{packageId}/gitops-manifest` and `GET /api/v1/capabilities/manifest` |
| `GET`/`POST`/`PUT`/`DELETE` `/api/v1/admin/gitops/watch`, `GET /api/v1/admin/gitops/changes`, `GET /api/v1/admin/gitops/changes/{id}`, `GET /api/v1/admin/gitops/changes/{id}/diff` (`ApiResponseGitOpsWatchConfigResponse`, `GitOpsWatchConfigRequest`, `GitOpsWatchConfigResponse`, `ApiResponseGitOpsChangeRecordResponse`, `ApiResponseGitOpsChangeRecordResponseArray`, `GitOpsChangeRecordResponse`, `ApiResponseGitOpsChangeDiffResponse`, `GitOpsChangeDiffResponse`) | GitOps release packages under `/api/v1/admin/metadata/release-packages/**` |
| `GET`/`POST` `/api/v1/admin/metadata/resources`, `GET`/`PUT`/`DELETE` `/api/v1/admin/metadata/resources/{kind}/{namespace}/{name}` (`ApiResponseMetadataResource`, `ApiResponseMetadataResourceArray`, `MetadataResource`, `MetadataResourceIdentifier`, `ResourceMetadata`) | Metadata v2 release packages and the layer authoring routes under `/api/v1/admin/metadata/layers/**` |


## Deprecation rules

Deprecations follow the [versioning and support policy](versioning-and-support.md):
replacements are announced and documented, deprecated operations are preserved through the
grace period and answer with `Deprecation` and `Sunset` headers, and removal happens only in
the next major path.
