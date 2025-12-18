# Honua Architecture Review Criteria

## 🚫 BLOCKING_ISSUES (Fail PR - Must Fix Before Merge)

### Critical Violations:
1. **Dependency Direction Violations**
   - `Honua.Core` depending on `Honua.Postgres` or `Honua.Server`
   - `Honua.Postgres` depending on `Honua.Server`
   - Detection: `using` statements, project references

2. **API Pattern Violations**
   - Controller classes inheriting from `ControllerBase`
   - Usage of `[ApiController]` attribute
   - Detection: Class inheritance, attribute usage

3. **AOT Compatibility Violations**
   - Reflection usage in hot paths without source generation
   - `Activator.CreateInstance` without generic constraints
   - Dynamic JSON serialization without source generation
   - Detection: Reflection APIs, dynamic patterns

4. **Security/Encapsulation Violations**
   - Public database repository types (`*Repository` classes)
   - Public data access types (`*DataAccess` classes)
   - Database connection strings in non-infrastructure layers

5. **Quality Gate Violations**
   - Public types without XML documentation (`/// <summary>`)
   - Missing integration tests for new endpoints
   - Detection: Missing XML docs on public APIs

## ⚠️ NEEDS_ATTENTION (Pass with Warnings - Review Required)

### Design Concerns:
1. **Organizational Issues**
   - Layer-based organization instead of vertical slices
   - Feature code scattered across multiple layers
   - Detection: File organization patterns

2. **Complexity Issues**
   - >5 dependencies in endpoint handlers
   - >4 dependencies in business logic
   - Deep inheritance hierarchies (>3 levels)
   - Detection: Constructor parameters, inheritance depth

3. **Performance Concerns**
   - Synchronous database operations in async context
   - Missing query optimization hints
   - Large object allocations in hot paths
   - Detection: Sync over async, allocation patterns

4. **Maintainability Issues**
   - God classes (>300 lines)
   - Methods with >10 parameters
   - Lack of composition over inheritance

## ✅ APPROVED (Pass - Good to Merge)

### Positive Indicators:
1. **Clean Architecture**
   - Proper dependency flow (Core <- Infrastructure)
   - Vertical slice organization
   - Single responsibility principle

2. **AOT Readiness**
   - Source-generated JSON serializers
   - Compile-time dependency injection
   - Value types for data transfer

3. **Quality Patterns**
   - Comprehensive XML documentation
   - Integration tests for public APIs
   - Proper error handling

## Implementation in Review Script

```python
def assess_overall_rating(analysis_text: str) -> str:
    """Determine overall assessment from analysis"""

    blocking_keywords = [
        "dependency violation", "controller inheritance",
        "reflection in hot path", "public repository",
        "missing xml documentation", "aot breaking"
    ]

    warning_keywords = [
        "layer organization", "too many dependencies",
        "inheritance hierarchy", "sync over async",
        "god class", "complex method"
    ]

    analysis_lower = analysis_text.lower()

    # Check for blocking issues
    if any(keyword in analysis_lower for keyword in blocking_keywords):
        return "BLOCKING_ISSUES"

    # Check for warning-level issues
    if any(keyword in analysis_lower for keyword in warning_keywords):
        return "NEEDS_ATTENTION"

    # Default to approved if no major issues
    return "APPROVED"
```

## GitHub Actions Integration

```yaml
# Optional: Block PR on BLOCKING_ISSUES
architecture-gate:
  name: Architecture Gate
  needs: llm-architecture-review
  runs-on: ubuntu-latest
  if: contains(github.event.pull_request.body, 'BLOCKING_ISSUES') || contains(steps.analysis.outputs.result, 'BLOCKING_ISSUES')
  steps:
    - name: Block PR on Critical Issues
      run: |
        echo "::error::Architecture review found blocking issues that must be resolved before merge."
        echo "::error::Please review the LLM Architecture Review comment and address critical violations."
        exit 1
```

## Usage Guidelines

1. **For Repository Owners**: Configure `OPENAI_API_KEY` in GitHub Secrets
2. **For Contributors**: Review LLM comments and address blocking issues
3. **For Reviewers**: Use LLM feedback as a starting point, not final authority
4. **For Architecture Evolution**: Update criteria as project matures