#!/usr/bin/env python3
"""Execute the workflow image selector for renamed capacity children."""
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[2]
workflow = (ROOT / ".github/workflows/ci.yml").read_text()
step = workflow.split("      - name: Prepare shard Testcontainers images\n", 1)[1]
body = step.split("        run: |\n", 1)[1]
lines = []
for line in body.splitlines():
    if line.strip() and not line.startswith("          "):
        break
    lines.append(line[10:])
selector = "\n".join(lines)
assert "scripts/ci/prepare-testcontainer-images.sh" in selector
selector = selector.replace("scripts/ci/prepare-testcontainer-images.sh", "prepare_images")
script = 'set -euo pipefail\nprepare_images() { printf "%s\\n" "$@"; }\n' + selector
contracts = json.loads((ROOT / "scripts/ci/fixtures/full-run-capacity-contracts.json").read_text())
# Images the unsplit parent selected before its capacity split. Every child must keep them, and
# every split parent must be listed, so a renamed child cannot silently lose its preparation.
PARENT_IMAGES = {
    "Core and Cloud Contracts": ["ryuk", "postgis16", "postgis18"],
    "STAC and API Governance": [],
    "Server Features Console Sessions and Access": [],
    "Server Features Console and Alerts": [],
    "OGC API Maps Rendering and Records": [],
    "OGC API Tiles Coverages and Processes": ["ryuk", "redis"],
    "OGC API Tiles Endpoints and CRS": [],
    "GeoServices Geometry VectorTile and Versioning": [],
    "GeoServices Versioning": [],
    "GeoServices ImageServer": [],
    "GeoServices Catalog and ImageServer Support": [],
    "GeoServices ImageServer Rendering and Support": [],
    "GeoServices ImageServer Raster Rendering": [],
    "Raster Serving Scene Geometry and Terrain": [],
    "Elevation and Terrain Analysis": [],

    "GeoServices GPServer and NAServer": ["ryuk", "redis"],
    "MCP and Sessions": ["ryuk", "redis"],
    "Migration": ["ryuk", "redis"],
    "OData Advanced and Filters": [],
    "OGC API Features": [],
    "Operator Eval Harness": ["ryuk", "redis", "postgis17"],
    "STAC Protocol": [],
    "Scene I3S Resources": [],
    "Server Features Admin Authorization": ["ryuk", "redis"],
    "Server Features Admin Layer Management": [],
    "Server Features Admin Release Control": [],
    "Server Features Collaboration Mobile and Identity": [],
    "Server Features Packages and Temporal": [],
    "OGC Classic WMTS": [],
}
assert {contract["originalShard"]["name"] for contract in contracts} == set(PARENT_IMAGES), "Unlisted split parent."
cases = {child: PARENT_IMAGES[contract["originalShard"]["name"]]
         for contract in contracts for child in contract["children"]}
cases.update({"Scene": [], "Protocol Units": []})
for name, expected in cases.items():
    result = subprocess.run(["bash", "-c", script], env={**os.environ, "SERVER_SHARD": name},
                            check=True, capture_output=True, text=True)
    assert result.stdout.splitlines() == expected, (name, expected, result.stdout)
print(f"shard-image-selection=ok cases={len(cases)}")
