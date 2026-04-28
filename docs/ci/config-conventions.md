# CI Configuration Conventions

> Cross-repo conventions for environment variables, secrets, caching, artifacts, and reusable workflow inputs.
> Last updated: 2026-03-18 (ticket #485)

## Toolchain Environment Variables

Use these standard names at workflow top-level `env:` blocks or as reusable workflow inputs:

| Variable | Standard value | Used by |
|---|---|---|
| `DOTNET_VERSION` | `'10.0.x'` | All .NET workflows |
| `DOTNET_SKIP_FIRST_TIME_EXPERIENCE` | `true` | All .NET workflows |
| `DOTNET_NOLOGO` | `true` | All .NET workflows |
| `PYTHON_VERSION` | `'3.11'` | Conformance and script workflows |
| `NODE_VERSION` | `'20'` | JS SDK and integration tests |

## Cache Keys

Follow the pattern: `${{ github.repository }}-${{ runner.os }}-${tool}-${hash}`

| Tool | Key pattern | Restore pattern |
|---|---|---|
| NuGet | `${repo}-${os}-nuget-${hashFiles('**/*.csproj', '**/packages.lock.json')}` | `${repo}-${os}-nuget-` |
| npm | `${repo}-${os}-npm-${hashFiles('**/package-lock.json')}` | `${repo}-${os}-npm-` |
| pip | `${repo}-${os}-pip-${hashFiles('**/requirements*.txt')}` | `${repo}-${os}-pip-` |
| Docker Buildx | `${repo}-${os}-buildx-${workflow}-${sha}` | `${repo}-${os}-buildx-${workflow}-`, `${repo}-${os}-buildx-` |

## Artifact Naming

Pattern: `${repo}-${workflow}-${run_id}-${kind}[-${suffix}]`

| Kind | Example | Description |
|---|---|---|
| `test-results` | `honua-server-ci-12345-test-results` | TRX, JUnit, or xUnit output |
| `coverage` | `honua-server-ci-12345-coverage` | Coverage reports (Cobertura, lcov) |
| `benchmark` | `honua-server-benchmarks-12345-benchmark` | BenchmarkDotNet JSON/HTML |
| `conformance` | `honua-server-cite-tiles-12345-conformance` | CITE/OGC conformance results |
| `sarif` | `honua-server-codeql-12345-sarif` | Security scan SARIF output |
| `evidence` | `honua-server-ci-12345-evidence` | Combined evidence package |

## Artifact Retention

| Tier | Retention (days) | Rationale |
|---|---|---|
| PR | 7 | Short-lived; only needed during review |
| nightly | 30 | Enough for trend analysis and debugging |
| release | 90 | Required for release certification audit |
| deploy | 90 | Required for deploy validation audit |

## Evidence Outputs

All evidence-producing workflows must:

1. Produce **machine-readable JSON** (not just human-readable markdown).
2. Write a **concise step summary** to `$GITHUB_STEP_SUMMARY`.
3. Use **stable file names** across repos (e.g., `ci-report.json`, `conformance-summary.json`).

## Secret Names

Use stable, tool-specific names across all repos:

| Secret | Purpose |
|---|---|
| `GITHUB_TOKEN` | GitHub Packages publish/restore in Actions |
| `DOCKERHUB_USERNAME` | Docker Hub authentication |
| `DOCKERHUB_TOKEN` | Docker Hub authentication |
| `DEPLOY_TOKEN` | Environment deployment |

## Environment Names

Use only these three environment names in deploy workflows:

| Environment | Purpose |
|---|---|
| `development` | Development/integration testing |
| `staging` | Pre-production validation |
| `production` | Production deployment |

## Reusable Workflow Inputs

When creating reusable workflows (`.github/workflows/reusable-*.yml`), use explicit inputs for:

| Input | Type | Description |
|---|---|---|
| `dotnet-version` | string | .NET SDK version |
| `node-version` | string | Node.js version |
| `python-version` | string | Python version |
| `gate-mode` | string | `quick` or `full` |
| `artifact-retention` | number | Days to retain artifacts |
| `test-command` | string | Repo-specific test command |
| `build-command` | string | Repo-specific build command |

## Composite Action Conventions

Composite actions live in `.github/actions/{action-name}/action.yml`:

| Action | Purpose |
|---|---|
| `setup-dotnet-ci` | .NET SDK setup, NuGet cache |
| `setup-node-ci` | Node setup, npm cache |
| `setup-python-ci` | Python setup, pip cache |
| `upload-ci-evidence` | Artifact upload with standard naming and retention |
| `run-conformance-stack` | Docker bootstrap/teardown for CITE workflows |
