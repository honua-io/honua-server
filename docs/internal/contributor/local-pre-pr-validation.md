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
