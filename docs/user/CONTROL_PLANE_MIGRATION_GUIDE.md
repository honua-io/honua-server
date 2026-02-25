# Control Plane API Migration Guide

This guide covers migration for the Honua control-plane/admin API only.

## Quickstart: Generate SDKs

Validate contract and generate SDK artifacts from the curated admin OpenAPI spec:

```bash
./scripts/validate-openapi-contracts.sh
./scripts/generate-control-plane-sdks.sh
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
OPENAPI_BASE_REF=origin/trunk ./scripts/validate-openapi-contracts.sh
```

2. If breakage is intentional, update:
- `docs/user/CONTROL_PLANE_VERSIONING_POLICY.md`
- `docs/user/CONTROL_PLANE_API.md`
- release checklist compatibility notes

3. Regenerate SDK artifacts and update client integrations.

4. Verify write-path behavior against your automation workflows (publish/update/import operations).

## Deprecation Rules

Deprecations must follow `docs/user/CONTROL_PLANE_VERSIONING_POLICY.md`:
- announce and document replacements
- preserve deprecated operations during grace period
- remove only in next major path (except emergency security cases)
