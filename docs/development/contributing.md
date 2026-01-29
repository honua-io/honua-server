# Contributing to Honua Server

Thank you for your interest in contributing to Honua Server! This guide provides everything you need to know to contribute effectively.

## Getting Started

1. **Read the Developer Guide**: Start with [`getting-started.md`](getting-started.md) to set up your development environment.

2. **Understand the Architecture**: Review the Architecture Decision Records in [`docs/adr/`](../adr/) to understand key design decisions.

3. **Check Existing Issues**: Look at [GitHub Issues](https://github.com/your-org/honua-server/issues) for tasks marked with `good first issue` or `help wanted`.

## Development Philosophy

Honua Server is built with these core principles:

### Quality Over Speed
- **Warnings as Errors**: All builds must pass without warnings
- **High Test Coverage**: 80% line coverage, 70% branch coverage minimum
- **API Surface Coverage**: 100% - every endpoint must have integration tests

### Clean Architecture
- **Dependency Direction**: `Server` → `Postgres` → `Core` (never reversed)
- **Vertical Slices**: Features organized by business capability, not technical layer
- **Minimal Dependencies**: Max 5 per endpoint, max 4 per handler

### Security First
- **Multi-layer Validation**: All inputs validated at multiple levels
- **No Information Leakage**: Error messages don't expose internal details
- **AOT Compatibility**: Source-generated JSON for security and performance

## Code Contribution Process

### 1. Setting Up Your Development Environment

```bash
# Fork the repository on GitHub, then clone your fork
git clone https://github.com/YOUR_USERNAME/honua-server.git
cd honua-server

# Add the upstream remote
git remote add upstream https://github.com/your-org/honua-server.git

# Set up development environment
docker compose up -d
dotnet restore
dotnet test
```

### 2. Creating a Feature Branch

```bash
# Create a branch from the latest main
git checkout main
git pull upstream main
git checkout -b feature/your-feature-name

# Use descriptive branch names
git checkout -b feature/add-ogc-tiles-endpoint
git checkout -b fix/geometry-validation-edge-case
git checkout -b docs/improve-api-examples
```

### 3. Making Changes

#### Code Style Requirements

**Formatting (Enforced by CI):**
```bash
# Always run before committing
dotnet format Honua.sln

# Verify no formatting changes needed
dotnet format Honua.sln --verify-no-changes
```

**Naming Conventions:**
```csharp
// ✅ Good
public class FeatureQueryValidator { }
private readonly IFeatureStore _featureStore;
public async Task<LayerDefinition> GetLayerAsync(int layerId) { }

// ❌ Avoid
public class FQV { }  // Unclear abbreviation
private readonly IFeatureStore featureStore;  // Missing underscore
public async Task<LayerDefinition> Get(int id) { }  // Unclear method name
```

**XML Documentation (Required for Public APIs):**
```csharp
/// <summary>
/// Validates geospatial queries for feature requests.
/// </summary>
/// <param name="query">The query to validate</param>
/// <returns>Validation result with any errors</returns>
public ValidationResult ValidateQuery(FeatureQuery query) { }
```

#### Architecture Patterns

**✅ Follow These Patterns:**

1. **Vertical Slice Organization:**
   ```csharp
   // ✅ Good: Feature-based organization
   Features/FeatureServer/
   ├── FeatureServerEndpoints.cs      // API layer
   ├── FeatureServerHandler.cs        // Business logic
   ├── Models/FeatureServerModels.cs  // DTOs
   └── Services/GeometryValidator.cs  // Supporting services
   ```

2. **Minimal API Pattern:**
   ```csharp
   // ✅ Good: Minimal API
   public static void MapFeatureServerEndpoints(this WebApplication app)
   {
       app.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId}/query",
           async (int serviceId, int layerId, IFeatureStore store) =>
           {
               // Handler logic
           });
   }
   ```

3. **Dependency Injection Limits:**
   ```csharp
   // ✅ Good: Limited dependencies
   public static async Task<IResult> QueryFeatures(
       int serviceId,
       int layerId,
       IFeatureStore store,
       IValidator validator,
       ILogger logger)  // 3 dependencies - good
   ```

**❌ Avoid These Anti-Patterns:**

1. **Controller Classes:**
   ```csharp
   // ❌ Avoid: Creates dependency injection issues
   public class FeatureServerController : ControllerBase
   {
       // This recreates the 22-dependency problem from legacy system
   }
   ```

2. **Layer-Based Organization:**
   ```csharp
   // ❌ Avoid: Layer-based organization
   Controllers/    // Mixed business domains
   Services/       // Mixed business logic
   Models/         // Mixed DTOs
   ```

3. **Too Many Dependencies:**
   ```csharp
   // ❌ Avoid: Too many dependencies
   public QueryHandler(
       IFeatureStore store, ILayerCatalog catalog, IValidator validator,
       ILogger logger, IMetrics metrics, IEventBus events,
       ICacheService cache, IMapper mapper)  // 8 dependencies - too many!
   ```

### 4. Testing Requirements

#### Every Feature Needs Tests

**Integration Tests (Required):**
```csharp
[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public class QueryEndpointTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithWhereClause_ReturnsFilteredFeatures()
    {
        // Arrange: Set up test data
        var client = _factory.CreateClient();
        await SeedTestData();

        // Act: Call the API
        var response = await client.GetAsync("/rest/services/1/FeatureServer/0/query?where=name='Test'");

        // Assert: Verify behavior
        response.Should().BeSuccessful();
        var featureSet = await response.Content.ReadFromJsonAsync<FeatureSet>();
        featureSet!.Features.Should().HaveCount(1);
    }
}
```

**Unit Tests (For Complex Logic):**
```csharp
[Unit]
public class GeometryValidatorTests
{
    [Theory]
    [InlineData("POINT(1 1)", true)]
    [InlineData("POINT(1)", false)]
    public void ValidateWkt_WithVariousInputs_ReturnsExpectedResult(string wkt, bool expectedValid)
    {
        // Arrange
        var validator = new GeometryValidator();

        // Act
        var result = validator.ValidateWkt(wkt);

        // Assert
        result.IsValid.Should().Be(expectedValid);
    }
}
```

#### Running Tests

```bash
# Run all tests
dotnet test

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run specific test categories
dotnet test --filter Category=Integration
dotnet test --filter Category=Unit

# Run architecture tests (enforces rules)
dotnet test tests/Honua.Architecture.Tests/
```

#### Coverage Requirements

The project enforces coverage gates:
- **API Surface**: 100% (every endpoint has integration test)
- **Line Coverage**: 80% target (CI enforces 40% minimum during development)
- **Branch Coverage**: 70% target (CI enforces 30% minimum during development)

### 5. Committing Changes

#### Conventional Commits

Use conventional commit format:
```bash
# Features
git commit -m "feat: add spatial filtering for feature queries"
git commit -m "feat(api): implement OGC Tiles endpoints"

# Bug fixes
git commit -m "fix: resolve geometry validation edge case"
git commit -m "fix(import): handle malformed shapefiles gracefully"

# Documentation
git commit -m "docs: update API examples for OData endpoints"
git commit -m "docs(adr): add decision record for caching strategy"

# Tests
git commit -m "test: add integration tests for import service"
git commit -m "test(performance): add benchmarks for spatial queries"

# Refactoring
git commit -m "refactor: extract geometry validation to service"

# CI/Build
git commit -m "ci: update GitHub Actions workflow"
git commit -m "build: upgrade to .NET 8.0.1"
```

#### Commit Message Guidelines

**Good commit messages:**
```
feat: add CQL2 filter support for OGC API Features (#123)

- Implement CQL2 parser for complex spatial queries
- Add validation for CQL2 expressions
- Support temporal and attribute filters
- Add comprehensive test coverage

Resolves #123
```

**Avoid:**
```
fix bug          // Too vague
WIP             // Don't commit work-in-progress
fixed stuff     // Not descriptive
```

### 6. Opening a Pull Request

#### Pre-Submission Checklist

Before opening a PR, ensure:
- [ ] Code is formatted: `dotnet format Honua.sln`
- [ ] All tests pass: `dotnet test`
- [ ] Architecture tests pass
- [ ] New functionality has integration tests
- [ ] Public APIs have XML documentation
- [ ] No TODO comments in committed code
- [ ] Branch is up to date with main

#### Pull Request Template

Use this template for your PR description:

```markdown
## Summary
Brief description of what this PR accomplishes.

## Changes Made
- [ ] New feature: Description
- [ ] Bug fix: Description
- [ ] Documentation: Description
- [ ] Tests: Description

## Testing
- [ ] Added integration tests
- [ ] Added unit tests for complex logic
- [ ] Manual testing performed
- [ ] All existing tests pass

## Performance Impact
- [ ] No performance impact
- [ ] Performance improved (provide details)
- [ ] Performance regression (justify necessity)

## Breaking Changes
- [ ] No breaking changes
- [ ] Breaking changes (describe migration path)

## Checklist
- [ ] Code formatted with `dotnet format`
- [ ] All tests pass
- [ ] Architecture tests pass
- [ ] Public APIs documented
- [ ] Changes align with project goals

Resolves #issue_number
```

#### Code Review Process

**What Reviewers Look For:**

1. **Architecture Compliance:**
   - Dependency direction follows Core ← Postgres ← Server
   - Features use vertical slice organization
   - Minimal APIs used instead of controllers

2. **Code Quality:**
   - Clear, descriptive naming
   - Proper error handling
   - Security considerations
   - Performance implications

3. **Testing:**
   - Integration tests for new endpoints
   - Unit tests for complex business logic
   - Edge cases covered

4. **Documentation:**
   - Public APIs have XML docs
   - Complex business logic explained
   - ADRs updated for architectural changes

**Addressing Feedback:**

```bash
# Make requested changes
git add .
git commit -m "fix: address code review feedback"

# Push changes
git push origin feature/your-feature-name

# If major changes needed, consider squashing commits:
git rebase -i HEAD~3  # Interactive rebase for last 3 commits
```

## Types of Contributions

### 1. Bug Fixes

**Process:**
1. Reproduce the bug with a failing test
2. Fix the issue
3. Ensure the test now passes
4. Add additional tests for edge cases

**Example:**
```csharp
// First, create a failing test
[IntegrationTest]
public async Task Query_WithComplexGeometry_ShouldNotTimeout()
{
    // This test should initially fail, demonstrating the bug
    var complexPolygon = CreateComplexPolygon();
    var response = await QueryWithGeometry(complexPolygon);
    response.Should().BeSuccessful();
}
```

### 2. New Features

**Process:**
1. Check if an issue exists, create one if not
2. Discuss approach in the issue
3. Implement with tests first (TDD)
4. Update documentation

**Example workflow for adding a new endpoint:**
```csharp
// 1. Write failing integration test
[Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/stats")]
public async Task GetLayerStats_WithValidLayer_ReturnsStatistics()
{
    var response = await client.GetAsync("/rest/services/1/FeatureServer/0/stats");
    response.Should().BeSuccessful();
    // Test will fail initially
}

// 2. Implement minimal endpoint
app.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId}/stats",
    async (int serviceId, int layerId) => Results.Ok(new LayerStats()));

// 3. Add business logic
// 4. Refactor and add more tests
```

### 3. Performance Improvements

**Guidelines:**
- Include benchmarks before/after
- Ensure no regression in functionality
- Consider memory allocation impact
- Test with realistic data sizes

**Example:**
```csharp
[Benchmark]
public class SpatialQueryBenchmarks
{
    [Benchmark]
    public async Task QueryFeatures_10000_Points()
    {
        await _store.QueryFeaturesAsync(bounds, limit: 10000);
    }
}
```

### 4. Documentation Improvements

**Types of documentation contributions:**
- API examples and tutorials
- Architecture Decision Records (ADRs)
- Troubleshooting guides
- Code comments for complex algorithms

### 5. Infrastructure and Tooling

**Examples:**
- CI/CD improvements
- Development tooling
- Docker configuration
- Monitoring and observability

## Architecture Enforcement

The project uses architecture tests to prevent common anti-patterns:

### Dependency Direction Rules

```csharp
// These tests will fail your PR if violated:

[Test]
public void Core_ShouldNotDependOn_Infrastructure()
{
    // Honua.Core cannot reference Honua.Postgres or Honua.Server
}

[Test]
public void Server_ShouldNotHaveControllers()
{
    // No classes inheriting from ControllerBase allowed
}

[Test]
public void PublicTypes_MustHaveDocumentation()
{
    // All public classes/interfaces must have XML documentation
}
```

### How to Handle Architecture Violations

If architecture tests fail:

1. **Understand the Rule**: Read the failing test to understand what's being enforced
2. **Follow the Pattern**: Look at existing code for the correct pattern
3. **Ask for Help**: If unsure, ask in the PR or create a discussion

## Getting Help

### Before You Start
- Read existing documentation in `docs/`
- Search existing issues and PRs
- Check Architecture Decision Records for context

### During Development
- **Technical Questions**: Create a GitHub Discussion
- **Bug Reports**: Create an Issue with reproduction steps
- **Feature Proposals**: Create an Issue to discuss approach

### Code Review Help
- **Comment on specific lines** in PR for targeted questions
- **Request review** from maintainers when ready
- **Be responsive** to feedback and questions

## Recognition

Contributors are recognized in several ways:
- **Contributors section** in README
- **Release notes** mention significant contributions
- **GitHub insights** track all contributions

## Code of Conduct

We follow the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md). Please read it to understand expected behavior in our community.

## License

By contributing, you agree that your contributions will be licensed under the same license as the project (Elastic License 2.0).

---

Thank you for contributing to Honua Server! Your efforts help build better geospatial infrastructure for everyone.
