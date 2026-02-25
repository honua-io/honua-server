# OGC CITE Conformance Testing Guide

This document describes how to run OGC Compliance and Interoperability Testing & Evaluation (CITE) suite tests against Honua Server to validate OGC API Features conformance.

## Overview

The CITE test suite validates that Honua Server correctly implements the OGC API Features specification. This includes:

- **Core conformance class**: Basic API structure and behavior
- **OpenAPI 3.0 conformance class**: Valid OpenAPI specification
- **GeoJSON conformance class**: Correct GeoJSON feature responses
- **HTML conformance class**: Human-readable HTML responses

## Quick Start

### Prerequisites

- Docker and Docker Compose
- Git (for version information)
- curl and jq (for endpoint verification)

### Running CITE Tests Locally

```bash
# Run all conformance tests
./scripts/run-cite-tests.sh

# Run with specific options
./scripts/run-cite-tests.sh --verbose --no-cleanup

# Interactive mode (keep services running for manual testing)
./scripts/run-cite-tests.sh --interactive
```

### Running in CI

CITE tests run automatically:
- Weekly via scheduled workflow
- Manually via workflow dispatch

## Test Profiles

### Full Profile (default)
Comprehensive testing including optional features:
- All default classes
- CRS (Coordinate Reference Systems)
- Query parameters
- Advanced features

Use `--profile default` to run the core conformance classes only.

### Minimal Profile
Basic conformance testing for quick validation:
- Core only

### Core Profile (`--profile default`)
Tests core OGC API Features 1.0 conformance classes:
- Core
- OpenAPI 3.0
- GeoJSON
- HTML

## Configuration

### Test Parameters

The CITE test suite is configured via `docker/cite-config/test-params.xml`:

```xml
<values xmlns:parsers="http://www.occamlab.com/te/parsers">
  <parsers:session>
    <parsers:test>ets-ogcapi-features10</parsers:test>
    <parsers:profile>full</parsers:profile>
  </parsers:session>
  <value key="ogc-api-features-uri">http://honua-server:8080/ogc/features</value>
  <value key="collectionsLimit">limited</value>
  <value key="noOfCollections">1</value>
</values>
```

### Docker Compose Services

The test environment includes:

1. **honua-server**: The main application under test
2. **postgres**: PostGIS database backend
3. **cite-engine**: CITE Team Engine web interface
4. **cite-runner**: Automated test execution

## Understanding Results

### Test Output Structure

```
cite-results/
├── cite-summary.md          # Human-readable summary
├── session-*/               # Test session results
│   ├── test-results.xml     # Detailed test results
│   ├── test-report.html     # HTML report
│   └── logs/                # Test execution logs
```

### Reading Test Results

#### Summary Report (`cite-summary.md`)
- Overall pass/fail status
- Test count and success rate
- Conformance class results
- Next steps and recommendations

#### Detailed Results (`test-results.xml`)
- Individual test case outcomes
- Assertion details and failure messages
- Request/response details
- Performance metrics

#### HTML Report (`test-report.html`)
- Interactive web-based results
- Visual test status indicators
- Linked test documentation
- Error details with context

### Success Criteria

For conformance runs:
- **Core**: 100% pass rate (required)
- **OpenAPI 3.0**: 100% pass rate (required)
- **GeoJSON**: 100% pass rate (required)
- **HTML**: 90%+ pass rate (required for user-facing features)

## Troubleshooting

### Common Issues

#### 1. Service Health Check Failures

**Symptoms**: Tests fail before execution begins
```bash
❌ Timeout waiting for Honua Server to become healthy
```

**Solutions**:
- Check Docker container logs: `docker-compose -f docker/cite-compose.yml logs honua-server`
- Verify database connectivity
- Ensure ports 8080-8081 are available
- Check disk space and memory availability

#### 2. Endpoint Accessibility Issues

**Symptoms**: API endpoints return 404 or connection refused
```bash
❌ Landing page not accessible
❌ Collections endpoint not accessible
```

**Solutions**:
- Verify OGC API Features endpoints are implemented
- Check API routing configuration
- Validate database contains test data
- Review application logs for errors

#### 3. Conformance Class Failures

**Symptoms**: Specific conformance classes fail tests
```bash
⚠️ Some tests failed. Review results for details.
```

**Solutions by conformance class**:

**Core failures**:
- Verify JSON structure matches OGC spec
- Check HTTP status codes (200, 404, etc.)
- Validate required response headers
- Ensure pagination links are correct

**OpenAPI 3.0 failures**:
- Check `/api` or `/swagger` endpoint returns valid OpenAPI spec
- Validate OpenAPI document against JSON Schema
- Ensure all endpoints are documented
- Verify parameter and response schemas

**GeoJSON failures**:
- Validate GeoJSON output with geometry
- Check coordinate ordering (longitude, latitude)
- Ensure required GeoJSON properties
- Verify CRS handling

**HTML failures**:
- Check HTML responses have proper content-type
- Validate HTML structure and accessibility
- Ensure navigation links work correctly
- Test content negotiation

#### 4. Test Timeout Issues

**Symptoms**: Tests exceed 30-minute timeout
```bash
❌ CITE tests timed out after 1800 seconds
```

**Solutions**:
- Optimize database queries for performance
- Increase Docker container resource limits
- Check for infinite loops in API logic
- Monitor system resource usage
- Use `--profile minimal` for faster testing

### Debug Mode

Run tests with detailed logging:

```bash
# Enable verbose output
./scripts/run-cite-tests.sh --verbose

# Keep containers running for investigation
./scripts/run-cite-tests.sh --no-cleanup

# Interactive mode with manual testing
./scripts/run-cite-tests.sh --interactive
```

In interactive mode, access services directly:
- **Honua Server**: http://localhost:8080
- **CITE Team Engine**: http://localhost:8081/teamengine
- **Database**: localhost:5433

### Manual Test Execution

For detailed investigation:

1. Start services in interactive mode:
   ```bash
   ./scripts/run-cite-tests.sh --interactive
   ```

2. Access CITE Team Engine at http://localhost:8081/teamengine

3. Select "OGC API Features 1.0" test suite

4. Configure test parameters:
   - Service URL: `http://honua-server:8080`
   - Select conformance classes to test

5. Execute tests and review results in web interface

## Best Practices

### Local Development

1. **Test Early and Often**: Run CITE tests during development to catch issues early
2. **Use Profiles**: Start with minimal profile for quick feedback, then run full tests
3. **Check Logs**: Always review service logs when tests fail
4. **Incremental Testing**: Fix one conformance class at a time

### CI Integration

1. **Pull Request Validation**: All PRs must pass core conformance tests
2. **Weekly Monitoring**: Scheduled tests catch conformance regressions
3. **Artifact Preservation**: Test results are preserved for 30 days
4. **Failure Notifications**: Failed tests block merge until fixed

### Performance Optimization

1. **Test Data**: Use representative but minimal test datasets
2. **Resource Limits**: Configure appropriate Docker resource limits
3. **Parallel Execution**: Consider running multiple test profiles in parallel
4. **Caching**: Use Docker layer caching to speed up builds

## Integration with Development Workflow

### Pre-commit Hooks

Consider adding CITE validation to pre-commit hooks:

```bash
#!/bin/bash
# .git/hooks/pre-push

# Quick conformance check before push
if ! ./scripts/run-cite-tests.sh --profile minimal --no-cleanup; then
    echo "❌ CITE conformance tests failed"
    echo "Fix conformance issues before pushing"
    exit 1
fi
```

### Release Validation

Before releases, run comprehensive testing:

```bash
# Full conformance validation
./scripts/run-cite-tests.sh --profile full --verbose

# Performance testing (if integrated)
./scripts/run-performance-tests.sh

# Security scanning
./scripts/run-security-scan.sh
```

## Conformance Classes Reference

### Core (Required)
- Landing page at `/`
- API definition at `/api` or `/swagger`
- Conformance declaration at `/conformance`
- Collections at `/collections`
- Collection metadata at `/collections/{collectionId}`

### OpenAPI 3.0 (Required)
- Valid OpenAPI 3.0 specification
- All endpoints documented
- Correct parameter definitions
- Response schema validation

### GeoJSON (Required)
- Features endpoint at `/collections/{collectionId}/items`
- Valid GeoJSON Feature Collection responses
- Individual features at `/collections/{collectionId}/items/{featureId}`
- Coordinate Reference System support

### HTML (Required)
- Human-readable HTML responses
- Content negotiation via Accept headers
- Navigation between resources
- Accessible markup

### CRS (Optional)
- Coordinate Reference System parameter support
- CRS transformation capabilities
- EPSG code support
- Bounding box queries with different CRS

## Resources

- [OGC API Features Specification](https://docs.ogc.org/is/17-069r3/17-069r3.html)
- [CITE Team Engine Documentation](https://cite.opengeospatial.org/)
- [OGC API Features Test Suite](https://github.com/opengeospatial/ets-ogcapi-features10)
- [Docker Compose Reference](https://docs.docker.com/compose/)

## Support

For CITE testing issues:

1. Check the [troubleshooting section](#troubleshooting)
2. Review workflow logs and artifacts
3. Test locally with `--verbose` and `--interactive` flags
4. Check OGC CITE community resources
5. Create GitHub issue with test results and logs
