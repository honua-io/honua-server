#!/usr/bin/env python3
"""List the local kind GP lifecycle cases without starting a cluster.

Default mode checks that docker, kind, kubectl, and helm are on PATH, prints
the six required cases, and exits. It does not call kind, pull images, or
create a cluster. ``--execute`` is accepted and refused: live execution is not
implemented in this slice. This script is not a CI gate.
"""

from __future__ import annotations

import argparse
import shutil
import sys

CASES = (
    "success",
    "failure",
    "cancellation",
    "timeout",
    "worker loss",
    "artifact-upload failure",
)

TOOLS = ("docker", "kind", "kubectl", "helm")

REMEDIATIONS = {
    "docker": "Install Docker Engine and the Compose v2 plugin, then rerun python3 scripts/ci/gp-kind-lifecycle-matrix.py.",
    "kind": "Install kind from https://kind.sigs.k8s.io/docs/user/quick-start/, then rerun python3 scripts/ci/gp-kind-lifecycle-matrix.py.",
    "kubectl": "Install kubectl matching the kind cluster, then rerun python3 scripts/ci/gp-kind-lifecycle-matrix.py.",
    "helm": "Install Helm 3 from https://helm.sh/docs/intro/install/, then rerun python3 scripts/ci/gp-kind-lifecycle-matrix.py.",
}

EXECUTE_REFUSAL = "live execution is not implemented in this slice"


def missing_tools(which):
    """Tool names whose lookup returned no path."""
    missing = []
    for name in TOOLS:
        found = which(name)
        if found is None or str(found).strip() == "":
            missing.append(name)
    return missing


def main(argv=None, *, which=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--execute",
        action="store_true",
        help="refused; live execution is not implemented in this slice",
    )
    args = parser.parse_args(argv)
    lookup = shutil.which if which is None else which
    missing = missing_tools(lookup)
    if missing:
        print("GP kind lifecycle matrix prerequisites are missing:", file=sys.stderr)
        for name in missing:
            print(f"- {name}: {REMEDIATIONS[name]}", file=sys.stderr)
        return 1
    if args.execute:
        # No cluster, image pull, or kind invocation exists on this path.
        print(EXECUTE_REFUSAL, file=sys.stderr)
        return 1
    print("GP kind lifecycle cases (not executed):")
    for case in CASES:
        print(f"- {case}")
    print("Live kind execution is not part of this check.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
