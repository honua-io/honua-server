# Migration Parity Checklist

Workflow-oriented human checklist that complements the CI-automated parity scorecard
([`parity-scorecard-baseline.json`](../../../tests/Honua.Server.Tests/Import/parity-scorecard-baseline.json)
with 10 service cases x 12 checks). The automated scorecard runs in CI nightly; this
checklist adds pilot-context verification that CI cannot cover.

---

## Protocol Parity Checks

Verify protocol coverage against the canonical matrices for operations in scope.

| Protocol | Coverage Matrix | In-Scope Operations | Status |
|----------|----------------|---------------------|--------|
| GeoServices REST parity landing page | [geoservices-rest-parity.md](../../gis/geoservices-rest-parity.md) | Pilot-critical Esri operations across FeatureServer, MapServer, ImageServer, and Geometry Service | |
| GeoServices REST parity data | [geoservices-rest-parity.json](../../gis/data/geoservices-rest-parity.json) | Machine-readable review of operation status, parameter support, and evidence links | |
| FeatureServer | [feature-server-matrix.md](../../gis/feature-server-matrix.md) | | |
| MapServer | [map-server-matrix.md](../../gis/map-server-matrix.md) | | |
| ImageServer | [image-server-matrix.md](../../gis/image-server-matrix.md) | | |
| Geometry Service | [geometry-service-matrix.md](../../gis/geometry-service-matrix.md) | | |
| OGC API Features | [ogc-api-features-coverage.md](../../gis/specifications/ogc-api-features-coverage.md) | | |
| OData v4 | [odata-v4-coverage.md](../../gis/specifications/odata-v4-coverage.md) | | |

## Automated Scorecard Status

| Field | Value |
|-------|-------|
| Latest nightly run date | |
| Run link / artifact | |
| Latest evidence job ID | |
| Latest evidence report ID | |
| Latest evidence report hash | |
| Evidence generated at | |
| Cutover profile | `pilot` / `production` |
| Readiness state | |
| Warning count | |
| Blocker count | |
| Evidence artifact endpoint | `/api/v1/admin/migrations/reports/{reportId}` |

Before sign-off, queue or refresh the server-generated evidence report and record the transient `jobId`, persisted `reportId`, immutable `reportHash`, `generatedAt`, and the final readiness summary counts. Treat `jobId` as short-lived operational state and use `reportId` plus `reportHash` for the durable audit trail. Use `GET /api/v1/admin/migrations/reports?provider=arcgis-geoservices&cutoverProfile=<profile>&readiness=<state>` for newest-first audit lookup; the summary row already echoes `requestedBy`, `summary`, and provenance refs, so fetch the full artifact by `reportId` only when you need the detailed section payload. If a run needs to be aborted before completion, use either `POST /api/v1/admin/migrations/reports/jobs/{jobId}/cancel` or `POST /api/v1/admin/operations/{jobId}/cancel`; a `409` means the run is already terminal or persisted and any completed artifact should be reviewed as-is.

| Service Case | Pass | Fail | N/A | Notes |
|-------------|------|------|-----|-------|
| | | | | |

## Manual Parity Verification

Items that require human judgment beyond automated checks.

- [ ] Client template smoke tests pass against pilot services
- [ ] Query behavior matches source for pilot-critical queries
- [ ] Styling/renderer fidelity is acceptable for pilot use cases
- [ ] Auth flow works end-to-end for pilot client configuration
- [ ] Immutable migration evidence JSON is attached to the pilot record or audit packet
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
