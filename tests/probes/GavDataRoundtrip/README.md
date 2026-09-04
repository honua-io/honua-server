# Data round-trip regression probes

Tracker: honua-io/honua-release#272. Baseline: f16b248dd7 (2026-09-04).

This isolated test project references the production I/O module and its format readers.
It deliberately uses the existing test friend-assembly name; it does not reference or
build the server host and is not registered in the solution or normal CI test shards.
Assertions express preservation requirements and are expected to fail while the
corresponding reported defects remain unfixed. No production code is modified.

Run one focused project through the lane's `dotnet` PATH shim:

```bash
HONUA_MSBUILD_NODE_CAP=4 timeout 290 dotnet test tests/probes/GavDataRoundtrip/GavDataRoundtrip.csproj --filter FullyQualifiedName~GavDataRoundtrip --logger 'console;verbosity=normal'
```

The canonical findings, verification results, duplicate dispositions, and coverage gaps
are recorded in honua-flow `docs/ops/bug-hunt-ga-vectors-2026-09-04/data-roundtrip.md`.
These probes exercise production readers/writers directly; they are not a claim of a
complete HTTP -> PostGIS -> HTTP format-pair matrix or external GIS certification.

Baseline outcome: 11 tests, 10 failures across nine findings, one passing control.
See `baseline-results.txt` for the exact observed outputs.

`diagnostic.patch` contains only two one-line corrections used to confirm the numeric
promotion and CSV dimensionality root causes. Applying it made the integer, CSV Z/M,
and neighboring CSV control probes pass (3/3; test execution 2.7014 seconds). It was
then reversed; production source in this branch remains identical to the baseline.
This is diagnostic evidence, not a complete reviewed fix for all findings.

To reproduce the failing-before/passing-after check, apply the patch and run the same
project with filter `FullyQualifiedName~Csv_ZmGeometry|FullyQualifiedName~GeoJson_Int64|FullyQualifiedName~Csv_Control`,
then reverse the patch. Other preservation probes remain expected failures.
