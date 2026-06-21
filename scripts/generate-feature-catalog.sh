#!/usr/bin/env bash
set -euo pipefail

# Regenerate the evidence-based feature catalog
# (docs/gis/data/feature-catalog.json, tracking issue #1946, ADR-0054).
#
# The catalog is a GENERATED projection of the shipped HTTP API surface:
# EndpointRegistry.All joined with the [Endpoint]-attributed proving tests and
# the public-interface proof ledger. It is never hand-edited — the
# FeatureCatalogDriftTests guard fails the build if the committed artifact does
# not equal freshly-generated output.
#
# This script drives the FeatureCatalogEmitter [Fact], which is gated behind
# HONUA_EMIT_FEATURE_CATALOG=1 so ordinary `dotnet test` runs stay read-only.
#
# Run from anywhere in the repo. Commit the regenerated file.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

echo "Regenerating ${PWD}/docs/gis/data/feature-catalog.json from the live API surface..."

HONUA_EMIT_FEATURE_CATALOG=1 dotnet test \
  tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj \
  --filter "FullyQualifiedName~FeatureCatalogEmitter" \
  "$@"

echo "Done. Review and commit docs/gis/data/feature-catalog.json."
