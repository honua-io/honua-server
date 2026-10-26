# GP source re-audit for AI admission — 2026-10-26

This is a bounded source/evidence applicability review of admission candidate
`2e99ffb426ee4f0e8cfd4acac236cf5eb9afe8e7` against merged trunk
`3350139bd0fb665685628f7c8e5f615ca9ebcf81`. It does not mint a release qualification,
change a frozen release-pack pin, or substitute for the new candidate's required runtime proofs.

## Changed catalog sources and affected rows

Only two files in the catalog digest's source roots differ from that trunk:

- `GeoprocessingServiceCollectionExtensions.cs` adds `AddAiExecutionAdmission` in imagery inference registration. Existing executor registrations, routing, capability declarations and other process implementations are unchanged. The added services are consumed by model-backed imagery, rather than by the remaining nonmodel algorithms.
- `Execution/ImageryInferenceJobExecutor.cs` constructs a trusted internal execution context from the stored job and optional verified workflow run. The run must match the stored submitter and reference this job. The request still selects the same configured model, source bytes and inference task. Classification output decoding, georeferencing validation, class histogram and publishing paths are unchanged.

The directly affected row is **`imagery.classify`**. Its two cited semantic proofs still
execute the production executor and HTTP adapter against the committed loopback classifier:
the positive test asserts model identity, grid/CRS, frozen class layout and class counts;
the negative test rejects a different committed model despite structurally valid output.
Their frozen scene, model program, expected layout/counts and semantic assertions are unchanged.
The proof's setup now supplies canonical stored-job identity and an execution-context factory,
and injects `RecordingAiAdmission` into the HTTP adapter. Consequently the row remains proven
for classification output semantics, **not** for shared admission persistence, stop controls,
provider approval or installed authorization. Those require their separate tests/receipts.

The other **100 rows** retain their prior evidence verdicts: none of their operation bodies,
catalog/capability/destructive declarations or executor selections changed in the digest roots.
The registration change introduces admission dependencies only in the imagery path; it does
not alter the nonmodel process implementations. No row is promoted and the denominator remains
101 (101 proven under the matrix's existing semantic-execution definition).

## Digest disposition and required execution

After that applicability review, `audit.catalogSourceContentDigest` may be synchronized to
the reviewed source bytes. The original comprehensive audit revision/date remain historical;
this supplement records the narrow later review. Matrix evidence bodies and frozen oracles
must remain unchanged. A new-head architecture check must recompute the digest, and the
required imagery execution proof must run before landing. A previous affected-shard pass
or an earlier proof cannot establish that the repaired candidate completed its runtime gate.
