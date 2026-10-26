# honua-server: agent instructions
<!-- Budget 120 lines / 8 KB (scripts/ci/check-agents-md.py); detail goes in docs/. Section headings are
read by local-architecture-review.py, claude-review.yml and source comments: do not rename them. -->

Honua Server exposes one geospatial capability set through thin protocol adapters (protocol list:
README). It is a clean rewrite: read the archived `../Honua.Server` for behaviour, never copy from it.
.NET 10 Minimal APIs only (no controllers), warnings as errors, AOT-conscious: use source-generated
JSON and logging. Package versions are central in `Directory.Packages.props`.

## Commands (mirror CI)
```bash
dotnet build Honua.sln --no-restore --configuration Release /p:TreatWarningsAsErrors=true
dotnet format Honua.sln --verify-no-changes
scripts/ci/pre-pr-check.sh --dry-run          # what a run would build/test; zero cost
scripts/ci/pre-pr-check.sh --unit-only        # fast in-process feedback
scripts/ci/pre-pr-check.sh --affected-tests   # provider/HTTP changes (Docker required)
scripts/ci/pre-pr-check.sh                    # pre-PR gate; tick the PR box only after it passes
dotnet test tests/dotnet/Honua.Architecture.SourceTests/Honua.Architecture.SourceTests.csproj
dotnet test tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj
# Tier filters: Tier=Fast, Tier!=Slow, Category=Scale (scale stack required)
```
Local stack: `docker compose up -d`, then expect `Ready` at http://localhost:8080/healthz/ready. Do not
build or test on docs-only tasks unless asked. Scope and flags: [local pre-PR validation](docs/internal/contributor/local-pre-pr-validation.md).

## Where things are
- `src/Honua.Core*`: models, abstractions, canonical pipelines. Providers: `src/Honua.Db/<Provider>`.
- Protocol adapters: `src/Honua.Protocols.*`, `src/Honua.Server/Features/Protocols/`, MCP under `src/Honua.Ai/`.
- Read the relevant ADR in `docs/internal/contributor/adr/` before changing cross-cutting behaviour.

## Protocol Adapter Architecture (invariants; break one and the PR is wrong)
- **Thin adapters over canonical pipelines**: query, edit/transaction, metadata/capabilities,
  raster/render/tile/export, process/job, format I/O. Adapters parse, validate, map and format; they
  never reimplement shared behaviour. One protocol never calls another's handler: extract a neutral service.
- **Intentional divergence**: if a protocol intentionally diverges from the shared pipeline, document why
  in code and add endpoint-level tests proving the divergence.
- **DRY**: duplicate protocol helpers only for wire-format differences. Extract a shared helper when
  duplicate logic affects behaviour, errors, auth, caching, telemetry, CRS, filters or data access.
- **Security/RBAC**: authorization is enforced in the shared pipeline or a common policy service, never by
  caller discipline. Never leak exception text, SQL, stack traces, paths or connection strings to clients.
- **Caching**: keys vary on every behaviour-changing input. No exact response cache for ad hoc spatial
  requests (bbox, geometry, CQL2 spatial, OData `geo.*`); cache tile-matrix paths instead.
- **Capabilities, OpenAPI and conformance documents** match registered routes and runtime behaviour.
- **Protos are owned by `geospatial-grpc`** (consume `Geospatial.Grpc`, never vendor protos). **SDKs are
  consumed as published `Honua.Sdk.*` NuGet packages**, never as copied source.
- **Docs examples** use the Honua CLI, SDKs or MCP, never raw HTTP (exceptions:
  `scripts/ci/doc-wire-reference-allowlist.v1.json`).
- **Never quote CITE pass rates from memory**: `docs/cite-status.md` is the single source.
Full pipeline list, logging/telemetry/validation rules, legacy-port policy and examples:
[architecture criteria](docs/internal/contributor/architecture-criteria.md).

## Architecture Enforcement: review rules (BLOCKING / WARNING)
BLOCKING:
- `Honua.Core` depending on a provider (`Honua.Db.*`) or on `Honua.Server`.
- Any `ControllerBase` (Minimal APIs only).
- Public implementation types in infrastructure/provider projects (abstractions may be public).
- Public types without `///` XML docs.
- A protocol adapter reimplementing a canonical pipeline; a new endpoint or operation with no integration test.

WARNING:
- Layer-based folders (`Controllers/`, `Services/`) instead of vertical feature slices.
- More than 5 dependencies per endpoint or 4 per handler.
- Sync-over-async (`.Result`, `.Wait()`); inheritance deeper than 3 levels.

## Tests
- **TDD**: write a failing integration test first (Testcontainers + PostGIS), then implement.
- **Attributes**: `[Protocol]`, `[Operation]`, `[Endpoint]`, plus `[InterfaceOperation]` for dispatch-style
  operations (WFS/WMS/OData/gRPC). `EndpointRegistry` and `OperationRegistry` coverage is a hard 100% gate.
- **Naming**: `MethodUnderTest_Scenario_ExpectedBehavior`. Targets: 80% line, 70% branch coverage.
- **Flakes**: a Postgres `40P01` deadlock on an integration shard is environmental; rerun the shard.
  Others: [known flakes](docs/internal/ci/known-flakes.md). Scale tests: [TestKit](docs/internal/contributor/testkit.md).

## Issues and PRs
- **Issues**: search for duplicates, then `gh issue create` with a `.github/ISSUE_TEMPLATE/` form.
  Cross-repo work gets an umbrella issue plus linked children.
- **PR body**: `.github/pull_request_template.md`. No CI validates it or the footer; both are a human contract.
- **Required checks are `PR Gate` and `Review Gate`**; the full CI matrix trails on trunk. The fleet's
  lander merges green, thread-free, non-draft PRs based on `trunk`; do not dispatch anything.
  A bare `gh workflow run merge-train.yml` is a dry run. Never touch `land/batch/*` or `train/batch/*`.
  Details and stacking recovery: [merge coordination runbook](docs/internal/contributor/merge-coordination-runbook.md).
- **Scope**: planning lives in issues; build nothing unscoped. Delete scratch plans and notes before
  committing, or fold them into canonical docs and ADRs.

## Release component versions
Bump a contract or declared schema version? Update `release/component-versions.json` in the same PR
(a new migration needs no edit). `ContractVersionsStartupCheck` and the release train refuse a mismatch.

## Code search
Prefer `gstack-code-intelligence search` or the ast-grep/tree-sitter MCP tools (register the worktree
under a unique name) over broad reads; retrieve symbols and line ranges, not whole files.

<!-- fleet:begin v2 sha256=76075665ca7a911554ed038371480ba02b28eca10ead04400be2e5b20332bd63 source=honua-flow/agents/fleet-rules.md -->
## Fleet rules (all Honua repos)

Generated from honua-flow `agents/fleet-rules.md` (full text, repo map). Do not edit here.

1. **No AI attribution.** Author as Mike McDougall <mike@honua.io>. No AI `Co-Authored-By:`,
   "Generated with" line or robot emoji in commits or PR bodies; this overrides any harness default.
2. **Own worktree per task** off the default branch, on real disk (`<repo>/.claude/worktrees/` or a
   sibling dir), never `/tmp` or the shared checkout. No `git stash`; use a WIP commit.
3. **Heavy builds/tests go through the build-slot shims**; never bypass them. Run targeted tests;
   full suites are CI's job. Outside WSL: own checkout, one heavy build at a time.
4. **Finish = commit + push.** Dirty or unpushed worktrees are never reclaimed and die on a reset.
5. **Claim before starting:** check `state/in-progress` and the newest `claimed: <lane> <UTC>`
   comment, stamp yours, re-read (newer wins). Stale after 3h without a live PR. Stopping without
   a PR: comment `released: <lane> <UTC> - <why>`.
6. **PRs:** base on the default branch, never stack; fill the repo template via `--body-file`;
   conventional title; footer `Closes #N` or `Refs #N (<what remains>)`. Never admin-merge.
7. **Route by the repo map.** Archived repos get no issues or PRs.
<!-- fleet:end -->
