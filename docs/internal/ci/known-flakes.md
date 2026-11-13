# Known flakes and root-caused intermittent failures

This page lists the intermittent CI failures that have a known cause, so that nobody "fixes" an
environmental failure in code, and nobody reruns a real failure as if it were environmental.
`AGENTS.md` links here. Add an entry when you diagnose a new recurring failure, and move an
entry to the root-caused section once its fix lands.

## Automatic bounded quarantine

The shared server shard runner retries failed test methods once in the same job, inside
its original deadline. Only exit code 1 with complete, consistent TRX case evidence is
eligible; infrastructure failures, missing cases, failed/skipped retries and timeouts stay
red. A successful retry retains both raw attempts and emits a candidate containing their
hashes. The final TRX uses actual retry rows for the originally failed cases.

`Publish Flaky Test Quarantine` validates this evidence after same-repository CI and PR
Gate runs. It runs trusted default-branch code and treats downloaded artifacts only as
data. Each project/method has one deterministic issue, labelled `ci-flake`; an active
quarantine additionally carries `ci-flake-quarantine`. These bot-authored issue bodies
are the quarantine list. Closing an issue removes its exclusion immediately on the next
snapshot. Fix the underlying test and close its issue; do not renew an active expiry.

`scripts/ci/validate-flake-quarantine.py` enforces at most 25 active methods, literal
project-scoped identities, and a maximum 14-day lifetime. Expired entries automatically
return to gating. Invalid snapshots fail closed. Issue reads make up to three attempts
with backoff; a GitHub API outage that outlasts them fails the step with gh's redacted error,
not as an invalid quarantine. Issue writes are never repeated, so a lost response cannot
file a duplicate. When full, additional flakes still get
issues but remain gating. The runner and duplicate foundation/lean-gate coverage apply
exact method exclusions from a validated snapshot. Existing execution tripwires remain:
an exclusion that leaves a shard without executed cases does not make it pass.

`Flaky Test Detection` runs the active list in a separate non-gating job with retry and
exclusions disabled. Its rotating flake hunt also observes original failures without
retry masking. Raw results remain available as workflow artifacts.

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

Quarantine filtering also covers overlapping foundation, source-architecture, isolated unit-fast, Postgres compatibility, and general Worker suites. Dedicated execution-proof receipt requirements remain strict; a quarantined method cannot substitute for a required proof receipt.
