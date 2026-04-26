# OGC API Tiles CITE Conformance Testing Guide

This document describes how to run OGC Compliance and Interoperability Testing & Evaluation (CITE) suite tests against Honua Server to validate OGC API Tiles conformance.

## Overview

The CITE test suite validates that Honua Server correctly implements the OGC API Tiles specification. This includes:

- **Core conformance class**: Basic API structure and tile retrieval
- **Tileset conformance class**: Tileset metadata and links
- **Tilesets List conformance class**: Listing available tilesets
- **Dataset Tilesets conformance class**: Dataset-level tile access
- **Geodata Tilesets conformance class**: Collection-level tile access
- **MVT conformance class**: Mapbox Vector Tile format support
- **OpenAPI 3.0 conformance class**: Valid OpenAPI specification

## Quick Start

### Prerequisites

- Docker and Docker Compose
- Git (for version information)
- curl and jq (for endpoint verification)

### Running CITE Tests Locally

```bash
# Run all conformance tests
./scripts/conformance/cite/run-cite-tiles-tests.sh

# Run with specific options
./scripts/conformance/cite/run-cite-tiles-tests.sh --verbose --no-cleanup

# Interactive mode (keep services running for manual testing)
./scripts/conformance/cite/run-cite-tiles-tests.sh --interactive
```

### Running in CI

CITE Tiles tests run automatically:
- On pull requests to `trunk`/`main` that touch OGC Tiles/CITE files
- On pushes to `trunk`/`main` that touch OGC Tiles/CITE files
- Weekly via scheduled workflow (Tuesdays at 6am UTC)
- Manually via workflow dispatch

## CI Baseline

- `failed_tests` must be `0`
- `total_tests` must be greater than `0`
- Results are uploaded as artifacts, including markdown summary and raw TeamEngine outputs

## Test Profiles

### Full Profile (default)
Comprehensive testing including all conformance classes:
- Core
- Tileset
- Tilesets List
- Dataset Tilesets
- Geodata Tilesets
- MVT
- OpenAPI 3.0

### Minimal Profile
Basic conformance testing for quick validation:
- Core only

### Default Profile
Tests core OGC API Tiles 1.0 conformance classes:
- Core
- Tileset
- MVT
- OpenAPI 3.0

## Configuration

### Test Parameters

The CITE test suite is configured via `docker/cite/ogc-api-tiles/config/test-params.xml`:

```xml
<values xmlns:parsers="http://www.occamlab.com/te/parsers">
  <parsers:session>
    <parsers:test>ets-ogcapi-tiles10</parsers:test>
    <parsers:profile>full</parsers:profile>
  </parsers:session>
  <value key="ogc-api-tiles-uri">http://honua-server:8080/ogc/tiles</value>
  <value key="collectionsLimit">limited</value>
  <value key="noOfCollections">1</value>
</values>
```

### Docker Compose Services

The test environment includes:

1. **honua-server**: The main application under test
2. **postgres**: PostGIS database backend with vector data
3. **cite-engine**: CITE Team Engine web interface
4. **cite-runner**: Automated test execution

## Understanding Results

### Test Output Structure

```
cite-tiles-results/
├── cite-summary.md          # Human-readable summary
├── conformance.json         # Captured conformance declaration
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

For conformance:
- **Core**: 100% pass rate (required)
- **Tileset**: 100% pass rate (required)
- **MVT**: 100% pass rate (required for vector tiles)
- **OpenAPI 3.0**: 100% pass rate (required)
- **Dataset/Geodata Tilesets**: 100% pass rate (if implemented)

## Troubleshooting

### Common Issues

#### 1. Service Health Check Failures

**Symptoms**: Tests fail before execution begins
```bash
Timeout waiting for Honua Server to become healthy
```

**Solutions**:
- Check Docker container logs: `docker compose -f docker/cite/ogc-api-tiles/compose.yml logs honua-server`
- Verify database connectivity
- Ensure ports 8080, 8082, 5434 are available
- Check disk space and memory availability

#### 2. Endpoint Accessibility Issues

**Symptoms**: API endpoints return 404 or connection refused
```bash
Tiles landing page not accessible
TileMatrixSets endpoint not accessible
```

**Solutions**:
- Verify OGC API Tiles endpoints are implemented
- Check API routing configuration
- Validate database contains test data
- Review application logs for errors

#### 3. Conformance Class Failures

**Symptoms**: Specific conformance classes fail tests
```bash
Some tests failed. Review results for details.
```

**Solutions by conformance class**:

**Core failures**:
- Verify JSON structure matches OGC spec
- Check HTTP status codes (200, 204, 404, etc.)
- Validate required response headers
- Ensure pagination links are correct

**Tileset failures**:
- Check tileset metadata structure
- Verify TileMatrixSetURI is valid
- Ensure links include self, item, and tiling-scheme rels
- Validate media types are declared

**MVT failures**:
- Validate MVT binary format
- Check Content-Type header (application/vnd.mapbox-vector-tile)
- Ensure tiles contain valid vector data
- Verify coordinate system handling

**OpenAPI 3.0 failures**:
- Check `/ogc/tiles/openapi.json` returns valid OpenAPI spec
- Validate OpenAPI document against JSON Schema
- Ensure all endpoints are documented
- Verify parameter and response schemas

#### 4. Test Timeout Issues

**Symptoms**: Tests exceed 30-minute timeout
```bash
CITE tests timed out after 1800 seconds
```

**Solutions**:
- Optimize tile generation performance
- Increase Docker container resource limits
- Check for infinite loops in API logic
- Monitor system resource usage
- Use `--profile minimal` for faster testing

### Debug Mode

Run tests with detailed logging:

```bash
# Enable verbose output
./scripts/conformance/cite/run-cite-tiles-tests.sh --verbose

# Keep containers running for investigation
./scripts/conformance/cite/run-cite-tiles-tests.sh --no-cleanup

# Interactive mode with manual testing
./scripts/conformance/cite/run-cite-tiles-tests.sh --interactive
```

In interactive mode, access services directly:
- **Honua Server**: http://localhost:8080
- **CITE Team Engine**: http://localhost:8082/teamengine
- **Database**: localhost:5434

### Manual Test Execution

For detailed investigation:

1. Start services in interactive mode:
   ```bash
   ./scripts/conformance/cite/run-cite-tiles-tests.sh --interactive
   ```

2. Access CITE Team Engine at http://localhost:8082/teamengine

3. Select "OGC API Tiles 1.0" test suite

4. Configure test parameters:
   - Service URL: `http://honua-server:8080/ogc/tiles`
   - Select conformance classes to test

5. Execute tests and review results in web interface

## Best Practices

### Local Development

1. **Test Early and Often**: Run CITE tests during development to catch issues early
2. **Use Profiles**: Start with minimal profile for quick feedback, then run full tests
3. **Check Logs**: Always review service logs when tests fail
4. **Incremental Testing**: Fix one conformance class at a time

### CI Integration

1. **Scheduled Validation**: Weekly tests catch conformance regressions
2. **Artifact Preservation**: Test results are preserved for 30 days
3. **Failure Notifications**: Failed tests are reported transparently
4. **Separate from Features**: Tiles and Features CITE tests run independently

### Performance Optimization

1. **Test Data**: Use representative but minimal test datasets
2. **Resource Limits**: Configure appropriate Docker resource limits
3. **Tile Caching**: Ensure tile caching is enabled for performance
4. **Parallel Execution**: Consider running multiple test profiles in parallel

## Conformance Classes Reference

### Core (Required)
- Landing page at `/ogc/tiles`
- API definition at `/ogc/tiles/openapi.json`
- Conformance declaration at `/ogc/tiles/conformance`
- Proper HTTP status codes and headers

### Tileset (Required)
- Tileset metadata structure
- TileMatrixSet references
- Links with proper relations
- Media type declarations

### Tilesets List (Required)
- List of available tilesets
- Proper pagination
- Self and item links

### Dataset Tilesets
- Dataset-level tile access at `/ogc/tiles/tiles`
- Tileset metadata at `/ogc/tiles/tiles/{tileMatrixSetId}`
- Tile retrieval at `/ogc/tiles/tiles/{tileMatrixSetId}/{z}/{row}/{col}`

### Geodata Tilesets
- Collection-level tile access at `/ogc/tiles/collections/{collectionId}/tiles`
- Collection tileset metadata
- Collection tile retrieval

### MVT (Required for Vector Tiles)
- Valid Mapbox Vector Tile format
- Proper Content-Type header
- Geometry and attribute encoding
- Layer structure

### OpenAPI 3.0 (Required)
- Valid OpenAPI 3.0 specification
- All endpoints documented
- Correct parameter definitions
- Response schema validation

## Skipped Tests Documentation

No tests are currently skipped. Both `WebMercatorQuad` and `WorldCRS84Quad` tile matrix sets are supported, and raster (PNG) tile output is available alongside vector (MVT) tiles.

## Resources

- [OGC API Tiles Specification](https://docs.ogc.org/is/20-057/20-057.html)
- [CITE Team Engine Documentation](https://cite.opengeospatial.org/)
- [OGC API Tiles Test Suite](https://github.com/opengeospatial/ets-ogcapi-tiles10)
- [Mapbox Vector Tile Specification](https://github.com/mapbox/vector-tile-spec)
- [Docker Compose Reference](https://docs.docker.com/compose/)

## Support

For CITE testing issues:

1. Check the [troubleshooting section](#troubleshooting)
2. Review workflow logs and artifacts
3. Test locally with `--verbose` and `--interactive` flags
4. Check OGC CITE community resources
5. Create GitHub issue with test results and logs
