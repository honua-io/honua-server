#!/usr/bin/env python3
"""Fail-closed roster and field-equality gate for Admin operation projections."""

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / "docs" / "gis" / "data"
OPENAPI = DATA / "admin-openapi-operation-ids.json"
MCP = DATA / "admin-mcp-projection-manifest.json"


def fail(message: str) -> None:
    print(json.dumps({"roster": {"status": "blocked", "reason": message}}, sort_keys=True))
    raise SystemExit(1)


def load(path: Path) -> dict:
    if not path.is_file():
        fail(f"missing committed export: {path.relative_to(ROOT)}")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        fail(f"invalid committed export {path.relative_to(ROOT)}: {error}")


openapi = load(OPENAPI)
mcp = load(MCP)

expected_exports = [
    str(OPENAPI.relative_to(ROOT)),
    str(MCP.relative_to(ROOT)),
]
roster = mcp.get("roster")
if not isinstance(roster, dict):
    fail("MCP roster is not wired to the committed authoritative exports")
if roster.get("status") != "ready" or roster.get("exports") != expected_exports:
    fail("MCP roster does not declare the exact committed exports ready")

surface_fields = {
    "cli": openapi.get("generatedCliFields"),
    "console": openapi.get("generatedConsoleFields"),
    "catalog": mcp.get("catalogFields"),
    "mcp-cli": mcp.get("generatedCliFields"),
    "mcp-console": mcp.get("generatedConsoleFields"),
}
if any(not isinstance(fields, list) or not fields for fields in surface_fields.values()):
    fail("one or more generated surfaces has no field roster")

expected = surface_fields["catalog"]
drift = {name: fields for name, fields in surface_fields.items() if fields != expected}
if drift:
    fail(f"CLI/Console/catalog field drift: {sorted(drift)}")

openapi_ids = {
    row["catalogOperationId"]
    for row in openapi.get("operations", [])
    if row.get("catalogOperationId")
}
mcp_ids = {row["operationId"] for row in mcp.get("operations", [])}
if not mcp_ids or not mcp_ids.issubset(openapi_ids):
    fail("MCP catalog contains missing or non-OpenAPI Admin operation identities")

print(json.dumps({
    "roster": {
        "status": "ready",
        "openApiOperationCount": len(openapi_ids),
        "mcpProjectionCount": len(mcp_ids),
        "fieldCount": len(expected),
        "exports": expected_exports,
    }
}, indent=2, sort_keys=True))
