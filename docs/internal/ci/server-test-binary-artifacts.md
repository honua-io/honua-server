# Server-test binary artifact contract

The server-test artifact contract stages Release output for one test project,
retains neutral Unix plus `linux`/`linux-x64` runtime assets, and removes every
other RID directory. It preserves PDBs, test data, configuration, and NuGet
`obj` state so a clean consumer can run `dotnet test --no-build --no-restore`.

This contract does not enable a shared CI producer. Issue #2708 owns that
orchestration decision after the hosted transfer benchmark in #2722. The
payload implementation and integrity proof are tracked by #2721.

## Bounds and integrity

`package-server-test-binaries.sh` creates a deterministic gzip-1 archive and a
manifest keyed by exact commit, SDK version, project, and contract version. The
default limits are 256 MiB compressed, 512 MiB staged, and 120 seconds to
package. Evidence is valid for at most 24 hours; restore rejects future or
expired manifests even if the transport still retains their bytes.
`restore-server-test-binaries.sh` rejects toolchain/source/project
mismatches, size drift, digest failures, unsafe paths, missing project assets,
or missing binaries/PDBs before extraction is accepted.

`.github/server-test-artifact-projects.json` is the complete project registry.
Router validation requires it to equal the effective unique `csproj` set in
`.github/ci-shards.json`, including the legacy empty-`csproj` fallback to
`Honua.Server.Tests`.

## Reproducing the proof

After building and packaging the ten registered projects in Release, run the
artifact-only proof without retaining every raw output tree at once:

```bash
HONUA_SERVER_TEST_ARTIFACT_USE_EXISTING=true \
  scripts/ci/prove-server-test-binary-artifacts.sh /path/to/artifact-output
```

The proof creates a detached clean worktree and empty NuGet cache. For every
project it restores only the packaged payload, performs full test discovery,
and executes the registry's small representative test selection with
`--no-build --no-restore`.

The fast fixture used by CI router validation is:

```bash
scripts/ci/validate-server-test-binary-artifacts.sh
```

## Shard-local exact-head cache

For each selected project, the lexicographically first selected shard is the
only cache writer on attempt 1; siblings do not package duplicate project
payloads. The writer packages and saves its exact project payload after build
and before tests, so a test failure cannot prevent reuse.

Every shard requests exactly one cache key containing the full commit SHA,
resolved .NET SDK, archive contract version, project identity, runner OS, and
artifact-registry digest. There are no prefix fallback keys. A valid hit is
verified and unpacked before the unchanged `--no-build --no-restore` shard test
command. A miss or rejected/expired payload cleans partial test-project output
and safely executes the normal restore/build path. A rebuilding rerun may save
the exact key for a subsequent attempt.

### Attempt-1 opportunistic reads (#3213)

This section is the single contract for attempt-1 reuse. `ci.yml`, the plan
script and ADR-0074 point here rather than restating it.

Reads happen on attempt 1 as well as on reruns. This is the only build-reuse
slice promoted out of shadow, and it is promoted without adding a producer job:

- the payload it reads is already written today, by the same writer shard, on
  the same attempt — only the read was previously gated on `run_attempt > 1`;
- the lookup runs at the latest safe point after checkout and setup. A first
  miss waits 90 seconds and makes one final exact-key attempt before taking the
  unchanged restore/build path. The retry is deliberately bounded at two total
  attempts so it adds at most 90 seconds to a cold consumer;
- no `needs:` edge is introduced, so shards retain independent scheduling and
  the same-run producer fan-out regression measured in run 31768277005 cannot
  recur;
- cache write volume is unchanged, so the repository cache quota is unaffected.

#### Run-scoped keys

The key includes the workflow run id. Cache keys are immutable and a payload is
valid for 24 hours, so a key scoped only to the commit SHA becomes permanently
poisoned once its payload ages out: every later run of that unchanged head — a
Saturday scheduled full matrix, a manual dispatch — would download up to 256 MiB
in every one of ~65 shards, reject it on TTL (`rejected_cache_evidence`),
rebuild, and then fail to re-save because the immutable key already exists. That
repeats on every future run of that SHA. Before attempt-1 reads this could only
bite an explicit rerun more than 24 hours later; making reads unconditional
would have made it routine.

Binding the run id makes evidence at most as old as the run that produced it,
which is the only window shards actually consume: sibling shards share the run
id, and a rerun keeps the same run id across attempts, so both the attempt-1
sibling read and the #2735 failed-rerun read are preserved. Cross-run reuse at
an identical SHA is deliberately given up; it never worked before this contract.
When no trustworthy run identity is available the key is namespaced `runlocal`
and reads are disabled entirely.

#### Writer selection

The writer is the first selected shard for the project **in matrix order**, not
in lexicographic order. Actions dispatches matrix entries in declaration order,
so a lexicographic writer can sit behind siblings that therefore miss by
construction. On the current full matrix the lexicographic writer sat at index
15 for GeoServices (first sibling at 12), 29 for OGC Classic (20), 26 for OData
(23) and 3 for Server (0) — 18 shards that could never win a read. Matrix order
costs nothing and is never worse.

*Deferred (follow-up, not in this contract):* first-builder-wins, where every
shard that had to build probes the key with `lookup-only` after its build and
publishes if it is still absent. That removes the remaining ordering dependency,
but in the first dispatch wave many shards would probe "absent" simultaneously
and each pay archive staging (~370 MiB staged, ~145 MiB archive) before losing
the reservation. The Server Features shards have a history of exhausting hosted
runner disk (#1899, #2943), so this needs its own disk-headroom measurement.

#### Kill switch

`ci.yml` forwards the repository variable `HONUA_SERVER_TEST_PREBUILD_CONSUME`
verbatim. Exactly one rule interprets it, in `server-test-shard-cache.sh plan`:
**attempt-1 consumption is on only when the value is exactly the string
`true`.** Unset is off. The raw value is echoed back as `consume_switch` (sanitised to a bounded
character set so it cannot forge extra output lines) and printed in the job
summary, so a run states whether the switch was unset or set and to what.

The switch governs attempt-1 reads only. It never withdraws the pre-existing
#2735 failed-rerun read, which stays on regardless.

Rollback is one variable update:
`gh variable set HONUA_SERVER_TEST_PREBUILD_CONSUME --body false`. It reverts
reads to rerun-only. Nothing else changes: no branch
protection, required context, shard name, filter, timeout, service, result
attribution, or merge authority depends on this switch.

#### Fail-open

Fail-open is enforced at three levels and every outcome is printed in the job
step summary (read mode, switch value, lookup outcome, decision, and — only when
a build actually succeeded — an explicit "restored and built locally" line):

1. both bounded `actions/cache/restore` steps are `continue-on-error`, so a
   cache-service error, throttle, or timeout is a miss rather than a red shard;
2. `restore-server-test-binaries.sh` stays fail-closed on evidence (contract,
   project, source SHA, SDK, size, digest, TTL, archive entry safety) and
   `server-test-shard-cache.sh restore` converts any rejection into
   `restored=false` plus cleanup of partial output;
3. `server-test-shard-cache.sh plan` treats a malformed switch, attempt counter
   or run identity as "no read", i.e. build locally.

#### Trust boundary

The key binds the exact commit SHA and run id, so a payload is only ever
readable by other jobs of that same run. A fork pull request cannot write into
the base repository's default-branch cache scope, and its own scope is keyed to
a SHA and run no other head shares, so attempt-1 reads grant no cross-head or
cross-fork authority. A PR author reusing a payload built from their own head
gains nothing they did not already have by controlling that head.

#### Accepted limitation: NuGet cache save on materialized shards

`setup-dotnet-ci` wraps `~/.nuget/packages` in `actions/cache@v5` with a
prefix `restore-keys` fallback, and its post-job save is unconditional. A shard
that materializes the binary payload never runs `dotnet restore`, so when a
`*.csproj`/`packages.lock.json` change produces a new exact key, a fast
materialized shard can claim that key with the folder it prefix-restored — which
may be missing the newly added packages.

This is a cache-warmth issue, not a correctness one: a later job that exact-hits
that entry still runs `dotnet restore`, which downloads only the missing
packages. It is also bounded to the first run after a dependency change. Fixing
it properly means splitting the composite into `actions/cache/restore` plus a
late conditional `actions/cache/save`, which changes behaviour for every caller
of `setup-dotnet-ci` and belongs in its own change rather than here.

#### Hosted proof coverage

`server-test-shard-cache-proof.yml` was the opt-in hosted lane for this
contract. It exercised the **rerun** path only (it gated its own restore steps
on `run_attempt > 1` and never passed the attempt-1 flags), so the attempt-1
read has fixture and local end-to-end coverage but never had a hosted proof run.
Rather than extend a lane whose only non-dispatch trigger was a push to the
merged `ci/2735-shard-local-rerun-cache` branch, #3332 retired it; the deterministic
fixture `scripts/ci/validate-server-test-shard-cache.sh` remains the guard on
the live `ci.yml` behaviour.

Its recorded result stands as evidence. Hosted proof
[run 29167891150](https://github.com/honua-io/honua-server/actions/runs/29167891150)
used three independent jobs over the 46.4 MiB geoprocessing CLI project cache.
Attempt 1 built all three shards in parallel; the sole writer completed in
145.3 seconds versus 144.9 seconds for its non-writer sibling (0.27% delta).
Two consumers then failed deliberately. Failed-only attempt 2 left the writer's
original timestamps unchanged: the exact-hit consumer verified/unpacked the
cache, skipped build, and completed in 51.1 seconds (64.7% faster than its cold
attempt; 1.8 second transfer, 1.0 second integrity check, 0.8 second unpack).
The forced-miss consumer rebuilt successfully in 148.0 seconds and saved its
new exact key. The run completed successfully on attempt 2.

## Baseline evidence

Measured on commit `b9ef5d78858e4cc0a42c7f835dac4f663b6b3209` with .NET SDK
10.0.300. Times are local packaging times and intentionally exclude builds and
network transfer.

| Project | Raw MiB | Staged MiB | Archive MiB | Pack seconds | Discovered | Proof executed |
|---|---:|---:|---:|---:|---:|---:|
| ai | 1219.5 | 372.2 | 144.0 | 5.3 | 524 | 9 |
| geoprocessing-cli | 568.9 | 121.8 | 46.6 | 1.8 | 79 | 1 |
| geoservices | 1224.5 | 377.2 | 145.5 | 5.6 | 1633 | 13 |
| odata | 1223.2 | 376.0 | 145.4 | 5.5 | 492 | 3 |
| ogc-api | 1219.5 | 372.2 | 144.0 | 6.8 | 530 | 10 |
| ogc-classic | 1218.8 | 371.6 | 143.8 | 6.6 | 308 | 4 |
| scene | 1217.6 | 370.3 | 143.4 | 7.3 | 158 | 2 |
| sensor-things | 1217.2 | 369.9 | 143.3 | 6.9 | 32 | 1 |
| stac | 1217.5 | 370.3 | 143.4 | 6.9 | 111 | 15 |
| server | 1262.3 | 415.0 | 158.3 | 5.9 | 7589 | 22 |

Across all ten projects, 11.32 GiB raw became 3.43 GiB staged and 1.33 GiB
archived in 58.7 seconds. Full discovery found 11,456 tests and the artifact-only
proof executed 80 representative tests successfully with an empty NuGet cache.
