# Benchmark Publication Process

This document describes how to refresh the public proof pack ([Benchmark Results](../operator/BENCHMARK_RESULTS.md)) so published numbers stay current with each release.

## When to Refresh

- **Every minor or major release** (e.g., v0.9 → v0.10, v1.0 → v2.0)
- **When benchmark infrastructure changes materially** (new benchmark classes, BenchmarkDotNet version bump, seed data changes)
- **When the proof pack is flagged as stale** in the [Release Checklist](RELEASE_CHECKLIST.md)

### Staleness Policy

If the proof pack is more than 2 minor releases behind the current version, it must be flagged as stale in the release checklist and refreshed before the next release ships.

## Refresh Steps

### 1. Run the Full Benchmark Suite

Trigger the `performance-benchmarks.yml` workflow via `workflow_dispatch` on the release tag or branch:

```bash
gh workflow run performance-benchmarks.yml --ref <release-tag>
```

Alternatively, run locally on release-candidate hardware:

```bash
./benchmarks/run-benchmarks.sh --category All --output All
```

### 2. Capture Environment Disclosure

```bash
./scripts/capture-bench-environment.sh > /tmp/bench-env.md
```

Review the output and paste it into the environment disclosure section of `docs/operator/BENCHMARK_RESULTS.md`.

### 3. Update the Performance Baseline

Replace `performance-baseline.json` in the repository root with the new JSON output:

```bash
cp benchmarks/Honua.Benchmarks/BenchmarkDotNet.Artifacts/results/<latest>.json performance-baseline.json
```

Verify the file format matches the existing schema (`Version`, `Created`, `GitSHA`, `Environment`, `Benchmarks` array). Set `GitSHA` to the full commit hash of the release tag.

### 4. Update Published Results

Edit `docs/operator/BENCHMARK_RESULTS.md`:

- Update the **Environment Disclosure** table with values from step 2
- Update the **Query Latency** table with new numbers from the baseline JSON
- Update the **Memory Footprint** table
- Update the **Operational Footprint** section if binary size, image size, or cold-start metrics changed
- Update the **Baseline date** to the current date

### 5. Run Regression Check

```bash
python3 scripts/check-perf-regression.py \
  --baseline <previous-baseline.json> \
  --current performance-baseline.json
```

Review any warnings or regressions. If critical regressions are detected, investigate before publishing.

### 6. Review and Commit

- Review the diff for accuracy and consistency
- Ensure no numbers are copied without corresponding environment disclosure
- Commit with a conventional commit message:

```bash
git add performance-baseline.json docs/operator/BENCHMARK_RESULTS.md
git commit -m "docs: refresh benchmark proof pack for <version>"
```

- Tag with the release version if part of a release branch

## Ownership

The proof pack refresh is owned by the maintainer running the [Release Checklist](RELEASE_CHECKLIST.md). It is a required step in the release process.

### File Ownership

| File | Owner |
| --- | --- |
| `docs/operator/BENCHMARK_RESULTS.md` | This process (`#542`) |
| `docs/operator/BENCHMARK_REPRODUCTION.md` | This process (`#542`) |
| `docs/operator/BENCHMARK_METHODOLOGY.md` | This process (`#542`) |
| `performance-baseline.json` | Benchmark infrastructure (`#335`) — updated by this process |
| `benchmarks/run-benchmarks.sh` | Benchmark infrastructure (`#335`) |
| `scripts/check-perf-regression.py` | Benchmark infrastructure (`#335`) |
| `scripts/capture-bench-environment.sh` | This process (`#542`) |
| `.github/workflows/performance-benchmarks.yml` | Benchmark infrastructure (`#335`) |

## References

- [Benchmark Results](../operator/BENCHMARK_RESULTS.md) — public-facing proof pack
- [Benchmark Methodology](../operator/BENCHMARK_METHODOLOGY.md) — measurement methodology and scope
- [Contributor Benchmarks Guide](benchmarks.md) — development-oriented benchmark documentation
- [Release Checklist](RELEASE_CHECKLIST.md) — release process including proof pack step
