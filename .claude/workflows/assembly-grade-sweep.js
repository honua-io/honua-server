export const meta = {
  name: 'assembly-grade-sweep',
  description: 'Review every Honua assembly against the architecture rubric and fix until each earns an A grade',
  phases: [
    { title: 'Review & Fix' },
    { title: 'Solution Verify' },
    { title: 'Repair' },
  ],
}

// ---- Project inventory ---------------------------------------------------
const SRC = [
  'src/Honua.Core.Abstractions/Honua.Core.Abstractions.csproj',
  'src/Honua.Geometry/Honua.Geometry.csproj',
  'src/Honua.ServiceDefaults/Honua.ServiceDefaults.csproj',
  'src/Honua.Core/Honua.Core.csproj',
  'src/Honua.Postgres.Shared/Honua.Postgres.Shared.csproj',
  'src/Honua.Postgres/Honua.Postgres.csproj',
  'src/Honua.MySql/Honua.MySql.csproj',
  'src/Honua.Oracle/Honua.Oracle.csproj',
  'src/Honua.SqlServer/Honua.SqlServer.csproj',
  'src/Honua.DuckDB/Honua.DuckDB.csproj',
  'src/Honua.Io/Honua.Io.csproj',
  'src/Honua.Import/Honua.Import.csproj',
  'src/Honua.Jobs/Honua.Jobs.csproj',
  'src/Honua.Geocoding/Honua.Geocoding.csproj',
  'src/Honua.Geoprocessing/Honua.Geoprocessing.csproj',
  'src/Honua.Routing/Honua.Routing.csproj',
  'src/Honua.Scene/Honua.Scene.csproj',
  'src/Honua.Ai/Honua.Ai.csproj',
  'src/Honua.Aws/Honua.Aws.csproj',
  'src/Honua.Azure/Honua.Azure.csproj',
  'src/Honua.ArcGisRest/Honua.ArcGisRest.csproj',
  'src/Honua.Hosting/Honua.Hosting.csproj',
  'src/Honua.Worker.Gdal/Honua.Worker.Gdal.csproj',
  'src/Honua.Protocols.Ogc.Shared/Honua.Protocols.Ogc.Shared.csproj',
  'src/Honua.Protocols.OgcApi/Honua.Protocols.OgcApi.csproj',
  'src/Honua.Protocols.OgcClassic/Honua.Protocols.OgcClassic.csproj',
  'src/Honua.Protocols.GeoServices/Honua.Protocols.GeoServices.csproj',
  'src/Honua.Protocols.OData/Honua.Protocols.OData.csproj',
  'src/Honua.Protocols.Stac/Honua.Protocols.Stac.csproj',
  'src/Honua.Protocols.Scene/Honua.Protocols.Scene.csproj',
  'src/Honua.Server/Honua.Server.csproj',
  'src/Honua.AppHost/Honua.AppHost.csproj',
  'benchmarks/Honua.Benchmarks/Honua.Benchmarks.csproj',
  'samples/Honua.StacOpsDemo/Honua.StacOpsDemo.csproj',
]
const TESTS = [
  'tests/dotnet/Honua.TestKit/Honua.TestKit.csproj',
  'tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj',
  'tests/dotnet/Honua.Core.Security.Tests/Honua.Core.Security.Tests.csproj',
  'tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj',
  'tests/dotnet/Honua.Postgres.Tests/Honua.Postgres.Tests.csproj',
  'tests/dotnet/Honua.Postgres.Security.Tests/Honua.Postgres.Security.Tests.csproj',
  'tests/dotnet/Honua.MySql.Tests/Honua.MySql.Tests.csproj',
  'tests/dotnet/Honua.Oracle.Tests/Honua.Oracle.Tests.csproj',
  'tests/dotnet/Honua.SqlServer.Tests/Honua.SqlServer.Tests.csproj',
  'tests/dotnet/Honua.DuckDB.Tests/Honua.DuckDB.Tests.csproj',
  'tests/dotnet/Honua.Ai.Tests/Honua.Ai.Tests.csproj',
  'tests/dotnet/Honua.ArcGisRest.Tests/Honua.ArcGisRest.Tests.csproj',
  'tests/dotnet/Honua.Worker.Gdal.Tests/Honua.Worker.Gdal.Tests.csproj',
  'tests/dotnet/Honua.Protocols.OgcApi.Tests/Honua.Protocols.OgcApi.Tests.csproj',
  'tests/dotnet/Honua.Protocols.OgcClassic.Tests/Honua.Protocols.OgcClassic.Tests.csproj',
  'tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj',
  'tests/dotnet/Honua.Protocols.OData.Tests/Honua.Protocols.OData.Tests.csproj',
  'tests/dotnet/Honua.Protocols.Stac.Tests/Honua.Protocols.Stac.Tests.csproj',
  'tests/dotnet/Honua.Protocols.Scene.Tests/Honua.Protocols.Scene.Tests.csproj',
  'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj',
  'tests/dotnet/Honua.LoadTests/Honua.LoadTests.csproj',
]

const RUBRIC = `
ARCHITECTURE GRADING RUBRIC (from honua-server CLAUDE.md). Grade A-F.
An "A" = ZERO blocking violations and minimal warnings; builds clean under
TreatWarningsAsErrors; dotnet format clean.

BLOCKING (each one drops grade below A):
1. Dependency direction. Flow: Honua.Core.Abstractions <- Honua.Core <- providers
   (Honua.Postgres/MySql/Oracle/SqlServer/DuckDB) <- protocol assemblies <- Honua.Server.
   Core must NOT 'using' any Infrastructure/Server/provider/protocol assembly.
   A provider must NOT depend on Server or another provider. Protocols must NOT
   depend on another protocol's internals (share via neutral helper).
2. No ASP.NET controllers (no ': ControllerBase'/': Controller'). Minimal APIs only.
3. Encapsulation: infrastructure implementation types must be 'internal'; only
   abstractions/DTOs/domain models are 'public'. Public infra types are blocking.
4. Every PUBLIC type and public member must have /// XML documentation.
5. Cross-cutting must reuse shared infra (problem/error mapping, structured logging,
   telemetry spans, cache helpers, RBAC pipeline, shared validators) rather than
   reimplement. Protocol code must adapt to shared query/edit/metadata/raster/process
   pipelines, not reimplement them. Never leak SQL/stack traces/paths/connection
   strings to clients.

WARNING (minimize for an A):
- Layer-based folders instead of vertical slices.
- Endpoint ctor deps > 5, handler ctor deps > 4.
- sync-over-async (.Result / .Wait() / .GetAwaiter().GetResult()).
- Inheritance depth > 3.
- Duplicated behavior that should be a shared helper.

TEST PROJECTS use a test-appropriate rubric instead: tests build clean under
warnings-as-errors and format clean; integration tests carry [Protocol]/[Operation]/
[Endpoint] (and [InterfaceOperation] where applicable) attributes per ADR-0011;
no controllers; no skipped/commented-out tests left behind; shared TestKit helpers
reused rather than copy-pasted; naming MethodUnderTest_Scenario_ExpectedBehavior.
Do NOT weaken or delete assertions to make tests pass.
`

const GUARDRAILS = `
SAFETY GUARDRAILS (parallel run — many agents edit sibling projects at once):
- You may refactor FREELY within files that belong to THIS project only.
- Do NOT rename, move the namespace of, or change the signature/accessibility of any
  PUBLIC type or member that other projects consume. Such a change would break sibling
  agents building in parallel. If such a change is warranted, record it under
  'deferredCrossProject' with a precise description INSTEAD of doing it.
- Before making a public type 'internal', grep the repo for external references; if any
  exist outside this project (including test projects / InternalsVisibleTo consumers),
  keep it public or defer.
- Do NOT edit Directory.Build.props, Directory.Packages.props, the .sln, or any file
  outside this project's directory.
- Do NOT weaken TreatWarningsAsErrors, suppress warnings with #pragma/NoWarn, or add
  blanket nullable-disable to dodge fixes. Fix the root cause.
- Preserve behavior. No deleting features or assertions to make things compile/pass.
- Author no commits. Leave changes in the working tree.
`

const RESULT_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  required: ['project', 'kind', 'gradeBefore', 'gradeAfter', 'buildPass', 'formatPass',
             'fixesApplied', 'deferredCrossProject', 'remainingIssues', 'summary'],
  properties: {
    project: { type: 'string' },
    kind: { type: 'string', enum: ['src', 'test'] },
    gradeBefore: { type: 'string', enum: ['A', 'B', 'C', 'D', 'F'] },
    gradeAfter: { type: 'string', enum: ['A', 'B', 'C', 'D', 'F'] },
    buildPass: { type: 'boolean' },
    formatPass: { type: 'boolean' },
    fixesApplied: { type: 'array', items: { type: 'string' } },
    deferredCrossProject: { type: 'array', items: { type: 'string' } },
    remainingIssues: { type: 'array', items: { type: 'string' } },
    summary: { type: 'string' },
  },
}

function reviewPrompt(proj, kind) {
  return `You are reviewing and fixing the .NET assembly at: ${proj}
This is a ${kind === 'test' ? 'TEST' : 'PRODUCTION SOURCE'} project in the honua-server repo (.NET 10, Nullable=enable, TreatWarningsAsErrors=true).

GOAL: bring this single project to an A grade with minimal remaining issues, then prove it builds clean.

${RUBRIC}

${GUARDRAILS}

PROCESS:
1. Enumerate the project's source files (everything under $(dirname ${proj})). Read the .csproj for its ProjectReferences and accessibility settings.
2. Grade the project as-is (gradeBefore) against the rubric. Identify concrete violations with file:line.
3. Apply AGGRESSIVE fixes within this project to reach A: add missing /// XML docs to public types/members; make leaked-public infra types internal (after the grep check); replace sync-over-async with await; reduce over-limit ctor deps via parameter objects/option records; split layer-folders into vertical slices where it is project-local and low-risk; route error/log/telemetry/cache/validation through shared infra; remove dead/duplicated code. For test projects, add missing [Protocol]/[Operation]/[Endpoint]/[InterfaceOperation] attributes and reuse TestKit helpers.
4. Run formatting on just this project: dotnet format <proj> --include $(the changed files) — or 'dotnet format ${proj}'. (dotnet is build-lock shimmed; just call it.)
5. Build ONLY this project to verify warnings-as-errors passes:
   dotnet build ${proj} -c Release /p:TreatWarningsAsErrors=true
   Fix everything until it builds clean. Re-run until green.
6. Re-grade (gradeAfter). If you could not reach A, list precisely why under remainingIssues.

Keep edits surgical and behavior-preserving. Return ONLY the structured result.
Set buildPass=true only if step 5 actually succeeded; formatPass=true only if format made no further changes.`
}

// ---- Phase 1: review + fix every project in parallel ---------------------
phase('Review & Fix')
const srcThunks = SRC.map(p => () =>
  agent(reviewPrompt(p, 'src'), { label: `fix:${p.split('/').pop().replace('.csproj','')}`, phase: 'Review & Fix', schema: RESULT_SCHEMA }))
const testThunks = TESTS.map(p => () =>
  agent(reviewPrompt(p, 'test'), { label: `fix:${p.split('/').pop().replace('.csproj','')}`, phase: 'Review & Fix', schema: RESULT_SCHEMA }))

const results = (await parallel([...srcThunks, ...testThunks])).filter(Boolean)
log(`Per-project pass complete: ${results.length}/${SRC.length + TESTS.length} agents returned.`)
const gotA = results.filter(r => r.gradeAfter === 'A').length
log(`${gotA}/${results.length} projects at grade A after first pass.`)

// ---- Phase 2: whole-solution build + format gate -------------------------
phase('Solution Verify')
const VERIFY_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['buildPass', 'formatPass', 'errors'],
  properties: {
    buildPass: { type: 'boolean' },
    formatPass: { type: 'boolean' },
    errors: { type: 'array', items: {
      type: 'object', additionalProperties: false,
      required: ['file', 'project', 'message'],
      properties: { file: { type: 'string' }, project: { type: 'string' }, message: { type: 'string' } },
    } },
  },
}
const verifyPrompt = `Run the full honua-server CI gate and report results as structured data.
Run, in order, from the repo root (dotnet is build-lock shimmed — just call it):
  1. dotnet build Honua.sln -c Release /p:TreatWarningsAsErrors=true
  2. dotnet format Honua.sln --verify-no-changes
Capture every compiler error/warning-as-error (file, owning project, message) into 'errors'.
buildPass = build step exited 0. formatPass = format verify exited 0 (no changes needed).
Do NOT fix anything; just report. Return ONLY the structured result.`

let verify = await agent(verifyPrompt, { label: 'solution-verify', phase: 'Solution Verify', schema: VERIFY_SCHEMA })

// ---- Phase 3: repair loop (sequential; parallel edits already serialized by build) ----
phase('Repair')
let attempt = 0
while (verify && !verify.buildPass && attempt < 3) {
  attempt++
  const errs = (verify.errors || []).slice(0, 80)
  log(`Repair attempt ${attempt}: ${errs.length} build errors to fix.`)
  // Group errors by project so one agent owns each broken project (disjoint files).
  const byProject = {}
  for (const e of errs) (byProject[e.project] || (byProject[e.project] = [])).push(e)
  const repairThunks = Object.entries(byProject).map(([proj, list]) => () =>
    agent(`The full-solution Release build (TreatWarningsAsErrors=true) is failing with errors in project "${proj}".
Fix the ROOT CAUSE of each — do NOT suppress warnings, weaken nullable, lower accessibility incorrectly, or delete behavior/assertions.
If a parallel refactor changed a public contract this project consumed, restore compatibility or adapt the consumer correctly.
Errors:
${list.map(e => `- ${e.file}: ${e.message}`).join('\n')}

After editing, verify with: dotnet build -c Release /p:TreatWarningsAsErrors=true on the affected project, then report what you changed.`,
      { label: `repair:${proj}`, phase: 'Repair' }))
  await parallel(repairThunks)
  verify = await agent(verifyPrompt, { label: `re-verify-${attempt}`, phase: 'Repair', schema: VERIFY_SCHEMA })
}

// ---- Final report --------------------------------------------------------
const finalGrades = {}
for (const r of results) finalGrades[r.project] = r.gradeAfter
return {
  totalProjects: SRC.length + TESTS.length,
  agentsReturned: results.length,
  buildPass: verify ? verify.buildPass : false,
  formatPass: verify ? verify.formatPass : false,
  repairAttempts: attempt,
  grades: finalGrades,
  belowA: results.filter(r => r.gradeAfter !== 'A').map(r => ({ project: r.project, grade: r.gradeAfter, remaining: r.remainingIssues })),
  deferredCrossProject: results.flatMap(r => (r.deferredCrossProject || []).map(d => `${r.project}: ${d}`)),
  perProject: results.map(r => ({ project: r.project, kind: r.kind, before: r.gradeBefore, after: r.gradeAfter, build: r.buildPass, fixes: r.fixesApplied.length, summary: r.summary })),
}
