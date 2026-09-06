# Copy-features execution proof

`CopyFeaturesExecutionProofTests.CopyFeatures_RealPublishedLayer_PreservesSelectedValuesZSchemaSridAndProvenance` protects the 2026.1 whole-catalog GP GA promise. The required PR Gate runs `Category=CopyFeaturesExecutionProof` with a real PostGIS container and retains the TRX receipt.

The test publishes a typed source table before calling the production `data-management.copy-features` executor. It resolves both the target and source through the same Metadata v2 provider query router used by protocol reads.

| ID | label | score | note | EPSG:4326 XYZ |
| --- | --- | --- | --- | --- |
| 11 | alpha | 7 | retained | 12,34,56 |
| 13 | beta | 14 | null | -20,40,80 |
| 15 | gamma | 21 | third | 30,-10,90 |

The three independently specified selections are all rows; `score >= 14` yielding IDs 13,15; and that predicate intersected with `objectIds=11,15` yielding only ID 15. The oracle uses these literals, never values captured from the implementation under test.

Readback asserts all attributes, exact XYZ coordinates, output SRID, copied schema including field length/type/nullability, a distinct target layer, and source/operation provenance. The original source is then read back against all three literal rows and its original metadata to detect mutation.

The operation creates typed storage from the canonical source schema and streams the selected provider read into it transactionally. Publication remains disabled while source schema and policy metadata are retained, then is enabled before the executor publishes its receipt.

## Candidate evidence sequencing

The immutable candidate required by #3848 and linked post-cut GP certification does not exist for this pre-cut PR. Exact-candidate execution and receipts are released from this PR until the candidate exists. Repository fixture execution and required CI are evidence for this implementation, not a claim that later certification has run.
