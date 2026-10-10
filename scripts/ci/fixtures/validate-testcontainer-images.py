#!/usr/bin/env python3
"""Offline checks for required image preparation, registry identity and failures."""
import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap

ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "scripts/ci/prepare-testcontainer-images.sh"
PIN16 = "mirror.gcr.io/postgis/postgis:16-3.4@sha256:44126d872ac91993766c341e369c539e8196614321765d36a6f1bab0419a5fa5"
RYUK = "mirror.gcr.io/testcontainers/ryuk:0.14.0@sha256:7c1a8a9a47c780ed0f983770a662f80deb115d95cce3e2daa3d12115b8cd28f0"
workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
assert workflow.count("image: " + PIN16) == 8
assert "image: ${{ matrix.pg_service_image }}" in workflow
assert "name: Postgres Compatibility (${{ matrix.pg_image }})" in workflow
assert "PG_IMAGE: ${{ matrix.pg_image }}" in workflow
for version in ("16-3.4", "17-3.5", "18-3.6"):
    pattern = r'"pg_image":"postgis/postgis:' + re.escape(version) + r'","pg_service_image":"mirror.gcr.io/postgis/postgis:' + re.escape(version) + r'@sha256:[0-9a-f]{64}"'
    assert len(re.findall(pattern, workflow)) == (2 if version == "16-3.4" else 1)
for name in ("ci.yml", "pr-gate.yml"):
    content = (ROOT / ".github/workflows" / name).read_text(encoding="utf-8")
    assert "TESTCONTAINERS_RYUK_CONTAINER_IMAGE: '" + RYUK + "'" in content
    assert not re.search(r"TESTCONTAINERS_RYUK_DISABLED:\s*['\"]?true", content)
assert RYUK in SCRIPT.read_text(encoding="utf-8")
python_job = re.split(r"(?m)^  [a-z][a-z0-9-]*:\n", workflow.split("  python-integration-tests:\n", 1)[1], maxsplit=1)[0]
assert "RYUK_CONTAINER_IMAGE: '" + RYUK + "'" in python_job
assert "run: scripts/ci/prepare-testcontainer-images.sh ryuk postgis18" in python_job
assert "if [[ \"${SERVER_SHARD}\" == 'Core and Cloud Contracts' ]]; then\n            images+=(postgis16 postgis18)" in workflow
datum = (ROOT / "tests/dotnet/Honua.TestKit/DatumGridPostgresFixture.cs").read_text(encoding="utf-8")
assert 'const string image = "mirror.gcr.io/postgis/postgis:18-3.6@sha256:60f6ad1d21ea86a67d47780b9a0d1e1d200500f62b19293fa834d0dea80b8677";' in datum
for block in re.split(r"(?m)^      - ", workflow):
    if not block.startswith("name: Prepare") or "        run: |\n" not in block:
        continue
    shell = block.split("        run: |\n", 1)[1]
    # Only the literal run body belongs to the shell, not the following YAML.
    body = []
    for line in shell.splitlines():
        if line.strip() and not line.startswith("          "):
            break
        body.append(line)
    subprocess.run(["bash", "-n"], input=textwrap.dedent("\n".join(body)), text=True, check=True)
print("PASS: primary services, compatibility labels and pinned enabled cleanup")

MOCK_DOCKER = '''#!/usr/bin/env bash
set -euo pipefail
printf '%s\\n' "$*" >> "$MOCK_LOG"
case "$1" in
  pull)
    count=$(cat "$MOCK_COUNT" 2>/dev/null || echo 0)
    count=$((count + 1)); echo "$count" > "$MOCK_COUNT"
    if [[ "$MOCK_MODE" == exhausted ]]; then exit 1; fi
    if [[ "$MOCK_MODE" == retry && "$count" == 1 ]]; then exit 1; fi
    if [[ "$MOCK_MODE" == fallback && "$*" != *docker.io/* ]]; then exit 1; fi
    printf '%s\\n' "${@: -1}" >> "$MOCK_IMAGES"
    ;;
  tag)
    [[ "$MOCK_MODE" != tag_failure ]]
    grep -Fxq -- "$2" "$MOCK_IMAGES"
    printf '%s\\n' "$3" >> "$MOCK_IMAGES"
    ;;
  image)
    if [[ "$MOCK_MODE" == inspect_failure && "$3" != *@sha256:* ]]; then exit 1; fi
    grep -Fxq -- "$3" "$MOCK_IMAGES"
    ;;
esac
'''

cases = [
    ("success", ["postgis18", "postgis17", "postgis16", "redis", "mysql", "postgres16", "ryuk"], 0, 7),
    ("retry", ["redis"], 0, 2),
    ("fallback", ["postgis17", "redis"], 0, 6),
    ("exhausted", ["redis"], 1, 4),
    ("exhausted", ["ryuk"], 1, 2),
    ("tag_failure", ["redis"], 1, 1),
    ("inspect_failure", ["redis"], 1, 1),
    ("success", ["redis", "unknown"], 2, 0),
    ("success", [], 2, 0),
]
with tempfile.TemporaryDirectory(prefix="ci-image-fixtures-") as directory:
    temp = Path(directory)
    for command, source in {
        "docker": MOCK_DOCKER,
        "timeout": '#!/usr/bin/env bash\nprintf "timeout %s\\n" "$*" >> "$MOCK_LOG"\nshift\nexec "$@"\n',
        "sleep": '#!/usr/bin/env bash\nprintf "sleep %s\\n" "$*" >> "$MOCK_LOG"\n',
    }.items():
        path = temp / command
        path.write_text(source, encoding="utf-8")
        path.chmod(0o755)
    for index, (mode, args, expected, pulls) in enumerate(cases):
        log = temp / f"{index}.log"
        env = dict(os.environ, PATH=str(temp) + os.pathsep + os.environ["PATH"],
                   MOCK_MODE=mode, MOCK_LOG=str(log), MOCK_COUNT=str(temp / f"{index}.count"),
                   MOCK_IMAGES=str(temp / f"{index}.images"))
        result = subprocess.run(["bash", str(SCRIPT), *args], env=env, capture_output=True, text=True, timeout=15)
        calls = log.read_text().splitlines() if log.exists() else []
        assert result.returncode == expected, (mode, args, result.returncode, result.stderr)
        requests = [line for line in calls if line.startswith("pull ")]
        assert len(requests) == pulls, (mode, calls)
        assert all("--platform linux/amd64" in line and "@sha256:" in line for line in requests)
        assert all(line.startswith("timeout 60s docker pull ") for line in calls if line.startswith("timeout "))
        if index == 0:
            assert [line.split()[-1] for line in calls if line.startswith("tag ")] == [
                "postgis/postgis:18-3.6", "postgis/postgis:17-3.5", "postgis/postgis:16-3.4", "redis:7.2-alpine",
                "mysql:8.0.36", "postgres:16-alpine"]
        print(f"PASS: {mode} {args} (exit {expected}, {pulls} pulls)")
print("Required-image fixtures: 10 passed, 0 failed")
