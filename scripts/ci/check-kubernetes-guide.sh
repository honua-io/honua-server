#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
guide="${repo_root}/docs/guides/deploy/kubernetes.md"

python3 - "${guide}" <<'PY'
from __future__ import annotations

import re
import sys
from pathlib import Path

guide = Path(sys.argv[1])
text = guide.read_text(encoding="utf-8")


def fail(message: str) -> None:
    raise SystemExit(f"Kubernetes guide contract failed: {message}")


def extract(marker: str) -> str:
    pattern = re.compile(
        rf"<!-- BEGIN {re.escape(marker)} -->\s*```yaml\n(.*?)\n```\s*"
        rf"<!-- END {re.escape(marker)} -->",
        re.DOTALL,
    )
    matches = pattern.findall(text)
    if len(matches) != 1:
        fail(f"expected one {marker} values block, found {len(matches)}")
    return matches[0]


def scalar_paths(yaml_text: str) -> dict[str, str]:
    """Read scalar mapping paths from the deliberately simple guide YAML.

    This is not a general YAML parser. It keeps the check dependency-free while
    validating the chart keys that make the published topology safe.
    """

    result: dict[str, str] = {}
    stack: list[tuple[int, str]] = []
    for line_number, raw in enumerate(yaml_text.splitlines(), 1):
        if not raw.strip() or raw.lstrip().startswith("#"):
            continue
        indent = len(raw) - len(raw.lstrip(" "))
        if "\t" in raw[:indent] or indent % 2:
            fail(f"unsupported indentation on values line {line_number}")
        stripped = raw.strip()
        if stripped.startswith("- "):
            stripped = stripped[2:]
            indent += 2
        match = re.match(r"^([A-Za-z0-9_]+):(?:\s*(.*))?$", stripped)
        if not match:
            continue
        key, value = match.groups()
        while stack and stack[-1][0] >= indent:
            stack.pop()
        path = ".".join([entry[1] for entry in stack] + [key])
        value = (value or "").split(" #", 1)[0].strip()
        if value:
            result[path] = value.strip('"\'')
        else:
            stack.append((indent, key))
    return result


def require(values: dict[str, str], path: str, expected: str, topology: str) -> None:
    actual = values.get(path)
    if actual != expected:
        fail(f"{topology} {path} must be {expected!r}, found {actual!r}")


single_yaml = extract("KUBERNETES_SINGLE_NODE_VALUES")
multi_yaml = extract("KUBERNETES_MULTI_NODE_VALUES")
single = scalar_paths(single_yaml)
multi = scalar_paths(multi_yaml)

for topology, values in (("single-node", single), ("multi-node", multi)):
    stale = sorted(path for path in values if path in {"env", "envFrom"})
    if stale:
        fail(f"{topology} uses obsolete top-level chart keys: {', '.join(stale)}")
    require(values, "image.tag", "", topology)
    require(values, "image.digest", "", topology)
    require(values, "image.pullPolicy", "IfNotPresent", topology)
    require(values, "secret.create", "false", topology)
    require(values, "secret.name", "honua-runtime", topology)
    require(values, "config.env.HONUA_OBSERVABILITY", "true", topology)
    require(values, "config.env.HONUA_OPENTELEMETRY", "true", topology)
    require(values, "config.env.Limits__Connections__MinConnectionPoolSize", "5", topology)
    require(values, "config.env.Limits__Connections__MaxConnectionPoolSize", "40", topology)
    require(values, "config.env.Limits__Connections__MaxConcurrentQueries", "40", topology)
    if not values.get("observability.otlpEndpoint"):
        fail(f"{topology} enables telemetry without an OTLP receiver")

require(single, "replicaCount", "1", "single-node")
require(single, "config.env.Deployment__Mode", "SingleInstance", "single-node")
require(single, "autoscaling.enabled", "false", "single-node")
require(single, "strategy.type", "Recreate", "single-node")
require(single, "config.env.FileStorage__Provider", "Local", "single-node")
require(
    single,
    "config.env.FileStorage__LocalStorage__BasePath",
    "/var/lib/honua/storage",
    "single-node",
)
if "claimName: honua-storage" not in single_yaml or "mountPath: /var/lib/honua/storage" not in single_yaml:
    fail("single-node Local storage must be backed by the documented PVC mount")

replicas = int(multi.get("replicaCount", "0"))
if replicas <= 1:
    fail("multi-node replicaCount must be greater than one")
require(multi, "config.env.Deployment__Mode", "MultiNode", "multi-node")
require(multi, "autoscaling.enabled", "true", "multi-node")
require(multi, "strategy.type", "Recreate", "multi-node")
require(multi, "config.env.Cache__EnableFallback", "false", "multi-node")
provider = multi.get("config.env.FileStorage__Provider")
if provider not in {"AwsS3", "AzureBlob"}:
    fail("multi-node FileStorage__Provider must be AwsS3 or AzureBlob")
if provider == "AwsS3":
    for path in (
        "config.env.FileStorage__AwsS3__BucketName",
        "config.env.FileStorage__AwsS3__Region",
    ):
        if not multi.get(path):
            fail(f"multi-node {path} is required")

required_guide_fragments = (
    "ConnectionStrings__redis=",
    "--set-string image.digest=\"$HONUA_IMAGE_DIGEST\"",
    "Local` storage is invalid in",
    "Use `RollingUpdate` only when",
    "preStop:",
    "Helm cannot downgrade the database schema",
    "68fcb72f03aab74adc812df48f8f63677c829877",
)
for fragment in required_guide_fragments:
    if fragment not in text:
        fail(f"missing required operator guidance: {fragment}")

if re.search(r"(?m)^\s*(?:tag:\s*[\"']?(?:latest|latest-aot)|image:\s*[^\n]*(?:latest|latest-aot))", single_yaml + "\n" + multi_yaml):
    fail("production values must not use a moving image tag")

print("Kubernetes guide static contract passed")
PY

if [[ -n "${HONUA_HELM_CHART:-}" ]]; then
    if ! command -v helm >/dev/null 2>&1; then
        echo "HONUA_HELM_CHART was set but helm is unavailable" >&2
        exit 1
    fi

    dummy_digest="sha256:$(printf '0%.0s' {1..64})"
    temp_dir="$(mktemp -d)"
    trap 'rm -rf "${temp_dir}"' EXIT

    python3 - "${guide}" "${temp_dir}" <<'PY'
import re
import sys
from pathlib import Path

text = Path(sys.argv[1]).read_text(encoding="utf-8")
output = Path(sys.argv[2])
for marker, name in (
    ("KUBERNETES_SINGLE_NODE_VALUES", "single-node.yaml"),
    ("KUBERNETES_MULTI_NODE_VALUES", "multi-node.yaml"),
):
    match = re.search(
        rf"<!-- BEGIN {marker} -->\s*```yaml\n(.*?)\n```\s*<!-- END {marker} -->",
        text,
        re.DOTALL,
    )
    if match is None:
        raise SystemExit(f"missing {marker}")
    (output / name).write_text(match.group(1) + "\n", encoding="utf-8")
PY

    for values in "${temp_dir}/single-node.yaml" "${temp_dir}/multi-node.yaml"; do
        helm template honua "${HONUA_HELM_CHART}" \
            --namespace honua \
            --values "${values}" \
            --set-string image.digest="${dummy_digest}" >/dev/null
    done
    echo "Kubernetes guide values rendered against ${HONUA_HELM_CHART}"
fi
