# Import executor epoch source audit — 2026-10-28

The catalog digest changed because `ImportDatasetJobExecutor` now passes the
canonical import result's `StorageCrsCoordinateEpoch` into the publication
request. The executor's process selection, import stages, result handling,
refresh and provenance paths are unchanged. The `import.dataset` matrix row
still cites `ExecuteAsync_StagedGeoJson_ImportsFlattensRefreshesAndRecordsProvenance`:
that test executes a real staged GeoJSON import, publication and readback, so
its existing semantic execution verdict remains **proven** for that path.
It does not claim to prove dynamic-CRS epoch retention; the GeoParquet
import/publish/export regressions cover that separate behavior.

No other catalog source root changed in this candidate. The remaining 100
operation rows and their evidence paths retain their prior verdicts. This
review neither promotes a row nor changes the 101-operation denominator.
The matrix's historical `catalogSourceRevision` and `recordedAt` remain
historical; `catalogSourceContentDigest` is updated to the exact reviewed
source bytes, and the new-head architecture gate must verify it.
