# FileGDB Import Workflow

This guide covers the MVP workflow for importing Esri File Geodatabases into Honua Server through the admin import API.

## What Is Supported

- Preview a File Geodatabase before import.
- Import a File Geodatabase into a PostgreSQL/PostGIS-backed Honua table.
- Use public object URLs for preview/import when the source archive is hosted in a supported public bucket or blob URL.
- Detect the source SRID from geodatabase metadata when possible.
- Surface warnings for advanced geodatabase constructs that the MVP does not preserve.

Honua uses a pure .NET FileGDB reader in the server runtime. GDAL/OGR is not required at runtime.

## Packaging Requirements

The practical transport shape for Honua admin workflows is a zipped geodatabase directory:

- Preferred upload shape: `dataset.gdb.zip`
- Archive contents: exactly one `.gdb` directory with the original internal FileGDB files preserved
- Do not flatten the archive contents; keep the `.gdb` directory and its files intact

Example:

```text
parcels.gdb.zip
└── parcels.gdb/
    ├── a00000001.gdbtable
    ├── a00000001.gdbtablx
    ├── a00000004.gdbtable
    └── ...
```

The core import service can also read a staged local `.gdb` directory, but that is an internal/local-path capability rather than the normal browser or public-URL admin workflow.

## Admin API Workflow

1. Check supported formats:

```bash
curl http://localhost:8080/api/v1/admin/import/formats
```

2. Preview the archive:

```bash
curl -X POST http://localhost:8080/api/v1/admin/import/preview \
  -F "file=@parcels.gdb.zip"
```

3. Import into a target table:

```bash
curl -X POST http://localhost:8080/api/v1/admin/import/upload \
  -F "file=@parcels.gdb.zip" \
  -F "tableName=parcels_import" \
  -F "targetSrid=4326" \
  -F "overwriteExisting=true"
```

## Public Object URL Workflow

If the archive already lives in public object storage, use the URL-based endpoints instead of re-uploading the file.

Preview:

```bash
curl -X POST http://localhost:8080/api/v1/admin/import/preview-url \
  -H "Content-Type: application/json" \
  -d '{
    "sourceUrl": "https://s3.amazonaws.com/example-bucket/parcels.gdb.zip"
  }'
```

Import:

```bash
curl -X POST http://localhost:8080/api/v1/admin/import/upload-url \
  -H "Content-Type: application/json" \
  -d '{
    "sourceUrl": "https://s3.amazonaws.com/example-bucket/parcels.gdb.zip",
    "tableName": "parcels_import",
    "targetSrid": 4326,
    "overwriteExisting": true
  }'
```

## What Honua Preserves

For supported datasets, the MVP import path preserves the migration-critical pieces needed to get data into Honua quickly:

- Feature geometry
- Feature attribute values
- Detected SRID when the geodatabase metadata exposes it
- Per-feature reprojection into the requested `targetSrid`

The imported output is one Honua target table per request.

## Warnings And Limitations

The MVP explicitly favors the common migration path first and reports unsupported geodatabase constructs as warnings instead of silently pretending they were preserved.

Current limitations:

- Domains are detected but not imported as Honua constraints.
- Relationship classes are detected but not imported.
- Subtypes are detected but not imported.
- Topology rules are detected but not imported.
- Network datasets are detected but not imported.
- Multi-feature-class geodatabases are imported through one target table per request; the admin API does not currently expose per-layer selection for FileGDB uploads.
- The public admin workflow expects a `.gdb.zip` archive, not a raw directory upload.

Preview and import responses include a `warnings` array so migration operators can see when the source geodatabase contains constructs that need manual follow-up.

## Operational Notes

- Preview first for unfamiliar datasets. It confirms format detection and lets you inspect warnings before writing data.
- Set `targetSrid` explicitly when you need a deterministic destination CRS for downstream services.
- For very large archives, prefer the streamed import path and avoid client-side unzip/rezip cycles.

## Related Import Work

The FileGDB workflow ships alongside the companion import-pipeline work tracked in:

- `honua-server#449` for streamed ingest of large datasets and archives
- `honua-server#450` for ingest from supported public object URLs

Those concerns were intentionally split so FileGDB support could stay focused on format handling while the import pipeline grew better transport options.
