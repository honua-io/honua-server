# Codecov Integration for Honua Server

This document explains the comprehensive code coverage setup using Codecov for the Honua Server project.

## 🎯 Overview

Our Codecov integration provides:
- **Automated Coverage Collection**: Every PR and push to trunk
- **Multiple Coverage Types**: Unit, Integration, and Combined reports
- **Coverage Thresholds**: CI-enforced gates with documented targets
- **Visual Reports**: HTML reports, badges, and trend tracking
- **Local Development**: Scripts for local coverage analysis

## 📊 Coverage Targets

### Current Thresholds (CI Enforced)
- **Line Coverage**: ≥ 1% (baseline above current ~0.7%)
- **Branch Coverage**: ≥ 0.5% (baseline above current ~0.4%)

### Long-Term Targets
- **Line Coverage**: 80%
- **Branch Coverage**: 70%

## 🔧 Configuration Files

### `codecov.yml`
Main Codecov configuration with:
- **Project Coverage**: 80% target, 1% threshold
- **Patch Coverage**: 70% target, 5% threshold
- **Component Tracking**: Core, PostgreSQL, Server components
- **Smart Ignores**: Tests, generated files, migrations

### `Directory.Build.props`
Global MSBuild coverage settings:
- **Coverlet Integration**: Multiple output formats
- **Exclusion Rules**: Test assemblies, generated code
- **Threshold Enforcement**: Local development gates

### `.coverletrc`
Coverlet-specific configuration:
- **File Exclusions**: Generated code, migrations, designer files
- **Attribute Exclusions**: CompilerGenerated, ExcludeFromCodeCoverage
- **Output Formats**: Cobertura, JSON, LCOV, OpenCover

## 🚀 Usage

### Automatic Coverage (CI/CD)

Coverage runs automatically on:
- **Every Push**: To trunk branch
- **Every PR**: Against trunk branch
- **Separate Uploads**: Unit tests, integration tests, combined

**Workflow Jobs:**
1. `test-unit` → Collects unit test coverage → Uploads to Codecov with `unittests` flag
2. `test-integration` → Collects integration coverage → Uploads to Codecov with `integration` flag
3. `coverage` → Merges all coverage → Uploads combined report

### Local Coverage Analysis

#### Bash (Linux/macOS)
```bash
# Run full coverage analysis
./scripts/coverage-local.sh

# View results
open coverage/reports/index.html
```

#### PowerShell (Windows)
```powershell
# Run full coverage analysis
./scripts/coverage-local.ps1

# Skip integration tests (faster)
./scripts/coverage-local.ps1 -SkipIntegration

# Automatically open report
./scripts/coverage-local.ps1 -OpenReport
```

### Manual Coverage Commands

#### Basic Coverage
```bash
# Unit tests only
dotnet test --collect:"XPlat Code Coverage" --filter "Category!=Integration"

# Integration tests only
dotnet test --collect:"XPlat Code Coverage" --filter "Category=Integration"

# All tests
dotnet test --collect:"XPlat Code Coverage"
```

#### Advanced Coverage with Coverlet
```bash
# With multiple output formats
dotnet test /p:CollectCoverage=true \
  /p:CoverletOutputFormat=cobertura,json,lcov \
  /p:CoverletOutput=./coverage/

# With thresholds
dotnet test /p:CollectCoverage=true \
  /p:Threshold=70 \
  /p:ThresholdType=line,branch \
  /p:ThresholdStat=minimum
```

## 📈 Codecov Dashboard Features

### Project Overview
- **Coverage Trends**: Track improvements over time
- **Component Breakdown**: Core vs PostgreSQL vs Server coverage
- **File-level Details**: Line-by-line coverage visualization

### Pull Request Integration
- **Coverage Diff**: Shows coverage change for new code
- **Patch Coverage**: Ensures new code meets standards
- **Status Checks**: Blocks merge if coverage drops below thresholds

### Flag-based Reports
- `unittests`: Coverage from unit tests only
- `integration`: Coverage from integration tests only
- `combined`: Merged coverage from all test types

## 🔍 Coverage Analysis

### What's Included
- **Source Code**: All `src/` directory code
- **Public APIs**: Full interface and public method coverage
- **Business Logic**: Core domain and application logic

### What's Excluded
- **Test Projects**: `tests/` directory entirely excluded
- **Generated Code**: Designer files, auto-generated code
- **Infrastructure**: Program.cs, Startup.cs entry points
- **Migrations**: Database schema migration files
- **Benchmarks**: Performance testing code

### Coverage Quality Metrics
- **Line Coverage**: Percentage of executable lines covered
- **Branch Coverage**: Percentage of decision branches covered
- **Method Coverage**: Percentage of methods with at least one test
- **Class Coverage**: Percentage of classes with at least one test

## 🛠️ Troubleshooting

### Common Issues

#### No Coverage Files Generated
```bash
# Check if coverlet collector is installed
dotnet list package | grep coverlet

# Restore packages if missing
dotnet restore
```

#### Low Coverage Numbers
1. **Check Exclusions**: Verify `.coverletrc` and `Directory.Build.props`
2. **Test Categories**: Ensure tests have proper `[Category]` attributes
3. **Test Discovery**: Confirm tests are being discovered and run

#### CI Coverage Upload Failures
1. **Token Configuration**: Ensure `CODECOV_TOKEN` secret is set
2. **File Paths**: Check that coverage files exist in expected locations
3. **Branch Names**: Verify trunk branch override is working

### Debug Coverage Collection
```bash
# Verbose coverlet output
dotnet test /p:CollectCoverage=true \
  /p:CoverletOutputFormat=cobertura \
  /p:Exclude="[*.Tests]*" \
  -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Verbosity=verbose

# List discovered tests
dotnet test --list-tests

# Check test categories
dotnet test --list-tests --filter "Category=Integration"
```

## 🔗 Integration Details

### GitHub Actions Integration
```yaml
- name: Upload to Codecov
  uses: codecov/codecov-action@v4
  with:
    files: ./coverage/report/Cobertura.xml
    flags: combined
    name: honua-server-coverage
    token: ${{ secrets.CODECOV_TOKEN }}
    fail_ci_if_error: true
```

### Required Secrets
- `CODECOV_TOKEN`: Project upload token from codecov.io (optional if GitHub organization is connected to Codecov)

#### When is CODECOV_TOKEN needed?

**✅ Token NOT required:**
- **Public repositories** with GitHub organization connected to Codecov
- **Private repositories** using Codecov GitHub App with proper permissions

**❌ Token required:**
- **Private repositories** using legacy GitHub integration
- **Organizations** requiring explicit token-based authentication

**🔧 To test without token:** Push a commit and check if Codecov uploads succeed. If they fail with authentication errors, add the token.

### Status Check Configuration
- **Project Coverage**: Must maintain 80% with 1% tolerance
- **Patch Coverage**: New code must have 70% with 5% tolerance
- **Flags**: Unit and integration tests tracked separately

## 📋 Best Practices

### Writing Testable Code
1. **Dependency Injection**: Use interfaces for testability
2. **Pure Functions**: Minimize side effects where possible
3. **Single Responsibility**: Keep methods focused and testable
4. **Async Patterns**: Use proper async/await for database operations

### Coverage Improvement Strategy
1. **Start with High-Value**: Focus on business logic first
2. **Test Public APIs**: Ensure all public interfaces are tested
3. **Edge Cases**: Add tests for error conditions and boundaries
4. **Integration Points**: Test database interactions and external APIs

### Maintaining Quality
1. **Review Coverage Diffs**: Check impact of changes in PRs
2. **Set Component Goals**: Different targets for different areas
3. **Regular Reviews**: Weekly/monthly coverage trend analysis
4. **Refactor for Testability**: Improve code structure based on coverage gaps

## 🏷️ Useful Links

- [Codecov Dashboard](https://codecov.io/gh/YOUR_ORG/honua-server)
- [Coverlet Documentation](https://github.com/coverlet-coverage/coverlet)
- [MSTest Code Coverage](https://docs.microsoft.com/en-us/dotnet/core/testing/unit-testing-code-coverage)
