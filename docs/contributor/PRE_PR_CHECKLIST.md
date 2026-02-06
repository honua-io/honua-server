# Pre-PR Validation Checklist

Run these commands locally BEFORE creating a PR to match CI requirements exactly.

## Required Local Validation Commands

### 1. Instruction Sync Check
```bash
bash scripts/check-instructions-sync.sh
```
**Purpose**: Ensures CLAUDE.md and CODEX.md are synchronized

### 2. Build Check (with warnings as errors)
```bash
dotnet restore Honua.sln
dotnet build Honua.sln --no-restore --configuration Release /p:TreatWarningsAsErrors=true
```
**Purpose**: Catches all warnings that would fail CI

### 3. Format Check
```bash
dotnet format Honua.sln --verify-no-changes --verbosity diagnostic
```
**Purpose**: Ensures code formatting meets standards
**Fix**: Run `dotnet format Honua.sln` if this fails

### 4. Unit Tests
```bash
dotnet test Honua.sln \
  --no-restore \
  --configuration Release \
  --filter "Category!=Integration" \
  --logger "trx;LogFileName=unit-results.trx" \
  --collect:"XPlat Code Coverage" \
  --results-directory ./tests/TestResults
```

### 5. Integration Tests
```bash
dotnet test Honua.sln \
  --no-restore \
  --configuration Release \
  --filter "Category=Integration" \
  --logger "trx;LogFileName=integration-results.trx" \
  --collect:"XPlat Code Coverage" \
  --results-directory ./tests/TestResults
```

### 6. Architecture Tests
```bash
dotnet test Honua.sln \
  --no-restore \
  --configuration Release \
  --filter "Category=Architecture" \
  --logger "trx;LogFileName=architecture-results.trx" \
  --results-directory ./tests/TestResults
```

### 7. AOT Build Verification
```bash
cd src/Honua.Server
dotnet publish \
  --configuration Release \
  -p:PublishAot=true \
  -p:StripSymbols=true \
  -o ./publish
```

### 8. Local Claude Architecture Review
```bash
# Run Claude-based architecture review locally
python scripts/claude-architecture-review.py
```
**Purpose**: Catch architectural violations before CI
**Note**: No API keys required - pure local analysis
**CI Note**: OpenAI review will also run automatically in CI

## Quick Validation Script

Save this as `scripts/pre-pr-check.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail

echo "🔍 Running pre-PR validation..."

echo "1. Checking instruction sync..."
bash scripts/check-instructions-sync.sh

echo "2. Restoring packages..."
dotnet restore Honua.sln

echo "3. Building with warnings as errors..."
dotnet build Honua.sln --no-restore --configuration Release /p:TreatWarningsAsErrors=true

echo "4. Checking code format..."
dotnet format Honua.sln --verify-no-changes --verbosity diagnostic

echo "5. Running unit tests..."
dotnet test Honua.sln --no-restore --configuration Release --filter "Category!=Integration"

echo "6. Running integration tests..."
dotnet test Honua.sln --no-restore --configuration Release --filter "Category=Integration"

echo "7. Running architecture tests..."
dotnet test Honua.sln --no-restore --configuration Release --filter "Category=Architecture"

echo "8. Testing AOT build..."
cd src/Honua.Server
dotnet publish --configuration Release -p:PublishAot=true -p:StripSymbols=true -o ./publish
cd ../..

echo "✅ All pre-PR checks passed! Ready to create PR."
```

## PR Requirements

### Commit Message Format
```
<type>: <description> (#<issue-number>)

Examples:
feat: add spatial query support (#7)
fix: resolve paging issue in query endpoint (#8)
test: add unit tests for Feature domain model (#9)
```

### PR Title Format
```
<type>: <description> (#<issue-number>)

Must match the main commit and include GitHub issue number
```

### PR Description Must Include
- Link to GitHub issue: `Fixes #<number>` or `Closes #<number>`
- Brief description of changes
- Testing notes
- Breaking changes (if any)

## Coverage Thresholds
- **Line Coverage**: Currently 40% (will increase to 80%)
- **Branch Coverage**: Currently 30% (will increase to 70%)

## Common Failure Reasons

1. **Format Check Fails**: Run `dotnet format Honua.sln` before committing
2. **Warnings as Errors**: Fix all compiler warnings
3. **Missing Tests**: Add tests for new endpoints (100% API coverage required)
4. **Architecture Violations**: Follow vertical slice pattern, avoid controllers
5. **Missing Issue Link**: PR must reference a GitHub issue
6. **AOT Incompatibility**: Avoid reflection in hot paths

## CI Monitoring After PR

After creating PR, monitor these CI jobs:
1. **Build & Format Check** - First to fail, easiest to fix
2. **Unit Tests** - Should pass if local validation passed
3. **Integration Tests** - May fail due to environment differences
4. **Architecture Tests** - Enforces project rules
5. **Coverage Report** - Must meet thresholds
6. **LLM Architecture Review** - AI-powered code review (GPT-4)
   - **Can BLOCK PR** if finds critical violations
   - Posts detailed review comments automatically
   - Checks dependency flow, API patterns, documentation
7. **Architecture Gate** - Hard blocker if LLM finds issues
8. **AOT Build Verification** - Ensures production readiness

## Emergency Fixes

If CI fails after PR creation:
1. **DON'T** force push to the same branch
2. **DO** create new commits with fixes
3. **DO** investigate root cause before retrying
4. **DO** update this checklist if new failure patterns emerge
