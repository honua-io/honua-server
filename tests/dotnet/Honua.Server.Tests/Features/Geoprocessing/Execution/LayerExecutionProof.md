# Layer catalog execution proofs

These tests protect the 2026.1 whole-catalog GP GA promise recorded in the release decision and quality contract. They run in the required PR Gate under `Category=LayerExecutionProof`; the PostGIS integration cases create isolated schemas in a real Docker PostGIS instance. These source/sink catalog rows are proven through their workflow-only executor entry point, not the job submission API.

- `LayerSourceExecutionProofTests.FeatureProject_RealCatalogLayer_PublishesAnalyticalMercatorCoordinatesAndSrid`: insert a literal EPSG:4326 point (12,34) with named attributes through the production feature writer. Decode the actual executor artifact and compare EPSG:3857 ordinates to `x = 6378137 * longitude * pi/180` and `y = 6378137 * log(tan(pi/4 + latitude*pi/360))`, within one millimetre. Read the original point back unchanged.
- `LayerSourceExecutionProofTests.HonuaLayerSource_RealCatalogFilterBboxAndFields_PublishesExactProjectedSelection`: the same point must survive both the category predicate and the projected bounding box; a different-category point inside the box and a same-category point outside it must not survive. Assert exact field selection, feature count, coordinates and output SRID. The initial execution exposed the missing storage CRS used to transform the filter.
- `EsriSourceExecutionProofTests.EsriSource_PagedFilteredFixture_PublishesEverySelectedFeatureExactlyOnce`: a deterministic two-page FeatureServer fixture returns IDs 11,13,15 with coordinate and attribute formulas compared with separately written literal expected rows, including a null name. The real HTTP client, source connector and executor must request offsets 0,2 exactly once and retain the where/watermark/outFields/outSR parameters and exact selected values. JSON numeric equality does not depend on integer versus decimal lexical encoding.
- `LayerSinkExecutionProofTests.HonuaLayerSink_AppendThenKeyedUpsert_ReadsExactCommittedGeometryAttributesAndReceipts`: start with A at (-5,6), append B at (10,20) and one rejected null geometry, then keyed-upsert B at (30,40) and C at (50,60). Resolve the published target through the production Metadata v2 provider router and assert exact persisted rows, attributes, batch provenance and receipts.
- `LayerSinkExecutionProofTests.HonuaLayerSink_FailingRow_RollsBackKeyDeletionAndAllInsertedRows`: a database CHECK rejects a negative value after an upsert would delete A. Assert failed execution, no artifact, and the original A value/geometry as the only visible row.

The CRS regressions additionally distinguish advertised EPSG:3857 from physical EPSG:4326. They exercise both resource StorageCrs fallback and binding storageSrid precedence against the same literal points and analytical oracle. A retained executor regression verifies that source.postgis bbox SRID is not emitted as geometry SRID, since that connector has not projected its stored geometry.

## Required-CI receipts

`dotnet test` exits 0 when a `--filter` matches nothing and when every matched case skips, so running
`Category=LayerExecutionProof` on the required gate did not by itself prove these cases executed: a renamed
trait, a deleted class or a cleanly-skipping Docker PostGIS fixture would have left the gate green with no
layer receipts at all while the operation matrix still called the rows proven. The PR Gate step *Prove the
layer execution cases ran, not skipped* now reads `layer-execution-proof.trx` back and fails on any skipped
case, on zero passes, and on a missing receipt for any test the matrix still cites as proven evidence for
`source.honua-layer`, `source.esri-featureserver` or `sink.honua-layer`.

Counting outcomes alone would not have been enough. The Esri cases are in-process HTTP fixtures that need no
database, so they would have held the passed count above zero even if both PostGIS-backed classes had
disappeared — and those two classes are the entire real-persistence argument for `source.honua-layer` and
`sink.honua-layer`. The step therefore reads the required test names out of
`certification/gp-operation-matrix.v1.json` rather than repeating them, so the manifest claim and the gate
cannot drift apart. `GeoprocessingOperationEvidenceMatrixTests` proves those names still exist in those
files; this step proves they ran.

## Candidate evidence sequencing

The immutable 2026.1 candidate required by #3848 and the linked post-cut certification runs does not exist for these pre-cut implementation PRs. Local and required CI receipts prove the repository executors against real fixtures. Exact-candidate reruns remain released from these PRs until that candidate exists; this does not claim those later certification criteria have passed.

## Wrong-but-well-formed output challenges

The semantic oracles first accept the real execution result, then reject the following independently introduced defects. A success status and a valid artifact are required before checking the negative oracle. No production-output snapshot supplies the expected values.

- Honua source: execute the real PostGIS source again with the where predicate, bbox restriction, field restriction or requested projection lost. The same selection/metadata oracle must reject every artifact. For the wrong projection the bbox is expressed in degrees so the correct row still reaches the SRID assertion.
- Esri source: the fixture returns a duplicated final page row, swapped X/Y ordinates or an incremented numeric value. The actual connector/executor converts and publishes each response; `EsriSource_WellFormedWrongFixtureOutput_FailsSemanticOracle` requires rejection by the same literal row oracle.
- Honua sink: append a duplicate keyed row through the real persistence executor instead of upserting, then reject the canonical read-back with the same oracle. Separately challenge that oracle with an unchanged pre-upsert value and swapped point axes. Exact property sets exclude unexpected fields and rejected rows. The existing database CHECK failure continues to prove transaction rollback.

These are vector fixtures: raster nodata and masks do not apply. Missing data is represented by the Esri null attribute and the sink's explicitly rejected null geometry.

Shared qualification is consumed via the matrix's `audit.candidateBinding` / `sharedRuntimeGaps`: #3848 owns exact server/worker identity and qualification receipts; #3852 owns staged-output durability (these fixtures publish inline artifacts); #3855 owns Postgres restart/retry barriers. These local proofs do not replace those suites or claim their candidate-bound receipts. The candidate-only criterion is released because the immutable release candidate is required for that receipt; no execution-correctness criterion is waived.
