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

## Shard-local failed-rerun cache

Production `Server Tests (<shard>)` jobs keep their independent attempt-1
restore/build path. For each selected project, the lexicographically first
selected shard is the only attempt-1 cache writer; siblings still build and test
independently but do not package duplicate project payloads. The writer packages
and saves its exact project payload after build and before tests, so a test
failure cannot prevent reuse.

On workflow attempts after the first, a failed shard requests exactly one cache
key containing the full commit SHA, resolved .NET SDK, archive contract version,
project identity, runner OS, and artifact-registry digest. There are no prefix
fallback keys. A valid hit is verified and unpacked before the unchanged
`--no-build --no-restore` shard test command. A miss or rejected/expired payload
cleans partial test-project output and safely executes the normal restore/build
path. A rebuilding rerun may save the exact key for a subsequent attempt.

Every job reports the hit/miss/rejection reason plus transfer, integrity,
unpack, package, and save timings in its step summary. Cache save contention or
service failure is non-gating; test/build failures keep their existing
attribution and advisory semantics. The deterministic contract fixture is:

```bash
scripts/ci/validate-server-test-shard-cache.sh
```

Hosted proof [run 29167891150](https://github.com/honua-io/honua-server/actions/runs/29167891150)
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
