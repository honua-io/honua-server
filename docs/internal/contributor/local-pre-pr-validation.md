# Local pre-PR validation

`scripts/ci/pre-pr-check.sh` uses the hosted affected-project and shard selectors.
Use `--dry-run` to inspect its selection before compiling. The plan distinguishes
solution-filter roots from the evaluated dependency closure: removing a root does
not remove a project referenced by another root. MSBuild's
`GenerateRestoreGraphFile` evaluates imports and conditional references (Release
for builds, Debug for the formatter's workspace), including the analyzer reference
in `Directory.Build.props`. Graph evaluation needs the SDK and build-slot
admission, but it downloads no packages and compiles no code. `--plan-json` stays
a selector plan and does not evaluate that graph.

| Mode | Architecture enforcement | Other work |
| --- | --- | --- |
| FAST, accounted-for implementation or test-body edits | Existing source topology assertions in `Honua.Architecture.SourceTests` | Affected build and unit tests, retained Fast smokes, MCP checks, owning-project format |
| FAST, changed or uncertain contract inputs | Full `Honua.Architecture.Tests`, including test-assembly discovery | Same FAST work; server shards and AOT stay deferred |
| Default SMART | Full architecture suite and source guards | Selected server shards and affected AOT verification |
| FULL | Full architecture suite and source guards | All solution projects, all shards, full format verification |

`Honua.Architecture.Tests` imports `.github/test-coverage-projects.props`, so a
solution filter that keeps that root still compiles the protocol, core, Postgres
and other test assemblies. FAST leaves that root out only when
`scripts/ci/pre-pr-contract-guard.py` accounts for every changed path. The source
suite has no integration-test project references and keeps the module-dependency,
cross-protocol isolation, and infrastructure back-edge assertions, plus the other
source and file guards. Hosted CI is unchanged and still runs the full suite.

FAST explicitly defers route and operation coverage, catalogue emission and drift,
parity, capability and proof-ledger discovery to default or FULL local validation
and hosted CI. These checks are not deferred when the guard detects a changed
contract. Only known implementation source roots and unchanged test discovery
metadata can take the bounded path. Route-bearing source, public declarations,
test names and attributes, added or deleted files, catalogue, parity and proof
inputs, and unclassified paths keep full architecture enforcement. The guard reads
committed, staged and working-tree versions separately, so a staged metadata edit
canceled in the working tree still requires enforcement. Selector and shared
build inputs force FULL. An unavailable or failing guard cannot select the
bounded path.

SMART and FAST format workspaces contain the projects that own the changed C#
files. `--include` bounds the analyzed files; the workspace bounds project
loading. The transitive closure of that workspace is printed separately.
Ambiguous or unowned files fail explicitly. FULL keeps full format enforcement.
Bounded format invocations use `timeout 20m`.

FAST can still have a substantial build closure: runtime and test-kit
dependencies, directly edited tests, and the retained Fast smokes stay required.
The closure output makes that cost visible rather than claiming a fixed project
limit. Run default SMART or FULL before opening a PR.

## Iteration modes

Inspect the selection before starting a build:

```bash
# Pure unit feedback; no Docker or full architecture collector
scripts/ci/pre-pr-check.sh --unit-only --dry-run
scripts/ci/pre-pr-check.sh --unit-only

# Affected units plus component/integration shards from the shared CI router
scripts/ci/pre-pr-check.sh --affected-tests --dry-run
scripts/ci/pre-pr-check.sh --affected-tests
```

Pick the mode by what the change needs to prove:

- Use unit feedback for an in-process change.
- Use affected-test feedback when provider or HTTP behaviour needs testing.

Both modes build their selected project union once and reuse the outputs for tests, so:

- do not rebuild the solution by hand, or launch parallel test processes alongside them;
- keep the source files and HEAD fixed until the command exits, and coordinate worker edits and
  commits between runs.

Shared or unaccounted inputs can broaden the selection. Inspect the dry run rather than removing
the safety fallbacks.

These modes are iteration tools, not PR validation receipts. Run the normal pre-PR gate before
publishing, and keep the full architecture, native and hosted gates. Affected-test logs, TRX
files and selection details are kept in unique directories under `tests/TestResults/`. See
[modular test feedback](../ci/modular-test-feedback.md) for resource ownership and cleanup.

## What the default run does

The default run is SMART mode. It is scoped to the change, not a full-solution build:

- It builds only the `compute-affected-projects.sh` closure of your diff against `origin/trunk`,
  composed as a throwaway solution filter. Test projects that neither run locally nor changed
  are pruned out.
- It format-checks only the changed `.cs` files (`dotnet format --include`).
- It runs only the ADR-0037 shard subset that the diff selects.

Two kinds of change widen that scope:

- **Shared build infrastructure widens it to all src projects.** Changing `Directory.*.props`,
  `Honua.sln`, `global.json`, `NuGet.config`, `.github/` or `scripts/ci/` makes
  `compute-affected-projects.sh` report `ALL`, so every src project compiles. Test-project
  pruning still applies.
- **A change to the selectors or shared test infrastructure escalates to FULL.** That covers
  `Directory.Build.props`, `Directory.Packages.props`, `tests/dotnet/Honua.TestKit/`,
  `.github/ci-shards.json`, `scripts/ci/capability-impact.py`, the feature-catalog and
  capability-key data, and `.github/workflows/`. A missing `python3` or a failing
  capability-crosswalk validation escalates too. You can also request FULL with `--full` or
  `HONUA_PRE_PR_FULL=1`.

FULL means no test-project pruning, a solution-wide format check, and `run_all` for the
server-test shards. Mid-edit, the scoped selectors cannot be trusted to scope themselves, and the
shard selectors live inside `.github/workflows/ci.yml`, so a workflow-only change is not a safe
exemption.

Useful flags:

- `--dry-run` prints the resolved build set, targeted shards and format scope, then exits. It
  costs nothing: no Docker, no restore, no build.
- `--fast` skips the heavy `Honua.Server.Tests` shards and the AOT publish. The pre-push hook
  uses it.
- `--base <ref>` overrides the diff base.

The script requires `jq`.
