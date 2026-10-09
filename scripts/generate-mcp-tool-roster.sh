#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

echo "Regenerating the canonical MCP tool roster..."

# serverSha is restamped only when the roster body changes.
HONUA_EMIT_MCP_TOOL_ROSTER=1 \
HONUA_MCP_TOOL_ROSTER_SERVER_SHA="$(git rev-parse HEAD)" \
dotnet test \
  tests/dotnet/Honua.Ai.Tests/Honua.Ai.Tests.csproj \
  --filter "FullyQualifiedName~McpToolRosterDriftTests.McpToolRoster_EmitsWhenExplicitlyRequested" \
  "$@"

echo "Done. Review and commit docs/gis/data/mcp-tool-roster.v1.json."
