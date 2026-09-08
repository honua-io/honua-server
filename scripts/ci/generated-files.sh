#!/usr/bin/env bash
# Sourced allowlist of repository-state projections; never include authored inputs.
GENERATED_FILES=(
  docs/gis/data/feature-catalog.json
  docs/gis/data/admin-openapi-operation-ids.json
  docs/gis/data/admin-mcp-projection-manifest.json
  docs/gis/data/geoservices-rest-parity.json
  docs/gis/data/capability-matrix.v1.json
  examples/manifest.json
)
