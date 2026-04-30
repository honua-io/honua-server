# WFS 2.0 CITE Conformance Testing Guide

This document describes how to run OGC Compliance and Interoperability Testing & Evaluation (CITE) suite tests against Honua Server to validate WFS 2.0 conformance.

> WFS 1.1.0 and WFS 1.0.0 read-only compatibility is tracked separately in the [Legacy OGC CITE Guide](cite-legacy-ogc-conformance-testing.md). Those Basic profiles run as a documented manual procedure rather than a PR gate.

## Overview

The CITE test suite validates that Honua Server correctly implements the OGC WFS 2.0 specification. This includes:

- **Basic WFS**: Core WFS 2.0 functionality
- **Transactional WFS**: Insert, Update, Delete operations
- **Filter Encoding 2.0**: Query filtering capabilities
- **XML/GML Encoding**: Proper XML and GML output
- **KVP Encoding**: Key-Value Pair URL parameter support

## Quick Start

### Prerequisites

- Docker and Docker Compose
- Git (for version information)
- curl and xmllint (for endpoint verification)

### Running WFS 2.0 CITE Tests Locally

```bash
# Run all WFS 2.0 conformance tests
./scripts/conformance/cite/run-cite-wfs20-tests.sh

# Run with specific options
./scripts/conformance/cite/run-cite-wfs20-tests.sh --verbose --no-cleanup

# Interactive mode (keep services running for manual testing)
./scripts/conformance/cite/run-cite-wfs20-tests.sh --interactive
```

### Running in CI

WFS 2.0 CITE tests run automatically:
- Weekly via scheduled workflow
- Manually via workflow dispatch

The workflow is a nightly-tier conformance lane. Pull request and push triggers
were removed in #485 because TEAM Engine suites are external and heavyweight.

## Test Profiles

### Basic Profile (default)
Core WFS 2.0 conformance testing:
- GetCapabilities operation
- DescribeFeatureType operation
- GetFeature operation
- Basic filtering support
- XML/GML output validation

### Transactional Profile
Extended testing including WFS-T operations:
- Transaction operations (Insert/Update/Delete)
- Locking capabilities
- Transaction rollback handling

### Full Profile
Comprehensive testing including optional features:
- All Basic and Transactional tests
- Advanced filtering (FES 2.0)
- Multiple output formats
- Spatial and temporal operations
- Extended WFS operations

## Configuration

### Test Parameters

WFS 2.0 suite metadata lives in `docker/cite/wfs20/config/wfs20-test-config.xml`. The runner generates the effective TeamEngine `test-params.xml` for the selected profile inside `cite-wfs20-results/` during each run, using the container endpoint `http://honua-server:8080/wfs`.

### Docker Compose Services

The test environment includes:

1. **honua-server**: The main application under test
2. **postgres**: PostGIS database backend
3. **cite-teamengine**: CITE Team Engine web interface
4. **cite-runner**: Automated test execution

## Understanding Results

### Test Output Structure

```
cite-wfs20-results/
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
- WFS operation details
- Request/response validation
- Filter parsing results

#### HTML Report (`test-report.html`)
- Interactive web-based results
- Visual test status indicators
- Linked WFS documentation
- Error details with context

### Success Criteria

For WFS 2.0 conformance runs:
- **Basic WFS**: 100% pass rate (required)
- **XML Encoding**: 100% pass rate (required)
- **KVP Encoding**: 100% pass rate (required)
- **Filter Encoding**: 90%+ pass rate (advanced filters optional)
- **Transactional WFS**: 95%+ pass rate (if enabled)

### 2026-04-30 Failure Triage

The latest retained scheduled artifact at triage time was GitHub Actions run
`24978795599` (`wfs20-cite-results-basic-22`), which reported 240 total tests,
174 passed, 28 failed, and 38 skipped in `testng-results.xml`. The retained
markdown summary incorrectly counted skipped tests as failures and reported 66
failed. Issue #870 fixes the bounded service behavior defects identified
directly from that artifact:

- `GetCapabilities` without `SERVICE=WFS` now returns a WFS exception report
  instead of a successful capabilities document.
- `GetFeature` and `GetPropertyValue` advertise and accept `resolve=local` as a
  local no-op alongside `resolve=none`.
- WFS capabilities declare the `xsd` namespace and advertise only FES 2.0
  schema-valid temporal operator names.
- WFS capabilities no longer advertise the non-schema `EnvelopeIntersects`
  spatial operator, while the parser can still accept it from existing clients.
- Unknown unqualified `RESOURCEID` values now return an empty feature collection
  instead of a malformed-identifier exception.
- DateTime attributes stored as JSON strings are emitted in the XML Schema
  offset lexical form used elsewhere by WFS XML output.
- Temporal filters with `gml:TimePeriod` operands accept UTC `Z` instants for
  `After` and `Before` request predicates.
- Stored-query management is truthfully de-advertised: Honua supports
  `ListStoredQueries`, `DescribeStoredQueries`, and the mandatory
  `GetFeatureById` stored query, but does not advertise `CreateStoredQuery`,
  `DropStoredQuery`, or `ManageStoredQueries`.
- Feature versioning is truthfully de-advertised through
  `ImplementsFeatureVersioning=FALSE` and `ImplementsVersionNav=FALSE`, while
  transactional Insert/Update/Delete behavior remains advertised through
  `ImplementsTransactionalWFS=TRUE`.
- Disabled WFS/FES optional conformance constraints no longer list `TRUE` in
  `ows:AllowedValues`, matching how the ETS decides whether optional suites are
  implemented.
- The WFS CITE summary parser now records skipped tests separately from failed
  tests.

The WFS 2.0 lane is still not certification-ready until a rerun proves the fixed
groups are cleared. This PR scope closes the bounded follow-ups #871, #872, and
#873 by de-advertising unsupported optional behavior and adding regressions for
the temporal-period request path.

## Troubleshooting

### Common Issues

#### 1. Service Health Check Failures

**Symptoms**: Tests fail before execution begins
```bash
❌ Timeout waiting for Honua Server to become healthy
```

**Solutions**:
- Check Docker container logs: `docker compose -f docker/cite/wfs20/compose.yml logs honua-server`
- Verify database connectivity
- Ensure port 8080 is available
- Check that WFS 2.0 endpoints are enabled

#### 2. WFS Endpoint Accessibility Issues

**Symptoms**: WFS endpoints return 404 or connection refused
```bash
❌ GetCapabilities endpoint not accessible
❌ WFS service not responding
```

**Solutions**:
- Verify WFS 2.0 endpoints are implemented and registered
- Check endpoint routing configuration
- Validate database contains test feature types
- Review application logs for WFS-specific errors

#### 3. XML/GML Validation Failures

**Symptoms**: XML structure validation fails
```bash
⚠️ Invalid XML structure in GetCapabilities response
⚠️ GML schema validation failed
```

**Solutions**:
- Verify XML namespace declarations
- Check GML geometry serialization
- Validate against WFS 2.0 schema
- Ensure proper XML encoding (UTF-8)

#### 4. Filter Encoding Issues

**Symptoms**: FES 2.0 filter parsing fails
```bash
⚠️ Filter parsing failed for spatial query
⚠️ Unsupported filter operator
```

**Solutions**:
- Review FES 2.0 parser implementation
- Check spatial operation support
- Validate filter-to-SQL translation
- Test with simple filters first

#### 5. Feature Type Schema Issues

**Symptoms**: DescribeFeatureType operation fails
```bash
⚠️ Invalid schema for feature type
⚠️ Missing feature type definition
```

**Solutions**:
- Verify feature types are registered in layer catalog
- Check XML Schema (XSD) generation
- Validate geometry and attribute definitions
- Ensure proper namespace handling

### Debug Mode

Run tests with detailed logging:

```bash
# Enable verbose output
./scripts/conformance/cite/run-cite-wfs20-tests.sh --verbose

# Keep containers running for investigation
./scripts/conformance/cite/run-cite-wfs20-tests.sh --no-cleanup

# Interactive mode with manual testing
./scripts/conformance/cite/run-cite-wfs20-tests.sh --interactive
```

In interactive mode, access services directly:
- **Honua Server**: http://localhost:8080
- **WFS GetCapabilities**: http://localhost:8080/wfs?service=WFS&version=2.0.0&request=GetCapabilities
- **CITE Team Engine**: http://localhost:8081/teamengine
- **Database**: localhost:5433

### Manual Test Execution

For detailed investigation:

1. Start services in interactive mode:
   ```bash
   ./scripts/conformance/cite/run-cite-wfs20-tests.sh --interactive
   ```

2. Access CITE Team Engine at http://localhost:8081/teamengine

3. Select "OGC WFS 2.0" test suite

4. Configure test parameters:
   - Service URL: `http://honua-server:8080/wfs`
   - Select conformance classes to test
   - Choose test profile (basic/transactional/full)

5. Execute tests and review results in web interface

## WFS 2.0 Operations Reference

### Required Operations

#### GetCapabilities
- **URL**: `/wfs?service=WFS&version=2.0.0&request=GetCapabilities`
- **Purpose**: Service metadata and feature type listing
- **Response**: XML capabilities document

#### DescribeFeatureType
- **URL**: `/wfs?service=WFS&version=2.0.0&request=DescribeFeatureType&typeNames=<type>`
- **Purpose**: XML schema for feature types
- **Response**: XML Schema (XSD) definition

#### GetFeature
- **URL**: `/wfs?service=WFS&version=2.0.0&request=GetFeature&typeNames=<type>`
- **Purpose**: Feature data retrieval
- **Response**: GML feature collection or other formats

### Optional Operations

#### Transaction (WFS-T)
- **URL**: `/wfs` (POST with XML body)
- **Purpose**: Feature data modification
- **Operations**: Insert, Update, Delete

#### GetPropertyValue
- **URL**: `/wfs?service=WFS&version=2.0.0&request=GetPropertyValue&typeNames=<type>&propertyName=<prop>`
- **Purpose**: Retrieve specific property values
- **Response**: XML value collection

## Conformance Classes Reference

### Basic WFS (Required)
- GetCapabilities operation
- DescribeFeatureType operation
- GetFeature operation
- KVP encoding support
- XML/GML output formats

### Transactional WFS (Optional)
- Transaction operation support
- Insert/Update/Delete capabilities
- Lock feature support
- Transaction result reporting

### Filter Encoding 2.0 (Optional)
- Comparison operators
- Logical operators (AND, OR, NOT)
- Spatial operators (BBOX, Intersects, etc.)
- Sorting capabilities
- Resource identification

### Advanced Features (Optional)
- Multiple output formats (GeoJSON, CSV)
- Coordinate reference system support
- Paging and result limiting
- Extended query capabilities

## Resources

- [OGC WFS 2.0 Specification](https://docs.ogc.org/is/09-025r2/09-025r2.html)
- [OGC Filter Encoding 2.0 Specification](https://docs.ogc.org/is/09-026r2/09-026r2.html)
- [CITE Team Engine Documentation](https://cite.opengeospatial.org/)
- [OGC WFS 2.0 Test Suite](https://github.com/opengeospatial/ets-wfs20)
- [GML 3.2 Documentation](https://www.ogc.org/standard/gml/)

## Support

For WFS 2.0 CITE testing issues:

1. Check the [troubleshooting section](#troubleshooting)
2. Review workflow logs and artifacts
3. Test locally with `--verbose` and `--interactive` flags
4. Check OGC WFS community resources
5. Validate against WFS 2.0 specification
6. Create GitHub issue with test results and logs
