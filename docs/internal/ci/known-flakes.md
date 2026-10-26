# Known flakes and root-caused intermittent failures

This page lists the intermittent CI failures that have a known cause, so that nobody "fixes" an
environmental failure in code, and nobody reruns a real failure as if it were environmental.
`AGENTS.md` links here. Add an entry when you diagnose a new recurring failure, and move an
entry to the root-caused section once its fix lands.

## Environmental: rerun the shard

### Postgres `40P01: deadlock detected` on integration shards

- **Symptom.** An integration shard fails with a transient `40P01: deadlock detected` under
  concurrent host load. Two examples are `DbUpMigrations.MobileOfflineDemoSeed` and
  `OGC API Maps and Tiles`.
- **What to do.** Rerun the failed shard with `gh run rerun <run-id> --failed`. Do not treat it
  as a code failure. This applies to PR shards, batch shards and trailing trunk shards alike.
- **Where the fix lives.** Root-cause serialization lives in `PostgresFixture` and `SeedRunner`
  (#1568).

## Root-caused: do not rerun blindly

### `IMetadataV2GraphProvider` cannot be resolved for `ResourceValidator` (#4640)

- **Symptom.** `Server Tests (Core and Cloud Contracts)` failed intermittently on a FeatureServer
  request with:

  ```text
  Unable to resolve service for type
  'Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphProvider' while attempting to
  activate 'Honua.Core.Features.Validation.ResourceValidator'
  ```

- **Cause.** The host had booted in the `Test` environment without the provider composition:
  - `WebApplicationFactory` builds its web host builder, which snapshots every `ASPNETCORE_*`
    variable, before `ConfiguredWebApplicationFactory` takes its environment gate.
  - A host that did not pin its environment therefore inherited the
    `ASPNETCORE_ENVIRONMENT=Test` that a concurrent host held inside `StartInEnvironment`.
  - A `Test` host skips `InfrastructureCompositionRoot` unless it opts in.
- **Fix.** `ConfiguredWebApplicationFactory` now pins the requested environment on the builder. A
  host that needs the production composition in `Test` sets
  `HONUA_REGISTER_TEST_INFRASTRUCTURE=true` via `UseSetting`.
- **Not this failure.** The `GeoprocessingSeedHostedService` warning about
  `IMetadataV2GraphStore` is expected on `Test` hosts that wire their own providers.
- **If it recurs.** Before rerunning, check the host's `Application starting ... Environment` log
  line and the `PostGIS preflight check skipped` count.

## Not flakes: batch CI is stricter than local builds

The Linux batch lane has analyzer rules that may not fire locally on Windows (for example
`CA1873` on log-argument evaluation). A clean local warnings-as-errors build does not guarantee
that the batch build passes. Read the batch log with `gh run view <id> --log-failed`, then fix the
problem on the PR branch.
