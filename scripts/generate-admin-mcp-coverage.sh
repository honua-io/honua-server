#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

echo "Regenerating docs/developer/api-specs/admin-mcp-coverage.v1.json from the integrated Admin OpenAPI and MCP catalogs..."

HONUA_EMIT_ADMIN_MCP_COVERAGE=1 dotnet test \
  tests/dotnet/Honua.Ai.Tests/Honua.Ai.Tests.csproj \
  --filter "FullyQualifiedName~AdminMcpCoverageArtifactTests" \
  "$@"

echo "Done. Review and commit docs/developer/api-specs/admin-mcp-coverage.v1.json."
