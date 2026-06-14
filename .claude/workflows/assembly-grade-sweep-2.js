export const meta = {
  name: 'assembly-grade-sweep-2',
  description: 'Finish remaining Honua assemblies: style/maintainability/performance review + fixes, build-only (no test runs)',
  phases: [
    { title: 'Review & Fix' },
    { title: 'Solution Verify' },
    { title: 'Repair' },
  ],
}

// Remaining projects only (the first sweep finished the other 36).
const SRC = [
  'src/Honua.Protocols.Ogc.Shared/Honua.Protocols.Ogc.Shared.csproj',
  'src/Honua.AppHost/Honua.AppHost.csproj',
]
const TESTS = [
  'tests/dotnet/Honua.TestKit/Honua.TestKit.csproj',
  'tests/dotnet/Honua.Core.Security.Tests/Honua.Core.Security.Tests.csproj',
  'tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj',
  'tests/dotnet/Honua.Postgres.Security.Tests/Honua.Postgres.Security.Tests.csproj',
  'tests/dotnet/Honua.MySql.Tests/Honua.MySql.Tests.csproj',
  'tests/dotnet/Honua.Oracle.Tests/Honua.Oracle.Tests.csproj',
  'tests/dotnet/Honua.SqlServer.Tests/Honua.SqlServer.Tests.csproj',
  'tests/dotnet/Honua.DuckDB.Tests/Honua.DuckDB.Tests.csproj',
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

const FOCUS = `
PRIMARY LENS — prioritize STYLE, MAINTAINABILITY, and PERFORMANCE issues:
- Maintainability: dead/duplicated code, oversized methods/classes, deep nesting, unclear
  names, magic numbers, copy-pasted logic that should be a shared helper, leaky abstractions,
  inconsistent patterns vs the rest of the codebase, missing/incorrect XML docs on public types.
- Performance: sync-over-async (.Result/.Wait()/.GetAwaiter().GetResult()), needless
  allocations in hot paths, repeated parsing/serialization, LINQ in tight loops, unbounded
  buffering, N+1 / per-item catalog lookups, missing streaming/paging, blocking I/O on async
  paths, redundant ToList()/materialization, string concatenation in loops.
- Style: formatting, using-ordering, consistent nullability, expression-bodied where idiomatic,
  guard-clause early returns, file-scoped namespaces, modern C# idioms — but only where it
  matches the surrounding code's conventions.
ALSO enforce the architecture rubric: correct dependency direction; no controllers; infra
implementation types internal (only abstractions/DTOs/domain models public); shared infra reuse
for error-mapping/logging/telemetry/cache/RBAC/validation; protocol code adapts to shared
pipelines; endpoint ctor deps <=5, handler <=4. Grade A = zero blocking violations, minimal
warnings, clean build under TreatWarningsAsErrors, clean format.
For TEST projects: maintainability/style/perf of the test code + ADR-0011 attributes
([Protocol]/[Operation]/[Endpoint], [InterfaceOperation] where applicable), TestKit reuse,
no commented-out/skipped tests, naming MethodUnderTest_Scenario_ExpectedBehavior. Do NOT weaken
or delete assertions.
`

const GUARDRAILS = `
SAFETY GUARDRAILS (parallel run; many agents edit sibling projects at once):
- Edit ONLY files inside THIS project's directory.
- Do NOT rename / change namespace / change signature or accessibility of any PUBLIC type or
  member consumed by other projects — record such ideas under 'deferredCrossProject' instead.
- Do NOT edit Directory.Build.props, Directory.Packages.props, the .sln, or anything outside
  this project's directory.
- Do NOT weaken TreatWarningsAsErrors, add #pragma/NoWarn, or blanket-disable nullable. Fix root cause.
- Preserve behavior. No deleting features or assertions.
- *** DO NOT RUN THE TEST SUITE. Never call 'dotnet test'. Verify with 'dotnet build' ONLY. ***
- Author no commits.
`

const RESULT_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['project','kind','gradeBefore','gradeAfter','buildPass','formatPass','fixesApplied','deferredCrossProject','remainingIssues','summary'],
  properties: {
    project: { type: 'string' }, kind: { type: 'string', enum: ['src','test'] },
    gradeBefore: { type: 'string', enum: ['A','B','C','D','F'] },
    gradeAfter: { type: 'string', enum: ['A','B','C','D','F'] },
    buildPass: { type: 'boolean' }, formatPass: { type: 'boolean' },
    fixesApplied: { type: 'array', items: { type: 'string' } },
    deferredCrossProject: { type: 'array', items: { type: 'string' } },
    remainingIssues: { type: 'array', items: { type: 'string' } },
    summary: { type: 'string' },
  },
}

function prompt(proj, kind) {
  return `Review and fix the .NET assembly at: ${proj}
This is a ${kind === 'test' ? 'TEST' : 'PRODUCTION SOURCE'} project in honua-server (.NET 10, Nullable=enable, TreatWarningsAsErrors=true).
GOAL: bring this single project to grade A with minimal remaining issues, then prove it BUILDS clean.

${FOCUS}

${GUARDRAILS}

PROCESS:
1. Enumerate this project's source files and read its .csproj.
2. Grade as-is (gradeBefore). Identify concrete style/maintainability/performance + architecture issues with file:line.
3. Apply surgical, behavior-preserving fixes to reach A.
4. Format only this project: 'dotnet format ${proj}'.
5. Build ONLY this project (NEVER test): dotnet build ${proj} -c Release /p:TreatWarningsAsErrors=true
   Re-run until it builds clean.
6. Re-grade (gradeAfter); list anything still short of A under remainingIssues.
Return ONLY the structured result. buildPass=true only if step 5 succeeded; formatPass=true if format made no further changes.`
}

phase('Review & Fix')
const thunks = [
  ...SRC.map(p => () => agent(prompt(p,'src'), { label: `fix:${p.split('/').pop().replace('.csproj','')}`, phase: 'Review & Fix', schema: RESULT_SCHEMA })),
  ...TESTS.map(p => () => agent(prompt(p,'test'), { label: `fix:${p.split('/').pop().replace('.csproj','')}`, phase: 'Review & Fix', schema: RESULT_SCHEMA })),
]
const results = (await parallel(thunks)).filter(Boolean)
log(`Remaining-project pass complete: ${results.length}/${SRC.length+TESTS.length}. A-grade: ${results.filter(r=>r.gradeAfter==='A').length}`)

phase('Solution Verify')
const VERIFY_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['buildPass','formatPass','errors'],
  properties: {
    buildPass: { type: 'boolean' }, formatPass: { type: 'boolean' },
    errors: { type: 'array', items: { type: 'object', additionalProperties: false,
      required: ['file','project','message'],
      properties: { file: { type: 'string' }, project: { type: 'string' }, message: { type: 'string' } } } },
  },
}
const verifyPrompt = `Run the honua-server build + format gate and report as structured data. From repo root:
  1. dotnet build Honua.sln -c Release /p:TreatWarningsAsErrors=true
  2. dotnet format Honua.sln --verify-no-changes
Do NOT run any tests. Capture every compiler error/warning-as-error (file, owning project, message) into 'errors'.
buildPass = build exited 0; formatPass = format-verify exited 0. Report only; fix nothing. Return ONLY the structured result.`
let verify = await agent(verifyPrompt, { label: 'solution-verify', phase: 'Solution Verify', schema: VERIFY_SCHEMA })

phase('Repair')
let attempt = 0
while (verify && (!verify.buildPass || !verify.formatPass) && attempt < 3) {
  attempt++
  if (!verify.buildPass) {
    const errs = (verify.errors || []).slice(0, 80)
    log(`Repair ${attempt}: ${errs.length} build errors.`)
    const byProj = {}
    for (const e of errs) (byProj[e.project] || (byProj[e.project] = [])).push(e)
    await parallel(Object.entries(byProj).map(([proj, list]) => () =>
      agent(`Full-solution Release build (TreatWarningsAsErrors=true) is failing in project "${proj}". Fix the ROOT CAUSE of each error — no warning suppression, no nullable-disable, no deleting behavior/assertions. If a parallel refactor changed a public contract this project consumed, restore/adapt correctly. Then verify with 'dotnet build -c Release /p:TreatWarningsAsErrors=true' on the affected project (do NOT run tests). Errors:\n${list.map(e => `- ${e.file}: ${e.message}`).join('\n')}`,
        { label: `repair:${proj}`, phase: 'Repair' })))
  } else if (!verify.formatPass) {
    log(`Repair ${attempt}: applying dotnet format across solution.`)
    await agent(`Run 'dotnet format Honua.sln' from repo root to apply formatting, then confirm 'dotnet format Honua.sln --verify-no-changes' passes. Do NOT run tests. Report what changed.`, { label: 'repair:format', phase: 'Repair' })
  }
  verify = await agent(verifyPrompt, { label: `re-verify-${attempt}`, phase: 'Repair', schema: VERIFY_SCHEMA })
}

return {
  totalProjects: SRC.length + TESTS.length,
  agentsReturned: results.length,
  buildPass: verify ? verify.buildPass : false,
  formatPass: verify ? verify.formatPass : false,
  repairAttempts: attempt,
  belowA: results.filter(r => r.gradeAfter !== 'A').map(r => ({ project: r.project, grade: r.gradeAfter, remaining: r.remainingIssues })),
  deferredCrossProject: results.flatMap(r => (r.deferredCrossProject || []).map(d => `${r.project}: ${d}`)),
  perProject: results.map(r => ({ project: r.project, kind: r.kind, before: r.gradeBefore, after: r.gradeAfter, build: r.buildPass, fixes: r.fixesApplied.length, summary: r.summary })),
}
