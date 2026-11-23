---
type: capability
title: "GeoParquet Format"
description: "Import, export, and query features as GeoParquet 1.1.0 with WKB geometry and spatial covering metadata through the shared provider-neutral writer."
resource: "honua://capability/format.geoparquet"
tags: [capability, format, community]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# GeoParquet Format

Import, export, and query features as GeoParquet 1.1.0 with WKB geometry and spatial covering metadata through the shared provider-neutral writer.

| | |
| --- | --- |
| Capability key | `format.geoparquet` |
| Category | Format |
| Edition | Community |
| Status | live |

The facts above come from the server's capability registry and capability matrix, which are generated from the server's own route catalog and test evidence rather than from prose.

## How to use it

GeoParquet is selected with f=parquet on FeatureServer, $format=parquet on OData, format=geoparquet on bulk export, and file upload import; it has no separate route.

- [All capabilities](README.md)
