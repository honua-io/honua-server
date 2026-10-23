# Geoprocessing evidence re-audit for PR #5731

The security remediation changed content pinned by the operation matrix without
refreshing its audit receipts. Required PR Gate run 37855849685, build/test job
113580154257, correctly rejected the catalog-source digest and the first changed
proof method. The guard and manifest were unchanged; compilation and 5,183 fast
server tests passed. This is attributable to the PR, not runner infrastructure.

The comparison baseline is trunk
`f6e6562917b08edd801e11e8f66b0a1b2165bd1d`, merged into the repair branch. The
re-audit compares every pinned source and proof method with that baseline before
refreshing any digest. Existing status, entry-point, fixture, assertion and gap
records are retained.

## Content review

- The only changed catalog-digest input is `Execution/ManagedDistance.cs`.
  Its two added CodeQL comments explain exact coincidence and signed zero.
  No expression, boundary condition, return value or executor dispatch changed.
- Aspect, spectral-index, reprojection and fixed-value rasterization proofs add
  comments explaining exact flat-slope or nodata/burn sentinels. Their production
  invocations, fixtures, metadata checks, cell assertions and tolerances remain.
- Resampling and IDW name the existing integer column/remainder and row/quotient
  calculations before converting to double. The operation, dimensions, source
  fixtures, independent sampling/weight oracle and all assertions remain.
- The PDAL proof replaces `NotBeNullOrWhiteSpace` with an explicit
  `string.IsNullOrWhiteSpace` rejection before writing CRS text. Null, empty and
  whitespace CRS still fail. Its binary LAS oracle, XYZ/attribute/scale/header
  checks, changed-intensity rejection and decoded authority-code equality remain.
- The proximity proof adds a sentinel comment inside the same method. Its exact
  nodata/allocation checks and approximate-distance checks remain.

## Re-audit results

- All existing catalog and method digests reproduced from the trunk baseline,
  using the unchanged architecture guard's own digest and Roslyn method resolver.
- Reversing only the reviewed edits restored the complete baseline content of
  all six changed pinned source/proof files. No additional change was accepted.
- Native C# comparisons found all 64 resampling and 25 IDW cell coordinates
  bit-identical between the original expressions and the named row/column form.
- Refreshed one catalog digest and seven distinct method digests across nine
  evidence receipts. Operation statuses, entry points, assertions, fixture paths,
  summaries and gap issues are unchanged.
- Native Windows SDK 10.0.100 compiled the focused architecture project and its
  required references in Release, with one MSBuild worker. Before the repair,
  the class executed four tests: two passed and the same two CI guards failed.
  After the repair, all four passed; none failed or skipped.

Executed after the initial Release build:

```powershell
dotnet test tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~GeoprocessingOperationEvidenceMatrixTests
```
## Verification boundary

The required pre-PR script has only a Bash entry point. This Windows repair packet
forbids invoking `bash.exe` or WSL, so `scripts/ci/pre-pr-check.sh --fast` is
blocked by that host rule. A native Release run of the named architecture class
is the focused verification; this audit does not claim a full pre-PR gate or a
new GDAL/PDAL production execution receipt.
