# Migration Parity Checklist

Workflow-oriented human checklist that complements the CI-automated parity scorecard
([`parity-scorecard-baseline.json`](../../../../tests/dotnet/Honua.Server.Tests/Import/parity-scorecard-baseline.json)
with 10 service cases x 12 checks). The automated scorecard runs in CI nightly; this
checklist adds pilot-context verification that CI cannot cover.

---

## Protocol Parity Checks

Verify protocol coverage against the canonical matrices for operations in scope.

| Protocol | Coverage Matrix | In-Scope Operations | Status |
|----------|----------------|---------------------|--------|
| GeoServices REST parity landing page | [geoservices-rest-parity.md](../../../gis/geoservices-rest-parity.md) | Pilot-critical Esri operations across FeatureServer, MapServer, ImageServer, and Geometry Service | |
| GeoServices REST parity data | [geoservices-rest-parity.json](../../../gis/data/geoservices-rest-parity.json) | Machine-readable review of operation status, parameter support, and evidence links | |
| FeatureServer | [feature-server-matrix.md](../../../gis/feature-server-matrix.md) | | |
| MapServer | [map-server-matrix.md](../../../gis/map-server-matrix.md) | | |
| ImageServer | [image-server-matrix.md](../../../gis/image-server-matrix.md) | | |
| Geometry Service | [geometry-service-matrix.md](../../../gis/geometry-service-matrix.md) | | |
| OGC API Features | [ogc-api-features-coverage.md](../../../gis/specifications/ogc-api-features-coverage.md) | | |
| OData v4 | [odata-v4-coverage.md](../../../gis/specifications/odata-v4-coverage.md) | | |

## Automated Scorecard Status

| Field | Value |
|-------|-------|
| Latest nightly run date | |
| Run link / artifact | |

| Service Case | Pass | Fail | N/A | Notes |
|-------------|------|------|-----|-------|
| | | | | |

## Manual Parity Verification

Items that require human judgment beyond automated checks.

- [ ] Client template smoke tests pass against pilot services
- [ ] Query behavior matches source for pilot-critical queries
- [ ] Styling/renderer fidelity is acceptable for pilot use cases
- [ ] Auth flow works end-to-end for pilot client configuration
- [ ] CRS handling is correct for all pilot spatial references
- [ ] Error response shape matches expected client handling
- [ ] Pagination behavior is consistent with source for pilot data volumes

## Gap Assessment

### Tier C Operations Encountered

| Operation | Protocol | Impact | Workaround |
|-----------|----------|--------|------------|
| | | | |

### Deferred Items

| Item | Reason | Target Phase |
|------|--------|-------------|
| | | |

### Documented Workarounds

| Workaround | Affected Operations | Temporary / Permanent |
|------------|--------------------|-----------------------|
| | | |

## Sign-Off

| Field | Value |
|-------|-------|
| Parity status | Sufficient / Insufficient / Conditional |
| Conditions (if conditional) | |
| Reviewer | |
| Date | |
