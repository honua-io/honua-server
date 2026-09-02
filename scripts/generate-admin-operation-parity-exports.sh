#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

echo "Regenerating authoritative Admin OpenAPI and MCP projection exports..."

HONUA_EMIT_ADMIN_PARITY_EXPORTS=1 dotnet test \
  tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
  --filter "FullyQualifiedName~AdminOperationParityExportTests.AdminParityExports_EmitWhenExplicitlyRequested" \
  "$@"

echo "Done. Review and commit docs/gis/data/admin-openapi-operation-ids.json and docs/gis/data/admin-mcp-projection-manifest.json."
