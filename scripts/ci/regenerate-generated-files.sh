#!/usr/bin/env bash
# Keep the existing generators and their output contracts unchanged.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
bash scripts/ci/generate-feature-catalog.sh "$@"
# Architecture.Tests references Server.Tests; the first invocation builds both.
bash scripts/ci/generate-admin-operation-parity-exports.sh "$@" --no-build --no-restore
python3 scripts/ci/verify-admin-operation-parity.py
# Reads the admin projection manifest above; builds Honua.Ai.Tests.
bash scripts/ci/generate-mcp-tool-roster.sh "$@"
bash scripts/ci/generate-geoservices-parity.sh "$@" --no-build --no-restore
python3 scripts/ci/generate-capability-matrix.py
python3 scripts/ci/generate-capability-concepts.py
# Capability pages above are graph inputs. Publish their final document digests,
# never the graph from before those pages were regenerated.
python3 scripts/ci/generate-okf-graph.py
