# GitHub Actions Workflows

This document describes the optimized CI/CD pipeline for Honua Server.

## Overview

The workflows are designed for efficiency and avoid duplication:

- **Daily CI/CD**: Fast feedback for development
- **Nightly Tests**: Comprehensive testing and benchmarks
- **Single Security Scan**: No duplicate CodeQL scans
- **Automated Dependencies**: Weekly dependency updates

## Workflows

### 1. CI (`ci.yml`)

**Triggers**: Push/PR to `trunk`

**Jobs**:
- **Build & Test**: Core unit and integration tests
- **Architecture Tests**: ADR-enforced architecture validation
- **Security Scan**: Single CodeQL analysis (no duplicates)
- **Docker Build**: Validate containerization

**Duration**: ~5-10 minutes

### 2. Nightly Tests (`nightly.yml`)

**Triggers**:
- Scheduled: Daily at 2 AM UTC
- Manual: `workflow_dispatch`

**Jobs**:
- **Performance Benchmarks**: BenchmarkDotNet performance tests
- **CITE Conformance Tests**: OGC API Features compliance
- **Extended Integration Tests**: Comprehensive test matrix
- **Security Vulnerability Scan**: Dependency vulnerability checks
- **Notification**: Issue creation on failures

**Duration**: ~30-60 minutes

### 3. Release (`release.yml`)

**Triggers**:
- Git tags matching `v*`
- Manual with version input

**Jobs**:
- **Validate Release**: Full test suite validation
- **Build & Publish Docker**: Multi-architecture container images
- **Create Release**: Automated GitHub releases with changelog

### 4. Dependency Updates (`dependency-update.yml`)

**Triggers**:
- Scheduled: Weekly on Mondays at 6 AM UTC
- Manual: `workflow_dispatch`

**Jobs**:
- **Update .NET Dependencies**: NuGet package updates
- **Update GitHub Actions**: Action version updates
- **Update Docker Images**: Base image update checks

## Key Optimizations

### ✅ Eliminated Duplicates
- **Single CodeQL scan** in daily CI (no duplicate security scans)
- **Consolidated security scanning** in nightly workflow
- **Unified Docker builds** across workflows

### ✅ Nightly-Only Heavy Tests
- **Performance benchmarks** moved to nightly schedule
- **CITE conformance tests** run nightly (30-minute timeout)
- **Extended integration tests** with full matrix

### ✅ Smart Scheduling
- **CI**: Immediate feedback on code changes
- **Nightly**: 2 AM UTC for comprehensive testing
- **Dependencies**: Monday mornings for weekly review

### ✅ Efficient Resource Usage
- **Parallel job execution** where possible
- **Shared PostgreSQL services** across test jobs
- **Cached Docker layers** for faster builds
- **Artifact cleanup** with appropriate retention periods

## Configuration

### Required Secrets
```
GITHUB_TOKEN (automatically provided)
```

### Optional Secrets
```
CODECOV_TOKEN (for code coverage)
SLACK_WEBHOOK (for notifications)
```

### Branch Protection
Recommended branch protection rules for `trunk`:
```yaml
required_status_checks:
  strict: true
  contexts:
    - "Build & Test"
    - "Architecture Tests"
    - "Security Scan"
    - "Docker Build"
enforce_admins: true
required_pull_request_reviews:
  required_approving_review_count: 1
  dismiss_stale_reviews: true
restrictions: null
```

## Monitoring

### Success Metrics
- **CI Success Rate**: >95% for daily builds
- **CITE Compliance**: 100% conformance test passing
- **Performance Regression**: <5% performance degradation alerts
- **Security Score**: Zero high/critical vulnerabilities

### Alerts
- **Failed Nightly Tests**: Auto-created GitHub issues
- **Performance Regression**: Benchmark comparison alerts
- **Security Vulnerabilities**: SARIF upload to security tab
- **Dependency Updates**: Weekly PR creation for review

## Usage Examples

### Manual CITE Test Run
```bash
gh workflow run nightly.yml
```

### Manual Release
```bash
gh workflow run release.yml -f version=v1.2.0
```

### Check Workflow Status
```bash
gh run list --workflow=ci.yml
```

## Troubleshooting

### Common Issues

1. **CITE Tests Timeout**
   - Check PostgreSQL service health
   - Verify Docker container build
   - Review test parallelization

2. **Performance Regression**
   - Compare benchmark artifacts
   - Check for memory/CPU usage changes
   - Review recent dependency updates

3. **Security Scan Failures**
   - Review Trivy/CodeQL results
   - Update vulnerable dependencies
   - Apply security patches

### Debug Commands
```bash
# Check workflow logs
gh run view <run-id> --log

# Download artifacts
gh run download <run-id>

# Check workflow definition
gh workflow view ci.yml
```

## Maintenance

### Weekly Tasks
- Review dependency update PRs
- Monitor performance benchmark trends
- Check security scan results

### Monthly Tasks
- Update workflow action versions
- Review artifact retention policies
- Optimize job execution times

### Quarterly Tasks
- Review branch protection rules
- Update documentation
- Performance baseline updates