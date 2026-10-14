---
type: concept
title: "Coordinate reference system and units"
description: "How coordinates locate geometry and how operation distances are interpreted."
tags: [gis, domain-language]
aliases: ["CRS","projection","SRID","EPSG","coordinates","meters","latitude longitude"]
sources:
  - id: contract
    resource: "/guides/query-analyze/run-geoprocessing.md"
relations:
  - predicate: relates_to
    target: "/guides/workflows/import-data.md"
  - predicate: relates_to
    target: "/guides/workflows/analyze-features.md"
---

# Coordinate reference system and units
A CRS defines how coordinates locate geometry; an SRID identifies a spatial reference in a particular system. EPSG:4326 coordinates are angular, while EPSG:3857 uses projected coordinates. A numeric distance must be interpreted using the chosen operation's contract.

The documented single-geometry buffer expresses distance in input CRS units; supplying a meter-like number with geographic coordinates is not a metric buffer.[^contract] A projected CRS must also be appropriate for the region and accuracy needed. Reprojection changes coordinates; merely changing a CRS label does not.

“Points from this CSV” needs the coordinate columns, order, and source reference. A map that looks plausible can still have swapped axes or incorrect units.

Use [import data](../../guides/workflows/import-data.md) and [analyze features](../../guides/workflows/analyze-features.md). Check bounds, representative locations, and output CRS.

[^contract]: [Source contract](../../guides/query-analyze/run-geoprocessing.md).
