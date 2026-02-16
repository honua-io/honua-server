# Contributing

Code style, architecture rules, and PR process for Honua Server. For dev environment setup, see [Getting Started](getting-started.md).

---

## Architecture Rules

These are enforced by architecture tests — PRs will fail if violated.

- **Dependency direction**: `Server` → `Postgres` → `Core` (never reversed)
- **Vertical slices**: features organized by business capability under `Features/`
- **Minimal APIs only**: no controllers (no `ControllerBase`)
- **Max 5 dependencies per endpoint, max 4 per handler**
- **AOT compatible**: source-generated JSON, `[LoggerMessage]` for logging, no reflection

---

## Code Style

```bash
# Always run before committing
dotnet format Honua.sln
```

- `TreatWarningsAsErrors=true` — all builds must pass without warnings
- Private fields: `_camelCase` with underscore prefix
- Async methods: suffix with `Async`
- New public APIs should include XML documentation

---

## Pre-PR Validation

Run these locally before creating a PR:

```bash
# 1. Instruction sync
bash scripts/check-instructions-sync.sh

# 2. Build
dotnet build Honua.sln --configuration Release /p:TreatWarningsAsErrors=true

# 3. Format
dotnet format Honua.sln --verify-no-changes

# 4. Tests
dotnet test Honua.sln --configuration Release

# 5. Architecture tests
dotnet test tests/Honua.Architecture.Tests/

# 6. AOT build
dotnet publish src/Honua.Server --configuration Release -p:PublishAot=true -p:StripSymbols=true -o ./publish
```

---

## Test Requirements

- **API Surface**: 100% — every endpoint must have an integration test
- **Line/Branch Coverage**: informational only (not CI-blocking)
- Use `[IntegrationTest]`, `[Protocol(...)]`, `[Endpoint(...)]` attributes
- Integration tests use Testcontainers (no external database needed)

---

## Commit Format

```
<type>(<scope>): <description>

feat: add spatial filtering for feature queries
fix(import): handle malformed shapefiles gracefully
test: add integration tests for import service
docs: update API examples for OData endpoints
```

---

## PR Process

1. Branch from `trunk`: `feature/<description>` or `fix/<description>`
2. Link to a GitHub issue: `Fixes #<number>` or `Closes #<number>`
3. Ensure all CI jobs pass (build, format, tests, architecture, AOT, LLM review)
4. LLM Architecture Review runs automatically and **can block PRs** if it finds critical violations

---

## Common CI Failure Reasons

1. **Format check fails**: run `dotnet format Honua.sln`
2. **Warnings as errors**: fix all compiler warnings
3. **Missing tests**: add tests for new endpoints (100% API coverage required)
4. **Architecture violations**: follow vertical slice pattern
5. **AOT incompatibility**: avoid reflection in hot paths
