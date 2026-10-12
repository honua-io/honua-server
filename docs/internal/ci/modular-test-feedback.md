# Modular unit feedback and full coverage

`pre-pr-check.sh --unit-only` is an explicit developer feedback lane. It tests
only affected catalogued isolated unit projects, building their forward runtime
and pure support dependencies once through a temporary solution filter, then
running each selected project with `dotnet test --no-build --no-restore`. It does not build
Architecture.Tests, integration consumers, or a solution filter. It does not
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
remains the authority for test shard routing. The updater also registers unit/component projects in the existing artifact
transfer/prebuild registry. Catalogued unit shards include Fast cases; integration
shard exclusions remain unchanged. Registering a project does not
schedule its tests in hosted CI: retain/add its full-gate shard separately.

Refs #5958 (catalog, feedback planning and discovery); umbrella #5955 owns the
remaining module splits and full rollout validation.
