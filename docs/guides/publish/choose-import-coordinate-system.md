---
type: guide
title: "How to keep or reproject an imported file's coordinate system"
description: "Separate the file's source CRS from the storage target and verify the stored SRID."
---
# How to keep or reproject an imported file's coordinate system

A coordinate reference system (CRS) gives coordinates their location and units.
An SRID identifies that system, commonly by its EPSG number. Keeping a projected
source such as EPSG:2193 preserves its coordinates in metres. Reprojecting to
EPSG:4326 stores longitude and latitude instead.

## Check availability first

**The keep-source default is an rc.2 change awaiting a matching server and
Console candidate.** Older builds default an omitted `targetSrid` to 4326.
Do not assume that leaving a target blank preserves a projected source on an
older installation. Use an explicit supported target matching the source, or
wait for the updated server and client.

On older installations, MCP `ingest_dataset` always stores EPSG:4326 and accepts
no target. Use the Admin import through a compatible Honua SDK with an explicit
`targetSrid` matching the source to retain a projected system until the updated
server is installed.

The updated direct file-preview contract includes `coordinateSystem` and a
successful import reports `sourceSrid`, `storedSrid` and `reprojected`. If those
fields or the corresponding Console controls are absent, this guide does not
establish keep-source support in that installation.

You need import permission, a supported file, and a new target table name.
Start from Console's **Import a file** entry, or use your installed Honua SDK
or the MCP `ingest_dataset` tool. The [file import guide](import-files.md)
describes supported formats and background jobs.

## Choose the source and target

1. Preview the file and review the detected source system. Check its EPSG
   code, name, units and area against the dataset's documentation. Detection
   describes the file; it is not a suggestion to change the coordinates.
2. If the source is unknown or wrong, supply an explicit `sourceSrid` override
   from the dataset's authoritative metadata. Use the server's supported
   coordinate-system lookup where the client exposes it. GeoJSON, KML and GPX
   can use format-defined WGS 84; an undeclared CSV or plain WKT must not be
   guessed to be WGS 84.
3. Choose the storage behavior:

   | Intent | Updated import request | Expected result for an EPSG:2193 source |
   |---|---|---|
   | Keep the source coordinates | Omit `targetSrid` or set it to null | `storedSrid: 2193`, `reprojected: false` |
   | Store WGS 84 coordinates | Set `targetSrid: 4326` | `storedSrid: 4326`, `reprojected: true` |
   | State the existing system explicitly | Set `targetSrid: 2193` | `storedSrid: 2193`, `reprojected: false` |

4. Import into the selected table. For a background job, keep the returned job
   identity and wait for its terminal result before publishing.

`sourceSrid` declares how to interpret the existing numbers; `targetSrid`
requests a coordinate conversion. A target alone cannot resolve an unknown
source. An override changes the effective source without rewriting what the
preview originally detected.

## Verify before publishing

Compare the final `sourceSrid`, `storedSrid` and `reprojected` with your choice.
`detectedSrid` remains the detected system and can differ from an explicit
source override. Review import warnings and check a known feature in the
dataset's expected location. Then [publish the table](publish-layers.md).

## Troubleshooting

- **`import.source_srid_required`:** identify the source system and supply
  `sourceSrid`; selecting a target does not supply the missing source.
- **`import.target_srid_unsupported`:** choose a target supported by this server.
  Entering an arbitrary EPSG number does not install its transformation support.
- **The preview has mixed or unresolvable source systems:** resolve the source
  metadata or prepare a consistent file before importing; do not borrow the
  first layer's system for every layer.
- **The stored system is unexpected:** stop before publication, retain the
  import result, and check both the installed version and explicit target.
  Changing a map's display CRS does not repair incorrectly interpreted data.

See [CRS and units](../../concepts/domain/crs.md) for the underlying concepts.
