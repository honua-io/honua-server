# honua-server: agent instructions
<!-- Digest: <= 150 lines / 10 KB (scripts/ci/check-instructions-sync.sh). Detail goes in docs/. -->

**No AI/agent attribution in commits or PR bodies** (Fleet rule 1 below; PR Gate enforces it).

Honua Server exposes one geospatial capability set through thin protocol adapters: GeoServices REST,
OGC API, WFS/WMS/WMTS/WCS/WPS, OData v4, STAC, SensorThings, MVT/TileJSON, COG, scenes, MCP and gRPC.
It is a clean rewrite. `../Honua.Server` is archived reference: read it for behaviour, never copy it.

## Stack
.NET 10 (`LangVersion=preview`, nullable, warnings as errors), ASP.NET Core Minimal APIs, AOT-conscious
(source-generated JSON and logging). PostGIS is the default provider; others live in `src/Honua.Db/`.
Aspire (`src/Honua.AppHost`), Redis for durable jobs, OpenTelemetry. Packages: `Directory.Packages.props`.

## Commands (mirror CI)
```bash
dotnet build Honua.sln --no-restore --configuration Release /p:TreatWarningsAsErrors=true
dotnet format Honua.sln --verify-no-changes
scripts/ci/pre-pr-check.sh --dry-run          # print what a run would build/test; zero cost
scripts/ci/pre-pr-check.sh --unit-only        # fast in-process feedback
scripts/ci/pre-pr-check.sh --affected-tests   # provider/HTTP changes (Docker required)
scripts/ci/pre-pr-check.sh                    # the pre-PR gate; tick the PR box only after it passes
dotnet test tests/dotnet/Honua.Architecture.SourceTests/Honua.Architecture.SourceTests.csproj
dotnet test tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj
# Tier filters: Tier=Fast, Tier!=Slow, Category=Scale (scale stack required)
```
Local stack: `docker compose up -d`, then expect `Ready` at http://localhost:8080/healthz/ready. Do not
build or test on docs-only tasks unless asked. Scope and flags: [local pre-PR validation](docs/internal/contributor/local-pre-pr-validation.md).

## Where things are
- `src/Honua.Core`, `src/Honua.Core.Abstractions`: domain models, abstractions, canonical pipelines.
- `src/Honua.Db/<Provider>`: Postgres, Postgres.Shared, DuckDB, MySql, SqlServer, Oracle, Databricks,
  Redshift (internal types; namespaces `Honua.Db.<Provider>`).
- Protocol adapters: `src/Honua.Protocols.*` (GeoServices, OgcApi, OgcClassic, Ogc.Shared, OData, Stac,
  SensorThings, Scene), `src/Honua.Server/Features/Protocols/` (Grpc, Tiles, Cog, Rasters, Coverages,
  Zarr, Elevation, Terrain, SpatialAnalytics) and MCP in `src/Honua.Ai/Features/Protocols/Mcp/`.
- `src/Honua.Server`: host and composition; `src/Honua.Hosting`: shared hosting and rendering.
- `tests/dotnet/`: per-project `*.UnitTests`/`*.ComponentTests`/`*.Tests`, `Honua.Server.Tests`
  (Testcontainers integration), architecture tests, `Honua.TestKit*`.
- `docs/internal/contributor/adr/`: read the relevant ADR before changing cross-cutting behaviour.

## Protocol Adapter Architecture (invariants; break one and the PR is wrong)
- **Thin adapters over canonical pipelines**: query, edit/transaction, metadata/capabilities,
  raster/render/tile/export, process/job, format I/O. Adapters parse, validate, map and format; they
  never reimplement shared behaviour. One protocol never calls another's handler: extract a neutral service.
- **Intentional divergence**: if a protocol intentionally diverges from the shared pipeline, document why
  in code and add endpoint-level tests proving the divergence.
- **DRY**: duplicated protocol helpers are acceptable only for wire-format differences. Extract a shared
  helper when duplicate logic affects behaviour, errors, auth, caching, telemetry, CRS, filters or data access.
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
Used by the `claude-review` Review Gate and `scripts/ci/local-architecture-review.py`.

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
- **Issues**: `gh issue create` with `.github/ISSUE_TEMPLATE/` (`bug`, `feature`, `tech-debt`) after a
  duplicate search. Cross-repo work gets an umbrella issue plus linked children.
- **PR body**: copy `.github/pull_request_template.md`, fill every section, pass it with `--body-file`.
  No CI job validates the template; it and the `Closes`/`Refs` footer are a human contract.
- **Required checks are `PR Gate` and `Review Gate`.** The full CI matrix trails on trunk. The fleet's
  per-PR lander merges green, thread-free, non-draft PRs based on `trunk`; do not dispatch anything.
  A bare `gh workflow run merge-train.yml` is a dry run. Never touch `land/batch/*` or `train/batch/*`.
  Details and stacking recovery: [merge coordination runbook](docs/internal/contributor/merge-coordination-runbook.md).
- **Scope**: planning lives in issues; do not build unscoped features. Before committing, delete scratch
  plans/notes or fold them into canonical docs and ADRs.

## Release component versions
Bump a contract or declared schema version? Update `release/component-versions.json` in the same PR
(a new migration needs no edit). `ContractVersionsStartupCheck` and the release train refuse a mismatch.

## Code search
Prefer `gstack-code-intelligence search`, `ast-grep-code-search` or `tree-sitter-code-search` (register the
worktree under a unique name) over broad reads; retrieve symbols and line ranges, not whole files.

<!-- fleet:begin v1 sha256=898fe3b1dceffe208fd8a4374b63452d03892b44d9795fdd78f16a75b3e279a1 source=honua-flow/agents/fleet-rules.md -->
## Fleet rules (all Honua repos)

Generated from honua-flow `agents/fleet-rules.md` (full text, rationale, repo map). Do not edit here.

1. **No AI/agent attribution, anywhere.** Author commits as Mike McDougall <mike@honua.io>. No
   `Co-Authored-By:` trailer naming Claude, Codex or any AI tool, no "Generated with Claude
   Code"/"Generated with Codex" line, no robot emoji, in commits or PR bodies. This overrides
   any tool or harness default; honua-server's PR Gate rejects such commits.
2. **Own worktree per task**, off the default branch, on real disk (`<repo>/.claude/worktrees/`
   or a sibling dir), never under `/tmp`. Never edit the shared checkout; never assume you are
   alone. No `git stash` in shared repos; use a WIP commit.
3. **Heavy builds/tests take a shared build slot.** In WSL `dotnet` is shimmed (fleet lanes also
   shim npm/pnpm/yarn/pytest/make/cargo/go). Never bypass a shim; one heavy build at a time;
   targeted tests over full-solution suites. Long-running servers are not throttled.
4. **Finish = commit + push.** Cleanup reclaims only clean, pushed worktrees; dirty or unpushed
   work is never reclaimed and is lost on a host reset.
5. **Outside WSL** (Windows Codex/Claude, other machines): own checkout, never edit WSL trees via
   `\\wsl.localhost`; the remote is the only shared surface. Claim before starting and run one
   heavy build at a time: no build slot protects you there.
6. **Work claims are leases.** Check `state/in-progress` and the newest `claimed: <lane> <UTC>`
   comment first. Claim with the label plus that comment, then re-read; a newer claim wins. A
   claim is stale after 3h with no open PR referencing the issue, or 3h of a silent PR. Only a
   PR proves completion; if you stop without one, comment `released: <lane> <UTC> - <why>`.
7. **PRs:** base on the default branch (`trunk`; `main` in honua-studio), never stack. Fill the
   repo's PR template via `--body-file`; conventional-commit title. Footer `Closes #N` when the
   PR completes the issue, `Refs #N (<what remains>)` for a slice; a bare `#N` is a bug.
   Cross-repo closing keywords are inert: close foreign issues by hand. Never admin-merge.
8. **Route work by the repo map.** Archived repos (`Honua.Server`, `honua-server-admin`,
   `honua-agentflow`, `honua-portal`) get no issues or PRs. Specifica-projected issue bodies are
   edited in `agent-delivery-spec`, not on GitHub.
<!-- fleet:end -->
