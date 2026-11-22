# honua-server: agent instructions
<!-- Budget: scripts/ci/check-agents-md.py. Detail: docs/. Preserve review headings. -->

Honua Server exposes one geospatial capability set through thin protocol adapters (protocol list:
README). It is a clean rewrite: read the archived `../Honua.Server` for behaviour, never copy from it.
.NET 11 Minimal APIs only (no controllers), warnings as errors, AOT-conscious: use source-generated
JSON and logging. Package versions are central in `Directory.Packages.props`.

## Commands (mirror CI)
```bash
dotnet build Honua.sln --no-restore --configuration Release /p:TreatWarningsAsErrors=true
dotnet format Honua.sln --verify-no-changes
scripts/ci/pre-pr-check.sh --dry-run          # what a run would build/test; zero cost
scripts/ci/pre-pr-check.sh --unit-only        # fast in-process feedback
scripts/ci/pre-pr-check.sh --affected-tests   # units; --integration adds Docker tests
scripts/ci/pre-pr-check.sh                    # pre-PR gate; tick the PR box only after it passes
dotnet test tests/dotnet/Honua.Architecture.SourceTests/Honua.Architecture.SourceTests.csproj
dotnet test tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj
# Tier filters: Tier=Fast, Tier!=Slow, Category=Scale (scale stack required)
```
Local stack: `docker compose up -d`, then expect `Ready` at http://localhost:8080/healthz/ready. Do not
build or test on docs-only tasks unless asked. Scope and flags: [local pre-PR validation](docs/internal/contributor/local-pre-pr-validation.md).

## Where things are
- `src/Honua.Core/<Core|Abstractions>/`: models, abstractions, canonical pipelines. Providers: `src/Honua.Db/<Provider>`.
- Protocol adapters: `src/Honua.Protocols/<Family>`, `src/Honua.Server/Features/Protocols/`, MCP under `src/Honua.Ai/`.
- Read the relevant ADR in `docs/internal/contributor/adr/` before changing cross-cutting behaviour.

## Protocol Adapter Architecture (invariants; break one and the PR is wrong)
- **Thin adapters over canonical pipelines**: query, edit/transaction, metadata/capabilities,
  raster/render/tile/export, process/job, format I/O. Adapters parse, validate, map and format; they
  never reimplement shared behaviour. One protocol never calls another's handler: extract a neutral service.
- **Intentional divergence**: if a protocol intentionally diverges from the shared pipeline, document why
  in code and add endpoint-level tests proving the divergence.
- **DRY**: duplicate protocol helpers only for wire-format differences.
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
Full rules and examples: [architecture criteria](docs/internal/contributor/architecture-criteria.md).

## Review guidelines: architecture enforcement and Clean Code (BLOCKING / WARNING)
Reviews judge added or changed lines by *Clean Code* and cite smell codes ([rules](docs/internal/contributor/architecture-criteria.md#clean-code-robert-c-martin)).
The `Clean code` PR check enforces what a tool can; `quality-exception: <reason>` in the PR body is logged.

BLOCKING:
- `Honua.Core` depending on a provider (`Honua.Db.*`) or on `Honua.Server`.
- Any `ControllerBase` (Minimal APIs only).
- Public implementation types in infrastructure/provider projects (abstractions may be public).
- A protocol adapter reimplementing a canonical pipeline; a new endpoint or operation with no integration test.
- G5 duplication: search first (`gstack-code-intelligence search`); reuse or consolidate, never copy.
- C1 history, ticket/PR refs or reviewer notes in code; C2-C4 obsolete, redundant or rambling comments;
  C5 commented-out code. Comment only what the code cannot say.
- XML doc comments (`///`): names carry the meaning (SDK packages: one-line `<summary>` only).
- N1 names that hide intent; F1 more than 3 parameters; F2 output arguments; F3 boolean flags.
- Over budget, no reason in the PR: functions ~40 lines or 2 nesting levels; classes, files ~400 lines.

WARNING:
- Layer-based folders (`Controllers/`, `Services/`) instead of vertical feature slices.
- More than 5 dependencies per endpoint or 4 per handler.
- Sync-over-async (`.Result`, `.Wait()`); inheritance deeper than 3 levels.
- Other Clean Code smells (N2-N7, G6-G36, T1-T9), e.g. G25 magic numbers.

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
- **Required checks are `PR Gate` and `Review Gate`**; the full CI matrix trails on trunk. The fleet's
  lander merges green, thread-free, non-draft PRs based on `trunk`; do not dispatch anything.
  Never touch `land/batch/*` branches.
  Details and stacking recovery: [merge coordination runbook](docs/internal/contributor/merge-coordination-runbook.md).
- **Scope**: planning lives in issues; build nothing unscoped. Delete scratch plans and notes before
  committing, or fold them into canonical docs and ADRs.

## Release component versions
Bump a contract or declared schema version? Update `eng/release/component-versions.v1.json` in the same PR
(a new migration needs no edit). `ContractVersionsStartupCheck` and the release train refuse a mismatch.

<!-- fleet:begin v3 sha256=2c5c9896ef56430ddcee7221072ff5685b96d73f4c51561e4862a1a965218242 -->
## Fleet rules (all Honua repos)

Generated from the org fleet-rules source; do not edit this block. Start with org skill `honua-system-map`.

1. Author commits as the repository owner's configured git identity. No AI attribution in commits or PR bodies.
2. Use a separate worktree per task, off the default branch, on durable disk. Include current trunk before opening a PR. No shared-checkout edits or git stash; preserve work with commits.
3. Run .NET builds and tests only through the approved remote build-host shims. Never fall back to a local build. See org skill `honua-build-host`.
4. Commit and push completed work; keep existing branches, commits and worktrees until their retention rules permit cleanup.
5. Claim work before starting, reread the issue and current lane rulings, and release the claim after completion. See org skill `honua-dev-loop`.
6. Reserve PR capacity and bundle at least three issues, a plan row, or a permitted single-work reason. Current caps and exceptions come from current lane rulings.
7. Target the default branch with the repository's PR template and conventional title. Never force-push or use an admin merge.
8. Run pre-PR checks, obtain one independent review of the final head, and resolve every review thread. Merge on green required checks; arm auto-merge where enforced checks support it. See org skill `honua-pr-flow`.
9. Read failed required-check logs and fix in one push. For red trunk, fix forward or revert within the repository's current ruling deadline; see org skills `honua-red-pr` and `honua-trunk-red`.
10. Never commit secrets, deployment identifiers, private repository names or infrastructure details. Security findings belong in the private tracker. Archived repos get no issues or PRs; public instructions name org skills only.
<!-- fleet:end -->
