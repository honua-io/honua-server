# Native image migration refusal evidence

`Serving Image Boundary` runs the canonical production AOT image through
`scripts/ci/verify-native-migration-refusal.py` before saving a successful
verification marker. Lambda and Azure Functions host lifecycles are outside this
regression. The image runs its real `/app/Honua.Server` entrypoint; this does not
substitute a JIT executable or a test host.

The fixture creates an isolated PostGIS database with a nonempty migration
journal containing `002_CreateAttachmentsTable`, leaving the reviewed
`038_DropV1MetadataGraphTables` contract migration pending. Migration approval,
skip, and safety override settings remain unset. A passing result requires the
specific existing-database migration safety refusal and pending contract name,
exit code 1, no OOM, no unhandled exception or listening message, an unchanged
journal, and no core/minidump in the container's filesystem changes. Core and individual file limits are
64 MiB and .NET dump requests are enabled. A dump exceeding that bound still
fails through signal exit or a detected partial file. The receipt records the host
kernel core pattern because host-level core collectors can receive dumps outside
the container filesystem; exit-by-signal still fails regardless of dump location.

Each container has a 1 GiB memory limit (no extra swap), one CPU, 256 PIDs,
and a 10 MiB rotating Docker log. Cleanup attempts each container and network
independently even if another cleanup command times out.

The invocation bounds server exit to 240 seconds by default (maximum 600),
PostGIS readiness to 120 seconds, and each Docker operation separately. Resource
names are unique and cleanup removes only that invocation's containers/network.
The `native-migration-refusal-<source SHA>` workflow artifact contains `server.log`
and `receipt.json`, including positive refusal observations, source SHA, image
revision, immutable local image ID, and registry manifest digests when available.
The local image ID is a configuration digest, not a registry manifest digest.
The harness verifies the image's native-AOT label, production entrypoint, and
revision, then starts that immutable ID rather than resolving the input tag again.

To verify a registry-published candidate on a Docker-capable Linux worker:

```bash
python3 scripts/ci/verify-native-migration-refusal.py \
  --image ghcr.io/honua-io/honua-server@sha256:<manifest-digest> \
  --source-sha <full-source-commit-sha> \
  --output native-migration-refusal-evidence
```

This is regression infrastructure, not a claim that an arbitrary published
release has passed. Keep the successful receipt and logs with that release's
immutable image digest. An unrelated configuration or connection failure is a
failed regression, even when it exits 1 and produces no dump.

Cheap detector fixtures require no Docker or .NET build:

```bash
python3 scripts/ci/fixtures/validate-native-migration-refusal.py
```
