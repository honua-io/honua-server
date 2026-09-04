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
