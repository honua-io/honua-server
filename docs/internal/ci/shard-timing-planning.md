# Timing-aware local shard scheduling

Local validation can start the longest selected shard first, using retained timing
reports from ordinary successful runs. It does not add workers or change the
canonical selection, effective filters, coverage checks, or resource admission.
`pre-pr-check.sh` and `local-test-feedback.py` use `tests/TestResults` by default;
set `HONUA_LOCAL_CI_TIMINGS` to a directory of retained reports, or pass
`local-test-feedback.py --timings-dir <directory>`.

Only positive finite durations from passing, non-timeout runs within 30 days
qualify. The shard, project and effective filter must match current routing.
Copied reports are deduplicated; the most recent ten observations contribute to
each median. If any selected shard lacks compatible evidence, canonical order is
retained. Timing hints are never a validation receipt or permission to skip tests.
The pre-PR dry run still shows canonical selection without reading timing hints;
the feedback JSON also records whether execution order changed.

## Propose a rebalance from existing runs

Download or retain ordinary CI timing JSON and matching TRX files together. Each
`<name>.timing.json` must have its sibling `<name>.trx`; no benchmark campaign is
needed. Obtain the current fingerprint and evidence gaps with:

```bash
python3 scripts/ci/audit-shard-headroom.py --rebalance --timings-dir /path/to/results
```

A proposal requires at least three distinct compatible runs for **every** shard
in a project, complete passing TRX results with stable class inventories, and a
reviewed grouping map. Create a JSON map using the reported fingerprint:

```json
{
  "schema": "honua.shard-balance-groups/v1",
  "routingFingerprint": "<reported fingerprint>",
  "projects": {
    "tests/dotnet/Example.Tests/Example.Tests.csproj": [
      {"name": "shared-database-collection", "classes": ["Example.QueryTests", "Example.EditTests"]}
    ]
  }
}
```

Review the current source for collection and fixture dependencies before grouping.
Include every observed class exactly once; classes sharing a collection or fixture
that must stay together belong in one group. This is an explicit human assertion,
not something inferred from display names or timing files. Re-review it after
source changes even if routing has not changed.

```bash
python3 scripts/ci/audit-shard-headroom.py --rebalance \
  --timings-dir /path/to/results --groups-file /path/to/reviewed-groups.json
```

The report proposes assignments for up to two slowest eligible projects using
whole groups and their existing shard counts. Summed test durations are a balancing
heuristic, not a wall-clock prediction: parallel test execution and fixture startup
are not additive. The tool never edits `.github/ci-shards.json`. Review any proposed
move, update canonical filters, and run the existing exact-ownership and router
guards before applying it. Missing history, changing class inventories, incomplete
TRX, or stale grouping fingerprints produce explicit gaps rather than estimates.

The October 13 implementation found only partial selective-run history compatible
with current routing. No shard boundaries were changed without the required
complete evidence. Existing AOT, native integration and architecture proofs remain
required. To roll back local ordering, use an empty timing directory; canonical
order is the default when evidence is absent.
