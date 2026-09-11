#!/usr/bin/env bash
# Keep the existing generators and their output contracts unchanged.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
bash scripts/generate-feature-catalog.sh "$@"
# Architecture.Tests references Server.Tests; the first invocation builds both.
bash scripts/generate-admin-operation-parity-exports.sh "$@" --no-build --no-restore
python3 scripts/ci/verify-admin-operation-parity.py
bash scripts/generate-geoservices-parity.sh "$@" --no-build --no-restore
python3 scripts/ci/generate-capability-matrix.py
python3 scripts/examples/generate-manifest.py
