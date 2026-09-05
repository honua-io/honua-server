# Offline server-test shard rebalance (#3711)

## Decision

This is a reviewed, static rebalance of `.github/ci-shards.json`. Runtime shard
selection remains deterministic and contains no timing-, API-, or model-driven
choice. The proposal folds twelve measured micro-shards, adds one static MCP
session/transport shard, and reduces a full server-test matrix from 67 jobs to
57 jobs.

## Reproducible baseline

The baseline is seven successful full-matrix `trunk` runs, collected on
2026-08-30 with the Actions API and artifact download endpoints:

| Run | Event | Head SHA |
|---:|---|---|
| 33328547614 | workflow_dispatch | `bfc3f2ed123a7a31c0737a0ee1c1985bca4d7d36` |
| 33316642816 | schedule | `b24930c7bd4975f1c856eb60787ae9418af2c4ba` |
| 33315000061 | workflow_dispatch | `b24930c7bd4975f1c856eb60787ae9418af2c4ba` |
| 33310891579 | workflow_dispatch | `356b58933ea257c739079698f18423c03e4e3e35` |
| 33306669840 | workflow_dispatch | `fd2e22035bf3bd9c0e8b7a93f207f9bba35fb0ae` |
| 33298284484 | workflow_dispatch | `90555062e5bc685926900c8ae6d47aa426cf993a` |
| 33267859930 | workflow_dispatch | `1ce78c97fdcb7dee2dd4650773a6e2198ee41c7e` |

For each run:

```bash
gh run download RUN_ID -p 'server-test-results-*' -D evidence/RUN_ID
gh api "repos/honua-io/honua-server/actions/runs/RUN_ID/jobs?per_page=100" \
  --paginate --slurp > "evidence/RUN_ID-jobs.json"
```

The 410 timing/TRX pairs were audited with:

```bash
scripts/ci/audit-shard-headroom.py --timings-dir evidence --markdown
```

Across the same 410 successful `Server Tests (*)` jobs, median non-test job
overhead (`job completed_at - started_at - timing.duration_seconds`) was **6.47
minutes** and p90 was **7.47 minutes**. Mean full-matrix server-test runner time
was **880.8 minutes per run**.

## Fold map

The p90 values below are test-step minutes from the audit. Filters and paths are
unioned into the destination; the class-coverage guard proves every test remains
owned.

| Folded shard(s) | p90 | Static destination |
|---|---:|---|
| Workflow Packages | 0.9 | Server Features Misc |
| Cloud & Contract; Performance; Comprehensive | 0.3; 0.3; 1.5 | Cloud Contract Performance and Comprehensive |
| Raster Serving; Geometry Tiles and Terrain | 2.8; 1.7 | Raster Serving Scene Geometry and Terrain |
| RasterImport | 2.1 | File and Raster Import |
| FeatureServer Query; Services; Replication | 0.9; 0.2; 2.7 | FeatureServer Endpoints Query Services and Replication |
| OGC API Features Transactions | 1.2 | OGC API Features |
| Server Features Admin Credentials | 2.9 | Server Features Admin Authentication and Credentials |

`SensorThings` (1.8m p90) and `GP Devkit CLI` (0.1m p90) remain separate because
each targets its own test project; combining them would require a multi-project
runner contract rather than a map-only rebalance.

The MCP shard measured 14.7m p90, so lowering its temporary #3664 cap in place
would be unsafe. The two session/transport integration fixtures that drove that
growth move to `MCP Sessions`; the remaining `MCP` shard returns from 20/30 to
15/25 minutes. Both are static filters over the same `Honua.Ai.Tests` project.

The matrix reduction is ten jobs per full run. At the measured 6.47m median
overhead, the conservative setup/build-only saving hypothesis is **64.7 runner
minutes per full matrix** (7.3% of the 880.8m baseline). This is a proposal, not
a claimed realized win; post-land Actions evidence decides it.

## Post-land measurement plan

After 30 successful full-matrix `trunk` runs on the landed map:

1. Fetch run IDs from `GET /actions/workflows/ci.yml/runs` and jobs from
   `GET /actions/runs/{run_id}/jobs`, retaining only completed successful trunk
   runs whose matrix contains all 57 server-test jobs.
2. Download every `server-test-results-*` artifact and rerun
   `audit-shard-headroom.py` over the 30-run cohort.
3. Compare with the baseline using the same definitions: per-shard test p50/p90,
   non-test overhead p50/p90, total server-test job runner minutes per run, and
   full-matrix wall time.
4. Accept the efficiency claim only if aggregate median runner time falls by at
   least 50 minutes/run, no destination exceeds 80% of its inner cap, both MCP
   shards stay below 80%, and the coverage/router guards remain green. Otherwise
   revise the offline map in another reviewed PR; do not add runtime selection or
   raise budgets silently.
