#!/usr/bin/env bash
set -euo pipefail

# Regenerate the published GeoServices REST parity matrix
# (docs/gis/data/geoservices-rest-parity.json, tracking issue #2863, ADR-0058).
#
# The matrix is a GENERATED join of two honestly-different halves:
#
#   1. WHICH ROUTES EXIST — mechanical, derived from EndpointRegistry.All via the
#      generated feature catalog and normalized to Esri-relative operation paths by
#      GeoServicesRouteRoster. Never hand-edited. Nothing can be published here that
#      is not served.
#   2. HOW COMPLETELY EACH IS IMPLEMENTED — human judgement, hand-authored in
#      docs/gis/data/geoservices-parity-judgment.json. Never inferred.
#
# Edit the JUDGEMENT SOURCE, then run this script. Do not hand-edit
# docs/gis/data/geoservices-rest-parity.json — GeoServicesParityMatrixDriftTests
# fails the build if the committed artifact does not equal freshly-generated output,
# if a served operation is unclassified, or if a classification names a route that is
# not served.
#
# NEVER upgrade a status to make the gate pass. If the gate creates that pressure,
# the gate is wrong — the whole point of this artifact is that we once published a
# computeClass route that never existed.
#
# This script drives the GeoServicesParityEmitter [Fact], which is gated behind
# HONUA_EMIT_GEOSERVICES_PARITY=1 so ordinary `dotnet test` runs stay read-only.
#
# Run from anywhere in the repo. Commit the regenerated file.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

echo "Regenerating ${PWD}/docs/gis/data/geoservices-rest-parity.json from the derived route roster + judgement source..."

HONUA_EMIT_GEOSERVICES_PARITY=1 dotnet test \
  tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj \
  --filter "FullyQualifiedName~GeoServicesParityEmitter" \
  "$@"

echo "Done. Review and commit docs/gis/data/geoservices-rest-parity.json."
