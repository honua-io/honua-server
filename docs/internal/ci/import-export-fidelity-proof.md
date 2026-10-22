# Import/export fidelity execution proof

Issue [#4420](https://github.com/honua-io/honua-server/issues/4420) protects the
2026.1 section 14 gate-integrity promise: missing, skipped or stale evidence must
not pass. The required PR Gate runs `scripts/ci/run-import-export-fidelity-proof.py`.
The optional external ArcGIS scorecard workflow validates policy; its committed
scorecard is not current execution evidence.

The runner executes three real `StreamingFileImportService` PostGIS fixtures:
replacement of existing rows, creation of a new target, and local GeoPackage CRS
resolution with transformed coordinate readback. These are file imports, distinct
from the GeoServices service/layer orchestration and published SDK receipts owned
by server#4600, release#316, release#317 and sdk-dotnet#340.

Three writer fixtures call the production CSV, shapefile and GeoPackage writers.
A digest-pinned upstream GDAL container independently opens those files. Python
CSV and SQLite readers also check quoting, missing cells and GeoPackage metadata.
Expected values are explicit constants: XY/Z/M, two disjoint line parts, a 4x4
polygon with a 1x1 hole (area 15), amount 7.25, a zero-valued unlocated feature,
null attributes and EPSG:4326. Ring orientation may change, but closure, vertex
values, hole/part counts and area must agree. CSV has no embedded CRS and represents
null string values as empty cells; the spatial formats must preserve null geometry
and CRS. Raster nodata does not apply to these vector fixtures.

The reviewed baseline declares the complete import/format denominator and numeric
counts, not golden output bytes. Each invocation creates a new UUID directory,
requires exactly three passing export and three passing import results (no skips),
then decodes all nine files before comparison. The successful receipt records
HEAD, run ID, UTC completion, reader version/image and SHA-256 of the actual files
and TRX. CI uploads partial evidence on failure and still fails the required gate.
The gate also deliberately changes a copied GeoPackage's amount from 7.25 to 8
and requires the independent reader to reject that corruption for that exact
value mismatch. An unrelated Docker/reader failure does not satisfy the challenge.

The client-compat comparator also rejects passing-status count changes, lost
measurements and increased absolute error. A delta tolerance must be explicitly
reviewed as `measured_delta_tolerance` in that case's baseline, in its own units;
the default is zero. A null baseline measurement remains explicitly unmeasured.
GDAL's older `ogr2ogr` export categories certify the GDAL writer, so they remain
outside the Honua-writer proof denominator.

Run natively from the repository with .NET 10 and Docker Desktop:

```powershell
python scripts/ci/run-import-export-fidelity-proof.py
```

`--no-build` is available only when both current-checkout test projects have
already been built in Release. The script uses a four-node MSBuild cap and bounded
foreground subprocesses. Initial Windows validation: writer fixtures 3 passed,
0 failed/skipped; PostGIS import fixtures 3 passed, 0 failed/skipped;
receipt-reader checks 2 passed; client comparator checks 5 passed.
The complete import/independent-reader result is recorded with the delivering PR.
