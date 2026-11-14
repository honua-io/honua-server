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
parents = {
    "GeoServices GPServer and NAServer",
    "MCP and Sessions",
    "Server Features Admin Authorization",
    "Operator Eval Harness",
}
cases = {}
for contract in contracts:
    parent = contract["originalShard"]["name"]
    if parent in parents:
        for child in contract["children"]:
            cases[child] = ["ryuk", "redis"] + (["postgis17"] if parent == "Operator Eval Harness" else [])
assert len(cases) == 9, cases
cases.update({"Scene": [], "Protocol Units": [], "Core and Cloud Contracts": ["ryuk", "postgis16", "postgis18"]})
for name, expected in cases.items():
    result = subprocess.run(["bash", "-c", script], env={**os.environ, "SERVER_SHARD": name},
                            check=True, capture_output=True, text=True)
    assert result.stdout.splitlines() == expected, (name, expected, result.stdout)
print(f"shard-image-selection=ok cases={len(cases)}")
