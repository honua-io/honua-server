# Modular unit feedback and full coverage

`pre-pr-check.sh --unit-only` is an explicit developer feedback lane. It tests
only affected catalogued isolated unit projects, building their forward runtime
and pure support dependencies once through a temporary solution filter, then
running each selected project with `dotnet test --no-build --no-restore`. It does not build
Architecture.Tests or integration consumers. It does not
validate a PR and does not replace normal fast/full checks, shard execution,
image, AOT or provider gates. Shared build/CI changes select all isolated units. Unaccounted source, test,
eng or build-input changes also select all eligible units with an explicit reason.

```bash
scripts/ci/pre-pr-check.sh --unit-only --dry-run --base origin/trunk
scripts/ci/pre-pr-check.sh --unit-only --base origin/trunk
```

The dry run performs only Python/XML/git discovery. The executor uses the selected
PATH dotnet command, so shared workers must keep their ordinary admitted shim or
supervisor execution environment. This tool does not supply admission, replace
resource policy, or launch Docker. `--unit-only` cannot combine with `--fast` or
`--full`; neither existing mode changes behavior.

`.github/test-projects.json` inventories every tests/dotnet project, including
support libraries and hosts. Each entry declares `role`, `runtimeOwners` (explicit
runtime project paths), `resources`, `isolated`, `coverageRequired`, and `shardRouted`. The latter distinguishes independently
routed projects from existing foundation-family projects; cataloguing a foundation
unit does not make its classes require duplicate shard execution. Existing
combined test projects remain `mixed` unless reviewed as fixture independent;
resource-free source execution alone does not prove that their assembly graph is
isolated. Component and integration projects remain outside unit feedback.
Temporary-file/in-process unit tests require no external service reservation.

After adding or moving a test project:

```bash
python3 scripts/ci/test-projects.py update
python3 scripts/ci/test-projects.py validate
python3 scripts/ci/test-test-projects.py
```

Review generated defaults before committing. The updater preserves reviewed
entries, proposes conservative roles for new projects, records their actual
forward runtime closure as owners, and generates the coverage MSBuild import and
assembly list. Do not approve unrelated protocols merely because they appear in
the proposed owners: remove the unwanted reference, or classify the project as
mixed. Role/resource declarations are reviewed policy; XML inspection is a
conservative source guard, not a replacement for evaluated MSBuild identity.
Unknown properties/imports and missing project references fail closed.

The guard checks isolated units transitively for Server, full TestKit, other test
or integration projects, unrelated protocol owners, and integration packages.
It scans linked Compile files as source inputs without adding the source-owning
project assembly. Pure helper source links therefore retain selection on helper
changes without importing the full legacy TestKit dependency graph. Canonical
repository analyzers are included in forward planning. Unit consumers may use
reviewed TestKit.Abstractions and TestKit.Core. CoreKit itself is restricted to
Core, Core.Abstractions, Geometry and those pure support libraries (plus repository
analyzers); Hosting, providers, protocols and container fixtures are rejected
transitively. A consumer may separately source-link a graph fake and explicitly
reference Hosting without compiling that fake into CoreKit. Other support
assemblies require an explicit guard policy extension and transitive review.

The normal Architecture.Tests build imports `.github/test-coverage-projects.props`
and copies `.github/test-coverage-assemblies.txt`. Its coverage discovery loads
all required assemblies and throws if the inventory or any assembly is missing;
new UnitTests and ComponentTests therefore cannot silently vanish from full
coverage scans. The generator is checked by PR Gate, while `.github/ci-shards.json`
remains the authority for test shard routing. The updater synchronizes the existing artifact transfer/prebuild registry to
the exact scheduled project set from ci-shards.json, preserving reviewed proof
filters. Foundation-only and opt-in component projects do not acquire duplicate
shard artifacts merely by being catalogued. Catalogued unit shards include Fast cases; integration
shard exclusions remain unchanged. Registering a project does not
schedule its tests in hosted CI: retain/add its full-gate shard separately.

Refs #5958 (catalog, feedback planning and discovery); umbrella #5955 owns the
remaining module splits and full rollout validation.

Every catalog project forbids direct or transitive references to executable test
projects, including references hidden behind support kits. Support and host helpers
are allowed dependencies. The canonical `Honua.Architecture.Tests` project is the
sole exception: its generated references intentionally collect every required
test assembly for full coverage discovery; this exception does not apply to
component hosts or ordinary tests.

Minimal component projects reject transitive Server, full TestKit, and protocol
modules outside their reviewed runtime owners, independently of the unit-only
`isolated` flag. They may use Hosting and explicit provider/fixture support. New
component suites have dedicated shards; existing foundation/provider families
(such as the in-process DuckDB suite) retain their existing scheduling and are
not duplicated merely because their catalog role is component.

Coverage inventory keys include both project and fully qualified class name.
Partial migrations may preserve the same class name in multiple assemblies;
each assembly retains its own methods, source files, and shard ownership.

## Current suite and support topology

Runtime and protocol `*.UnitTests` projects contain reviewed in-process cases and
reference their owning runtime closure. Narrowed Core/Core.Security and
Postgres.Security suites retain their existing project names. Io startup cases
remain in Server.Tests; there is no Io.UnitTests project. Hosting middleware
cases that need TestServer run in Hosting.ComponentTests, outside unit feedback.

Protocol component suites cover Ai, GeoServices, OData, OgcApi, OgcClassic,
Scene, SensorThings and Stac through minimal hosts. Postgres-backed suites use
bounded fixture containers; Ai, Hosting and Scene component cases use in-process
hosts without a database. Remaining mixed protocol and Server.Tests assemblies
retain full-host integration coverage and existing fast/full policies.

| Support project | Intended consumers |
| --- | --- |
| TestKit.Abstractions | Shared attributes and fixture-independent contracts |
| TestKit.Core | Pure Core/Geometry helpers; no host or provider fixtures |
| TestKit.Hosting | Minimal component TestServer composition |
| TestKit.Postgres / TestKit.Redis | Explicit bounded service fixtures |
| TestKit.Postgres.Hosting | Minimal Postgres-backed component composition |
| Legacy TestKit | Retained full-host integration suites |

Canonical linked graph fakes and `tests/dotnet/Shared/SceneUnitSupport` remain
source inputs of their actual consumers. Changing a linked helper selects those
consumers without importing its former executable test assembly.

## Running one component suite

Use the admitted PATH `dotnet` command in the checked-out worktree. Run build
and test sequentially, and await completion before starting another resource
consumer. For example, the OData contracts use the same runner for the minimal
host and explicitly configured external parity; the default below starts its
owned minimal host and bounded Postgres fixture.

```bash
dotnet build tests/dotnet/Honua.Protocols.OData.ComponentTests/Honua.Protocols.OData.ComponentTests.csproj --configuration Release /p:UseSharedCompilation=false
dotnet test tests/dotnet/Honua.Protocols.OData.ComponentTests/Honua.Protocols.OData.ComponentTests.csproj --configuration Release --no-build --no-restore --filter 'FullyQualifiedName~Honua.Protocols.OData.ComponentTests.ODataContractTests'
```

The filter above runs shared protocol contracts; omit it to include host isolation
and fixture lifecycle cases. The component project declares its runtime/support
closure and requires no full Server.Tests or Architecture.Tests build. Use the
corresponding catalogued component project for another protocol. Database-backed
components require Docker and the existing shared admission/resource policy;
resource-free units still require normal build admission. Do not bypass the
worker shim, claim an inherited reservation, or launch concurrent standalone
containers to evade that policy.

Fixtures own and dispose their hosts, clients, databases and containers. Let
normal test cancellation finish cleanup before releasing the job or worktree.
After interruption, inspect the owning job and its recorded resources; clean up
only that job's processes and named/labeled containers after confirming ownership.
Do not kill unrelated testhosts, remove shared locks, or prune Docker globally.

## Validation and completion

Affected unit feedback is useful before the normal developer gate:

```bash
scripts/ci/pre-pr-check.sh --dry-run --base origin/trunk
scripts/ci/pre-pr-check.sh --base origin/trunk
# Deliberate full validation retains Architecture and integration discovery:
scripts/ci/pre-pr-check.sh --full --base origin/trunk
```

A unit feedback result does not validate a PR. Full discovery still checks every
required assembly, endpoint coverage, tier reachability and assembly-specific
shard ownership. Moving source preserves test identity and assertions, but an
untiered test does not inherit a baseline waiver from a different assembly:
reviewed pure cases need an explicit Fast tier, component cases an explicit
Integration tier, and existing Slow/integration choices must remain intact.

At the rollout checkpoint before the latest trunk merge, 21 of 23 directly run
unit projects and eight of nine component projects passed. The two unit failures
identified missing Skia native assets; the component failures identified OGC API
fixture setup. Corrections have been applied but need fresh execution at the
merged head. Catalog/selection fixtures and static shard coverage have passed;
the complete expanded .NET graph, live architecture/tier guards, full integration
gates and hosted PR verification remain pending. These partial runs are not a
completion receipt. OData image/performance qualification remains a separate
opt-in pilot with its documented missing managed lifecycle evidence; ordinary
component execution above does not certify those measurements.
