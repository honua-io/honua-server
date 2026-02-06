# CI Failure Monitoring and Resolution

## After PR Submission

### 1. Monitor CI Jobs in Order
CI runs in dependency order. Fix failures from top to bottom:

1. **Build & Format Check** (runs first)
   - Fastest feedback (~1-2 minutes)
   - Most common failure point

2. **Unit Tests** (after build passes)
   - Should pass if pre-PR validation was run

3. **Integration Tests** (parallel with unit tests)
   - May fail due to environment differences
   - Uses Testcontainers + PostGIS
   - Skipped when only docs/config changes are detected

4. **Architecture Tests** (parallel with other tests)
   - Enforces project rules

5. **Coverage Report** (after all tests)
   - Combines all test results
   - Enforces coverage thresholds

6. **LLM Architecture Review** (parallel with tests)
   - **AI-powered architectural analysis** using GPT-4
   - Checks dependency flow, API patterns, documentation
   - **Can BLOCK PRs** if finds critical violations
   - Posts detailed review comments automatically

7. **Architecture Gate** (runs only if blocking issues found)
   - **HARD BLOCKER** - prevents merge if LLM finds BLOCKING_ISSUES
   - Must resolve all critical violations before merge

8. **AOT Build Verification** (parallel with tests)
   - Ensures production readiness

## Common Failure Patterns and Solutions

### Build & Format Check Failures

#### Error: `TreatWarningsAsErrors=true`
```bash
# Fix warnings, don't ignore them
# Example: nullable reference warnings
string? value = GetValue(); // Add ? if nullable
```

#### Error: Format verification failed
```bash
# Run formatter locally
dotnet format Honua.sln
git add .
git commit -m "style: fix code formatting"
```

#### Error: Instruction sync failed
```bash
# Files out of sync
cp CLAUDE.md CODEX.md
git add CODEX.md
git commit -m "docs: sync instruction files"
```

### Test Failures

#### Unit Tests
```bash
# Run locally to debug
dotnet test --filter "Category!=Integration" --verbosity normal
```

#### Integration Tests
```bash
# Common issue: Docker not available
# Ensure Docker Desktop is running locally
docker --version

# Run integration tests locally
dotnet test --filter "Category=Integration"
```

#### Architecture Tests
```bash
# Common violations:
# - Using controllers instead of minimal APIs
# - Too many dependencies
# - Missing endpoint tests

dotnet test --filter "Category=Architecture"
```

### Coverage Failures

#### Line Coverage Below Threshold
```bash
# Current: 40% line / 30% branch, target: 80%/70%
# Add tests for uncovered code paths

dotnet test --collect:"XPlat Code Coverage"
# Use coverage report to identify gaps
```

### LLM Review Blocking Issues

#### `BLOCKING_ISSUES` found
- Check PR comments for specific violations
- Common issues:
  - Controller usage (use minimal APIs)
  - Missing tests for new endpoints
  - AOT incompatible patterns
  - Missing documentation

### LLM Architecture Review Failures

#### `BLOCKING_ISSUES` Assessment
**Most Common Blocking Issues:**
1. **PR not linked to GitHub issue**
   ```bash
   # Fix: Edit PR description to include:
   Fixes #123
   # or
   Closes #456
   ```

2. **Missing acceptance criteria in linked issue**
   ```bash
   # Fix: Edit the GitHub issue to add:
   ## Acceptance Criteria
   - [ ] Endpoint returns correct HTTP status codes
   - [ ] Input validation works properly
   - [ ] Integration tests added
   ```

3. **Dependency direction violations**
   ```csharp
   // ❌ WRONG: Core depending on Infrastructure
   using Honua.Postgres; // in Honua.Core project

   // ✅ CORRECT: Infrastructure depending on Core
   using Honua.Core; // in Honua.Postgres project
   ```

4. **Controller usage (should use Minimal APIs)**
   ```csharp
   // ❌ WRONG: Controller pattern
   public class FeaturesController : ControllerBase { }

   // ✅ CORRECT: Minimal API pattern
   app.MapGet("/features", async (IFeatureService service) => { });
   ```

5. **Public database/repository types**
   ```csharp
   // ❌ WRONG: Public repository
   public class FeatureRepository { }

   // ✅ CORRECT: Internal repository
   internal class FeatureRepository { }
   ```

#### `NEEDS_ATTENTION` Assessment
- Review suggested improvements
- Consider implementing before merge
- Not blocking but should be addressed

#### Monitoring LLM Review
1. **Check PR comments** - LLM posts detailed architectural analysis
2. **Read educational notes** - Explains WHY patterns matter
3. **Follow recommendations** - Specific code improvements suggested
4. **Re-run after fixes** - Push new commits to trigger re-review

### AOT Build Failures

#### Reflection Usage
```bash
# Replace reflection with source generation
# Example: JSON serialization
[JsonSerializable(typeof(MyClass))]
public partial class MyJsonContext : JsonSerializerContext { }
```

## CI Monitoring Workflow

### 1. Immediate After PR Creation
- [ ] Watch GitHub Actions tab for initial build
- [ ] Fix any build/format failures within 10 minutes

### 2. Test Phase Monitoring (5-15 minutes)
- [ ] Check unit tests pass
- [ ] Monitor integration tests (may take longer)
- [ ] Review architecture test results

### 3. Review Phase (5-30 minutes)
- [ ] Read LLM architecture review comments
- [ ] Address any BLOCKING_ISSUES immediately
- [ ] Respond to reviewer feedback

### 4. Final Validation
- [ ] Ensure all CI checks are green
- [ ] Verify coverage meets thresholds
- [ ] Confirm AOT build succeeds

## Emergency Procedures

### Critical CI Failure
1. **DON'T** force push or retry without understanding
2. **DO** investigate root cause first
3. **DO** create new commits with fixes
4. **DO** test fixes locally before pushing

### Rollback Procedures
```bash
# If changes break trunk after merge
git revert <commit-hash>
# Or create hotfix PR immediately
```

### Escalation Path
1. Check existing GitHub issues for similar problems
2. Create new issue with CI logs if new problem
3. Tag relevant team members for urgent fixes

## Continuous Improvement

### Update This Document
When new failure patterns emerge:
1. Add to "Common Failure Patterns" section
2. Update pre-PR checklist if preventable
3. Consider adding to architecture tests if architectural

### Script Improvements
```bash
# Enhance pre-PR script based on failures
# Add new validation steps as needed
scripts/pre-pr-check.sh
```

## Tools and Resources

### Local Coverage Analysis
```bash
# Generate detailed coverage report
dotnet tool install -g dotnet-reportgenerator-globaltool
dotnet test --collect:"XPlat Code Coverage"
reportgenerator -reports:"tests/TestResults/**/coverage.cobertura.xml" -targetdir:"coverage-report"
```

### GitHub CLI for CI Monitoring
```bash
# Install GitHub CLI
gh auth login
gh pr checks    # View PR check status
gh pr view      # View PR details and comments
```

### Docker Management
```bash
# Clean up Docker if integration tests fail
docker system prune -f
docker volume prune -f
```
