# Coverage and Codecov

This project tracks coverage configuration in `codecov.yml`, but Codecov is currently informational only.

## Current State

- `codecov.yml` exists and defines ignore rules/flags/components.
- Component status checks are disabled (`component_management.default_rules.statuses: []`).
- CI does not currently upload coverage reports to Codecov.

## Coverage Targets

- Team target: 80% line coverage, 70% branch coverage.
- These are engineering goals; they are not enforced by Codecov checks today.

## Local Coverage

Use any of the following:

```bash
# Full local report (bash)
./scripts/coverage-local.sh

# Full local report (PowerShell)
./scripts/coverage-local.ps1

# Optional comprehensive coverage runner
./scripts/run-coverage.sh
```

Manual collector example:

```bash
dotnet test --collect:"XPlat Code Coverage"
```

## If You Re-enable Codecov Gating

1. Add coverage upload steps to CI workflow(s).
2. Enable status rules in `codecov.yml`.
3. Update this document and `docs/contributor/ci-workflows.md` in the same PR.
