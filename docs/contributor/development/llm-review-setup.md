# LLM Architecture Review Setup Guide

## Overview
This system provides automated architectural review using OpenAI GPT-4 for every pull request, focusing on Honua's specific architecture patterns and quality standards.

## Setup Instructions

### 1. Configure OpenAI API Key

Add your OpenAI API key to GitHub repository secrets:

1. Go to your repository on GitHub
2. Navigate to **Settings** → **Secrets and variables** → **Actions**
3. Click **New repository secret**
4. Name: `OPENAI_API_KEY`
5. Value: Your OpenAI API key (starts with `sk-`)

### 2. Enable Architecture Gate (Optional)

To automatically block PRs with blocking architectural issues:

Edit `.github/workflows/pr-architecture-review.yml`:

```yaml
# Change this line from comment to active:
# architecture-gate:

# To this:
architecture-gate:
```

### 3. Test the System

Create a test PR with proper issue linking:

```markdown
## PR Title: Fix user authentication bug

Closes #123

This PR addresses the authentication timeout issue by implementing proper session management.
```

And create a test PR with violations to verify the system catches them:

```csharp
// Example: Add this to a Core project file (should trigger BLOCKING_ISSUES)
using Honua.Postgres; // Core depending on Infrastructure - VIOLATION

public class BadExample : ControllerBase  // Controller usage - VIOLATION
{
    // Missing XML documentation - VIOLATION
    public void DoSomething()
    {
        // Reflection in hot path - VIOLATION
        var type = Type.GetType("SomeType");
        Activator.CreateInstance(type);
    }
}
```

### 4. Linking PRs to Issues

The LLM review enforces proper issue tracking. Your PR must be linked to a GitHub issue using one of these formats:

**In PR title or description:**
- `Closes #123`
- `Fixes #456`
- `Resolves #789`
- Simple reference: `#123`

**Issue Requirements:**
Your linked issue must have an "Acceptance Criteria" section:

```markdown
## Summary
Description of the issue...

## Acceptance Criteria
- [ ] All gates must pass to merge
- [ ] CI completes in < 5 minutes
- [ ] Failed gates provide clear error messages
```

**Common Formats Supported:**
- `## Acceptance Criteria`
- `### Acceptance Criteria`
- `Acceptance Criteria:`

## Review Criteria

### 🚫 BLOCKING_ISSUES (Fails PR)
- **PR Process Violations**:
  - PR not linked to any GitHub issue
  - Missing acceptance criteria in linked issue
  - Changes don't address acceptance criteria
- **Dependency Violations**: Core depending on Infrastructure
- **Controller Usage**: `ControllerBase` inheritance
- **Public Database Types**: Repository/DataAccess classes public
- **Missing Documentation**: Public APIs without XML docs

*Note: AOT compatibility is validated by the main CI build, not LLM review*

### ⚠️ NEEDS_ATTENTION (Warning)
- **Organizational Issues**: Layer vs vertical slice organization
- **Complexity**: >5 dependencies per endpoint, >4 per handler
- **Inheritance Depth**: >3 levels of inheritance
- **Performance**: Sync over async patterns

### ✅ APPROVED (Pass)
- **Clean Architecture**: Proper dependency flow
- **AOT Ready**: Source generation patterns
- **Well Documented**: Comprehensive XML docs
- **Vertical Slices**: Feature-based organization

## Cost Considerations

- **Model**: GPT-4 (higher quality architectural reasoning)
- **Estimated Cost**: ~$0.05-0.25 per PR review (varies by file count/size)
- **Context Strategy**: Diff-only, chunked into ~300-line segments
- **File Limits**: Reviews up to 50 changed files per PR (configurable)
- **Frequency**: Only on C# file changes, not on documentation

**Cost Scaling:**
- Small PRs (1-3 small .cs files): ~$0.05-0.10
- Medium PRs (4-6 medium .cs files): ~$0.10-0.20
- Large PRs (many files/large diffs): cost scales with number of chunks

## Customization

### Modify Review Criteria
Edit `scripts/architecture-review.py` in the `get_honua_architecture_rules()` function.

### Change LLM Model
In `scripts/architecture-review.py`:
```python
model="gpt-4-turbo",  # More cost-effective
# or
model="gpt-3.5-turbo",  # Even cheaper for simpler reviews
```

### Adjust Chunking Limits
In `.github/workflows/ci.yml`, set:
```yaml
REVIEW_CHUNK_LINES: 300   # max diff lines per chunk
REVIEW_MAX_FILES: 50      # max changed files to review
```

### Add Anthropic Claude Support
Set `ANTHROPIC_API_KEY` instead of `OPENAI_API_KEY` and implement the `analyze_with_anthropic()` function.

## Troubleshooting

### Review Not Running
- Check that C# files were changed
- Verify PR is not in draft mode
- Check GitHub Actions logs

### API Errors
- Verify API key is correctly set in secrets
- Check OpenAI account has sufficient credits
- Review rate limits if running many PRs

### Mock Reviews
- If no API key is configured, system falls back to mock reviews
- Mock reviews provide basic feedback but no real LLM analysis

### False Positives/Negatives
- LLM reviews are educational aids, not perfect
- Human architectural review still recommended for complex changes
- Update prompt in `scripts/architecture-review.py` to improve accuracy

## Integration with Existing CI

This LLM review runs **in addition to** existing CI checks with clear separation of concerns:

### Main CI Pipeline (Build/Merge Validation)
1. **Build + Format**: `dotnet build --warnaserror` + `dotnet format --verify-no-changes`
2. **Tests**: Unit, Integration, Architecture static tests
3. **AOT Validation**: `dotnet publish --configuration Release -p:PublishAot=true`
4. **Coverage Gates**: 80% line, 70% branch thresholds

### LLM Architecture Review (PR Feedback)
1. **Dependency Analysis**: Core→Infrastructure flow validation
2. **Design Patterns**: Vertical slices, composition over inheritance
3. **API Compliance**: Minimal APIs vs Controllers
4. **Documentation**: XML docs coverage

### Human Review (Final Approval)
- Complex architectural decisions
- Domain-specific requirements
- Strategic direction alignment

**Key Point**: LLM review focuses on **design patterns and architecture**, while CI focuses on **compilation and functionality**.
