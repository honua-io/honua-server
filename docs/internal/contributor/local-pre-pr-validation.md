# Local pre-PR validation

`scripts/ci/pre-pr-check.sh` uses the hosted affected-project and shard selectors.
Use `--dry-run` to inspect its selection before compiling. The plan distinguishes
solution-filter **roots** from the **evaluated dependency closure**: removing a
root does not remove a project referenced by another root. MSBuild's
`GenerateRestoreGraphFile` evaluates Release imports and conditional references,
including the analyzer reference in `Directory.Build.props`; graph evaluation
requires the SDK and build-slot admission, but downloads no packages and compiles
no code.

| Mode | Architecture enforcement | Other work |
| --- | --- | --- |
| FAST, accounted-for implementation/test-body edits | Existing source topology assertions compiled in `Honua.Architecture.Tests/Topology` | Affected build/unit tests, MCP taxonomy/registry checks, changed-file format |
| FAST, changed or uncertain contract inputs | Full `Honua.Architecture.Tests`, including integration-test assembly discovery | Same FAST work; server shards and AOT remain deferred |
| Default SMART | Full architecture suite | Selected server shards and affected AOT verification |
| FULL | Full architecture suite | All solution projects, all shards, full format verification |

The independent topology project links the original module-dependency,
cross-protocol isolation, and infrastructure back-edge assertions. It retains
the repository analyzers and has no runtime or integration-test project
references. The full suite still compiles and runs those same assertions.

FAST explicitly defers all remaining architecture tests, including route and
operation coverage, catalogue emission/drift, parity, capability and proof-ledger
discovery, to default/FULL local validation and hosted CI. These checks are **not
deferred** when the guard detects a changed contract. Only known implementation
source roots and unchanged test discovery metadata can take the bounded path.
Route-bearing source, public declarations, test names/attributes, added/deleted
files, catalogue/parity/proof inputs and unclassified paths retain full
architecture enforcement. The guard examines committed, staged and working-tree
versions separately, so a staged metadata edit canceled in the working tree still
requires enforcement. Changes to the selection machinery and shared test/build
inputs force FULL; an unavailable or failing guard cannot select topology-only.

SMART/FAST format workspaces contain the projects owning the changed C# files,
with their actual transitive closure printed separately. `--include` bounds the
analyzed files, while the workspace bounds project loading. Ambiguous/unowned
files fail explicitly. FULL keeps full format enforcement. Format invocations are
bounded by `timeout 20m`.

FAST can still have a substantial build closure: runtime/test-kit dependencies
and directly edited tests remain required. The closure output makes that cost
visible rather than claiming a fixed total-project limit. Run default SMART or
FULL for the complete local gate before opening a PR.
