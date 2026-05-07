# Release Checklist

Use this checklist for every MVP release.

## Core Release Gates

- [ ] CI green on `trunk`
- [ ] Update the [2026-05 Preview release-train manifest](../../release/honua-2026-05-preview.json)
  with the release-candidate server ref, immutable image tag/digest, latest
  workflow evidence, approved waivers, and bounded follow-up issues for every
  remaining cross-repo release gap
- [ ] Full production audit run completed: `./scripts/conformance/run-production-audit.sh --mode full`
- [ ] Audit artifacts reviewed and attached from `.audit/runs/<timestamp>/summary.md`
- [ ] Conformance workflows pass (OGC Features, OGC Tiles, OGC Maps, WMS, WMTS)
- [ ] MCP certification passes for both transports (`grpc-web`, `rest`) **and** certification artifacts (`mcp-certification-{transport}`) are produced — see [MCP Certification](mcp-certification.md). Skip if SDK-side scripts are not yet landed (CI jobs will show a warning annotation).
- [ ] `MCP_SDK_REF` in `ci.yml` is pinned to a specific tag or commit SHA (not a branch name) for reproducible release evidence.
- [ ] OpenAPI contract governance checks pass
- [ ] Control-plane SDK artifacts generated and attached to release

## Compatibility Contract Updates (Required)

- [ ] Update [MVP Compatibility Contract](../gis/MVP_COMPATIBILITY_CONTRACT.md)
- [ ] Validate [Public Interface Quality Model](public-interface-quality-model.md) and [public-interface-proof.json](../gis/data/public-interface-proof.json) against the shipped runtime surface
- [ ] Refresh [GeoServices REST Parity](../gis/geoservices-rest-parity.md), the service drill-down matrices, and [data/geoservices-rest-parity.json](../gis/data/geoservices-rest-parity.json) when GeoServices routes, parameters, or response shapes changed in the release
- [ ] Execute [Client Templates + Manual Smoke Runbook](../gis/CLIENT_TEMPLATE_RUNBOOK.md)
- [ ] Bump [`docs/developer/sdk-compatibility-versions.json`](../developer/sdk-compatibility-versions.json)
  per the [Server + SDK Compatibility Matrix](../developer/SDK_COMPATIBILITY_MATRIX.md#machine-readable-ci-manifest)
  procedure: add the new server ref and SDK set at the top, keep
  `matrixDepth` entries, refresh `matrix.supported` / `matrix.evaluation`,
  and confirm a manual `sdk-server-compatibility.yml` dispatch produced a
  green `sdk-compatibility-matrix-<run-id>` artifact for the release commit
- [ ] Attach the green SDK compatibility artifact, Real-Client Interop Matrix
  output, Security Nightly output, and any approved waivers to the
  release-train manifest; source-build workflow evidence is not
  release-candidate-image evidence unless the tested image digest is recorded
- [ ] Confirm supported/partial/unsupported protocol notes are current
- [ ] Confirm newly added or removed public query/output formats are reflected in API examples and coverage matrices
- [ ] Confirm replication limitations section reflects runtime behavior

### Cross-Client Certification (Required)

- [ ] Verify the nightly `windows-client-compat-nightly.yml` workflow passes with zero `fail` results in automated `.cert.json` envelopes (FeatureServer, OGC API Features, MapServer, OData)
- [ ] Review automated certification evidence artifacts: `certification/{timestamp}-ci-desktop-*.cert.json` and `certification/{timestamp}-ci-bi-odata.cert.json`
- [ ] Verify the nightly `client-interop-nightly.yml` workflow passes with zero baseline `pass` regressions (including silent `pass`→`skip`/`not-applicable` drops), zero missing-lane envelopes, zero `expected-pairs.json` gaps, and zero new `fail` results in unbaselined cases across the docker/client-compat lanes (`gdal`, `pyqgis`, `openlayers`, `cesium`, `arcgis-stub`); review the committed [`docs/gis/gap-report.md`](../gis/gap-report.md) for any new entries and confirm permanent gaps still match the documented rationale (Cesium vector-feature exclusions, ArcGIS Pro pending licensed runner)
- [ ] Verify the [visual / style certification slice](../gis/visual-style-certification-slice.md) evidence is present on the JS OpenLayers, JS Esri Leaflet, and PyQGIS lanes. The JS collectors seed the full 24-case core and emit every `CERT-RNDR-{SYM,LIN,FIL,LBL,SPR,URL}-01` ID as `pass`, `skip` (with a `pending-fixture` note), or `not-applicable` (with a documented reason). The PyQGIS envelope (`tests/python/pyqgis/conftest.py:CertificationEvidenceCollector`) does not seed unexercised IDs, so confirm only the three recorded slice IDs (`CERT-RNDR-SYM-01`, `CERT-RNDR-LIN-01`, `CERT-RNDR-FIL-01`) are present; `CERT-RNDR-LBL-01`, `CERT-RNDR-SPR-01`, and `CERT-RNDR-URL-01` are tracked in the slice spec's pending-fixture table instead of being seeded into the PyQGIS envelope
- [ ] Confirm all `skip` and `not-applicable` entries have documented reasons (CERT-CONN-02 TLS, CERT-AUTH-01/02 auth, CERT-RNDR-01/02 visual, visual / style slice `pending-fixture` skips)
- [ ] Produce manual client certification evidence per the [Evidence Specification](../gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) for desktop (ArcGIS Pro, QGIS) and BI (Power BI, Excel) lanes
- [ ] Verify all 24 common-core CERT-\* test cases (18 base + 6 visual / style slice) have results for each active client lane (automated + manual)

### Tested Client Versions (Required)

Update from certification workflow outputs and manual validation logs:
- [ ] Update [Client Template Version Matrix](../gis/CLIENT_TEMPLATE_VERSION_MATRIX.md) with exact client versions, run date, and evidence links from `#320`
- [ ] Replace any curated example links in [Client Template Version Matrix](../gis/CLIENT_TEMPLATE_VERSION_MATRIX.md) with the current release artifact URL or release asset URL

| Client | Version tested | Protocol(s) | Result | Notes |
|---|---|---|---|---|
| ArcGIS Pro | _update_ | FeatureServer | _update_ | _update_ |
| ArcGIS Pro | _update_ | MapServer | _update_ | _update_ |
| QGIS | _update_ | OGC API Features | _update_ | _update_ |
| Power BI Desktop | _update_ | OData v4 | _update_ | _update_ |
| Excel | _update_ | OData v4 | _update_ | _update_ |
| MapLibre GL JS ‡ | _update_ | MVT | _update_ | _update_ |

‡ MapLibre GL JS certification is **automated** via the `maplibre-compat` CI job (Playwright + Chromium). Evidence rolls up under the **JS lane** (`client_lane: "js"`) with protocol `"mvt"`. The browser suite (`tests/js-browser/`) covers CERT-CONN-01, CERT-RNDR-01, JS-EXT-01, and JS-EXT-02. See [Certification Matrix — JS Lane Extensions](../gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md#js-lane) for scope.

### Tool Interoperability Evidence (Required)

- [ ] Confirm the tool-lane version capture rules in [Public Interface Quality Model](public-interface-quality-model.md) still match the current implementation
- [ ] Preserve immutable evidence for `Microsoft.OData.Client`, `GDAL/OGR`, and MCP lanes as described in [Public Interface Quality Model](public-interface-quality-model.md)

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
