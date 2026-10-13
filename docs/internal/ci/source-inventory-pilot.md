# Semantic source inventory qualification pilot

Refs #6051. This additive CI experiment observes test metadata without building
or emitting the inspected test projects. The existing compiled collector,
trait-discoverer checks, 49 assembly references, NativeAOT gates, and production
reflection boundaries remain authoritative and unchanged.

The tool consumes the centrally pinned `Microsoft.CodeAnalysis.CSharp` version.
It has no production project reference. Its ASP.NET shared-framework reference
lets the reflection exporter load test metadata that references ASP.NET types;
it does not start an application or instantiate custom attributes.

## Inputs and output contract

The pilot covers OData isolated units, Scene protocol components, and GDAL tests.
The driver invokes design-time `ResolveReferences;GenerateGlobalUsings` with
`BuildProjectReferences=false`, `SkipCompilerExecution=true`, and
`DesignTimeBuild=true`. MSBuild supplies actual Compile items (including links
and generated global usings), reference paths, configuration, preprocessor
constants, language and nullable settings. Generating implicit usings can write
that generated source file; no inspected project's Build/CoreCompile/Emit is
invoked. Source analysis uses Roslyn semantic symbols, never namespace guessing.

**Already-built reference metadata is a prerequisite. This pilot does not yet
remove the build floor.** Reference resolution may need normal-validation
outputs and restored NuGet packages. Missing references, compiler errors,
unsupported language/attribute behavior and absent reflection outputs cannot
qualify. The tool emits synthetic assemblies only inside its `self-test`
fixtures; there is no inspected-source emission path.

`honua.ci.semantic-test-inventory/v1` records:

- Project/configuration/compiler identity, source and reference SHA-256 inputs.
- Test class and declaring class, return type, method name, generic arity and
  complete ordered parameter type/ref-kind signature (overloads remain distinct).
- Relevant declared and effective inherited attributes, constructor/named
  constant arguments, and explicit inheritance origin. Partial declarations
  aggregate through semantic symbols. Conditional code uses evaluated defines.
- Declared coverage values from canonical Protocol/Operation/Endpoint and
  xUnit Trait attributes. These are metadata coverage values, **not** a claim that
  arbitrary executable trait discoverers have run.
- Explicit compiler/unresolved/unsupported diagnostics.

The reflection exporter reads CustomAttributeData from existing compiled test
outputs and does not invoke attribute constructors. Binary SHA-256 evidence is
written alongside its inventory. The pipeline's existing exact-head admission
and artifact validation remain responsible for binary provenance.

`honua.ci.semantic-test-parity/v1` distinguishes `MetadataMatch` from
`RuntimeTraitSemanticsVerified`. Arbitrary custom Fact constructors and
executable ITrait discoverers are not modeled. Known UnitTest/Integration
metadata can match while their runtime discovery semantics remain unverified.
Even matching unsupported inventories produce `Status=notQualified`,
`Qualified=false`, `ValidatesPullRequest=false`; they are never a partial green.
Empty inventories, changed constants, changed conditional selection and input
identity mismatches also fail qualification. Exit 3 means not qualified;
malformed input/tool execution failures remain nonzero.

The driver retains evaluated MSBuild JSON, exact command, stderr/stdout logs,
source/reflection inventories, binary evidence, parity and aggregate result in a
new uniquely owned output directory. It never overwrites caller evidence.

## Focused local validation

Use the shared Flow admission wrapper locally; never bypass an active reservation.
Only the CI tool itself is built:

```bash
PATH=/home/mike/honua-io/honua-flow-work/tools/lane-bin:/home/mike/.dotnet:$PATH \
NUGET_PACKAGES=/home/mike/.local/share/honua-supervisor/cache/nuget \
dotnet build eng/ci/Honua.Ci.SourceInventory/Honua.Ci.SourceInventory.csproj \
  --configuration Release -p:TreatWarningsAsErrors=true -p:UseSharedCompilation=false

dotnet eng/ci/Honua.Ci.SourceInventory/bin/Release/net10.0/Honua.Ci.SourceInventory.dll self-test
python3 scripts/ci/source-inventory-pilot/test_run.py
```

The self-test compares independent semantic and compiled-reflection readers on
aliases, inherited attributes and multi-level overrides, partial classes,
overloads, generic method signatures, conditional compilation, constructor
constant coverage, custom executable attributes and unresolved symbols. Mutation
cases prove changed coverage and conditional discovery cannot qualify.

## Local pilot observation limitation

The focused CI-tool build passed Release warnings-as-errors with zero warnings
and errors; all eleven executed synthetic semantic/reflection assertions and
four offline driver fixtures passed. The local design-time driver smoke did
**not** qualify source inventory: the shared Flow admission parser returned exit
75 for an unsupported read-only MSBuild inventory option before starting the SDK.
No wrapper bypass, admission change, inspected compilation or production build
was performed. Standard hosted `dotnet msbuild` supports the required
`-getItem`/`-getProperty` API; the optional hosted observation must provide actual
qualification evidence. Local fixture success is not that evidence.

## Optional workflow insertion (no cutover)

Insert these steps **after existing normal test outputs, projection regeneration
and the complete collector have run** in the same checkout/job. Do not add
another solution/test-project build. If a consumer has only restored test outputs
without reference metadata, an honest not-qualified result is expected; do not
build reference projects solely to manufacture pilot parity.

```yaml
- name: Build CI source-inventory observation tool
  run: >-
    dotnet build eng/ci/Honua.Ci.SourceInventory/Honua.Ci.SourceInventory.csproj
    --configuration Release -p:TreatWarningsAsErrors=true -p:UseSharedCompilation=false
- name: Validate inventory fixture semantics
  run: |
    dotnet eng/ci/Honua.Ci.SourceInventory/bin/Release/net10.0/Honua.Ci.SourceInventory.dll self-test
    python3 scripts/ci/source-inventory-pilot/test_run.py
- name: Observe semantic inventory parity (not an admission replacement)
  continue-on-error: true
  run: >-
    timeout 15m python3 scripts/ci/source-inventory-pilot/run.py
    --tool eng/ci/Honua.Ci.SourceInventory/bin/Release/net10.0/Honua.Ci.SourceInventory.dll
- name: Retain semantic inventory qualification evidence
  if: always()
  uses: actions/upload-artifact@v7
  with:
    name: source-inventory-pilot-${{ github.run_attempt }}
    path: tests/TestResults/source-inventory-pilot/
    if-no-files-found: warn
```

The observation alone is advisory. Fixture failure is a real tool regression.
No existing required check, collector assembly reference, runtime trait test,
NativeAOT validation or production reflection policy is removed or weakened.
