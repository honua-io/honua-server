# Release Checklist

Use this checklist for every MVP release.

## Core Release Gates

- [ ] CI green on `trunk`
- [ ] Full production audit run completed: `./scripts/run-production-audit.sh --mode full`
- [ ] Audit artifacts reviewed and attached from `.audit/runs/<timestamp>/summary.md`
- [ ] Conformance workflows pass (OGC Features, OGC Tiles, WMS, WMTS)
- [ ] MCP certification passes for both transports (`grpc-web`, `rest`) **and** certification artifacts (`mcp-certification-{transport}`) are produced — see [MCP Certification](mcp-certification.md). Skip if SDK-side scripts are not yet landed (CI jobs will show a warning annotation).
- [ ] `MCP_SDK_REF` in `ci.yml` is pinned to a specific tag or commit SHA (not a branch name) for reproducible release evidence.
- [ ] OpenAPI contract governance checks pass
- [ ] Control-plane SDK artifacts generated and attached to release

## Benchmark Proof Pack (Required)

- [ ] Refresh benchmark proof pack if stale (>2 minor releases behind) — see [Benchmark Publication Process](BENCHMARK_PUBLICATION_PROCESS.md)
- [ ] Verify `performance-baseline.json` matches the current release
- [ ] Verify `docs/operator/BENCHMARK_RESULTS.md` environment disclosure is current

## Compatibility Contract Updates (Required)

- [ ] Update [MVP Compatibility Contract](../gis/MVP_COMPATIBILITY_CONTRACT.md)
- [ ] Refresh [GeoServices REST Parity](../gis/geoservices-rest-parity.md), the service drill-down matrices, and [data/geoservices-rest-parity.json](../gis/data/geoservices-rest-parity.json) when GeoServices routes, parameters, or response shapes changed in the release
- [ ] Execute [Client Templates + Manual Smoke Runbook](../gis/CLIENT_TEMPLATE_RUNBOOK.md)
- [ ] Confirm supported/partial/unsupported protocol notes are current
- [ ] Confirm newly added or removed public query/output formats are reflected in API examples and coverage matrices
- [ ] Confirm replication limitations section reflects runtime behavior

### Cross-Client Certification (Required)

- [ ] Produce cross-client certification evidence per the [Evidence Specification](../gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md)
- [ ] Verify all common-core CERT-\* test cases have results for each active client lane

### Tested Client Versions (Required)

Update from certification workflow outputs and manual validation logs:
- [ ] Update [Client Template Version Matrix](../gis/CLIENT_TEMPLATE_VERSION_MATRIX.md) with exact client versions, run date, and evidence links from `#320`

| Client | Version tested | Protocol(s) | Result | Notes |
|---|---|---|---|---|
| ArcGIS Pro | _update_ | FeatureServer | _update_ | _update_ |
| ArcGIS Pro | _update_ | MapServer | _update_ | _update_ |
| QGIS | _update_ | OGC API Features | _update_ | _update_ |
| Power BI Desktop | _update_ | OData v4 | _update_ | _update_ |
| Excel | _update_ | OData v4 | _update_ | _update_ |
| MapLibre GL JS ‡ | _update_ | MVT | _update_ | _update_ |

‡ MapLibre GL JS certification is currently **manual** (visual browser-based verification). Evidence rolls up under the **JS lane** (`client_lane: "js"`) with protocol `"mvt"`. The existing Vitest suite does not yet include MVT tests; automated coverage is tracked as a follow-up. See [Certification Matrix — JS Lane Extensions](../gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md#js-lane) for JS-EXT-01/JS-EXT-02 scope.

### Known Caveats and Workarounds (Required)

| Area | Caveat | Workaround | Linked issue |
|---|---|---|---|
| _update_ | _update_ | _update_ | _update_ |

- [ ] Publish caveats/workarounds in release notes.
- [ ] Ensure caveats/workarounds are reflected in user-facing docs where applicable.

## Auth and Security

- [ ] Authentication behavior (API key/OIDC/Basic compatibility) verified against docs
- [ ] Security configuration docs updated for any auth change

## Sign-off

- [ ] Release owner approval
- [ ] Engineering approval
- [ ] Documentation approval
