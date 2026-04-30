# OGC Certification Path

This note is the contributor-facing decision record for Honua Server's OGC
certification path. It distinguishes internal conformance evidence from formal
OGC certification, records the current baseline evidence lanes, and defines the
criteria for reopening a certification submission.

Evidence snapshot: GitHub Actions and retained artifacts were checked on
2026-04-30 UTC. GitHub Actions artifacts listed here currently have 30-day
retention; preserve release or certification evidence outside normal workflow
artifacts before making any public certification claim.

## Decision

Formal OGC certification is deferred.

Honua has useful internal OGC evidence, including passing service-level CITE
runs for OGC API Features 1.0, OGC API Tiles 1.0, and WMS 1.3.0, plus a
passing non-CITE OGC API - Maps integration conformance gate. That evidence is
not enough to proceed with formal OGC certification now because:

- The latest WFS 2.0 and WMTS 1.0 scheduled CITE runs are not clean passes.
- The WCS 2.0.1 harness is present locally but has no GitHub Actions evidence
  run yet, and the documented implementation is intentionally a thin KVP slice.
- KML 2.2, GML 3.2, and GeoPackage 1.2 are format-level validators, not service
  certification evidence, and their latest scheduled runs did not retain
  passing CITE summaries.
- OGC API - Maps is covered by Honua integration conformance tests rather than
  an official CITE lane because the public `ets-ogcapi-maps10` packaging path is
  not stable enough for the same claim.
- Normal GitHub Actions artifacts expire and do not provide durable
  release-grade or audit-grade certification evidence by themselves.

Do not describe Honua, a Honua edition, or any protocol surface as "Certified
OGC Compliant" until the exact product version and implementation standard
revision have passed the OGC Compliance Program review.

## Terminology

- **Service-level CITE** means an OGC TEAM Engine executable test suite exercises
  a service endpoint or API surface.
- **Format-level CITE** means an OGC TEAM Engine executable test suite validates
  a generated file or response document. It is evidence about that encoding, not
  certification of the service API that produced it.
- **Non-CITE OGC conformance** means Honua-owned tests verify advertised OGC
  conformance classes or behavior, but the lane is not an official CITE result.
- **Manual legacy CITE** means a documented TEAM Engine procedure exists, but it
  is not automated in the current GitHub Actions baseline.
- **OGC certification** means OGC has reviewed and approved the candidate
  product/version/standard revision through the OGC Compliance Program.

## Baseline Evidence Matrix

| Surface / standard revision | Evidence type | Suite / profile version | Runner and workflow | Latest artifact location | Status | Known gaps and eligibility note |
|---|---|---|---|---|---|---|
| OGC API Features 1.0 | Service-level CITE | `ogccite/ets-ogcapi-features10:1.9-teamengine-6.0.0-RC2`; workflow profile `default`; local config default is `full` | `scripts/conformance/cite/run-cite-tests.sh`; `.github/workflows/cite-conformance.yml` | [Run 24984363714](https://github.com/honua-io/honua-server/actions/runs/24984363714), 2026-04-27, `trunk`, SHA `d38db2796319c91018a8c43e1014257525306c4f`; artifact `cite-features-conformance-results-56`; summary `cite-results/cite-summary.md` | PASS: 137 total, 137 passed, 0 failed, 0 skipped | Good internal service-level evidence. Still not certification because no OGC submission/review and the retained artifact is not durable release evidence. |
| OGC API Tiles 1.0 | Service-level CITE | `ogccite/ets-ogcapi-tiles10:1.2-teamengine-6.0.0-RC2`; workflow profile `default` | `scripts/conformance/cite/run-cite-tiles-tests.sh`; `.github/workflows/cite-tiles-conformance.yml` | [Run 25041148696](https://github.com/honua-io/honua-server/actions/runs/25041148696), 2026-04-28, `trunk`, SHA `69a965cc43884a4148fee31e394f9e0244453df7`; artifact `cite-tiles-conformance-results-56`; summary `cite-tiles-results/cite-tiles-summary.md` | PASS: 16 total, 16 passed, 0 failed, 0 skipped | Good internal service-level evidence. Still not certification because no OGC submission/review and the retained artifact is not durable release evidence. |
| WFS 2.0 | Service-level CITE | `ogccite/ets-wfs20:latest`; generated TeamEngine params use `ets-wfs20-1.43`; scheduled profile `basic`; on-demand classic wrapper can run WFS 2.0 | `scripts/conformance/cite/run-cite-wfs20-tests.sh`; `.github/workflows/cite-wfs20-conformance.yml`; on-demand wrapper `.github/workflows/cite-classic-conformance.yml` | Latest retained run: [Run 24978795599](https://github.com/honua-io/honua-server/actions/runs/24978795599), 2026-04-27, `trunk`, SHA `d38db2796319c91018a8c43e1014257525306c4f`; artifact `wfs20-cite-results-basic-22`; summary `cite-summary.md`; authoritative result `testng-results.xml`. Earlier retained run: [24650295507](https://github.com/honua-io/honua-server/actions/runs/24650295507), artifact `wfs20-cite-results-basic-21` | PARTIAL/latest workflow failure: TestNG reports 240 total, 174 passed, 28 failed, 38 skipped. The retained markdown summary over-counted skipped tests as failed by reporting 66 failed. | Not certification-ready. Issues #870-#873 fix the concrete capabilities schema, missing-service, `resolve=local`, unknown `RESOURCEID`, datetime lexical-form, temporal-period filter, optional stored-query management advertisement, optional feature-versioning advertisement, and summary-accounting defects found in the latest artifact, but a retained rerun is required. |
| WMS 1.3.0 | Service-level CITE | `ogccite/ets-wms13:1.34-teamengine-6.0.0-RC2`; scheduled profile `default`; classic wrapper default `minimal` | `scripts/conformance/cite/run-cite-wms-tests.sh`; `.github/workflows/cite-wms-conformance.yml`; on-demand wrapper `.github/workflows/cite-classic-conformance.yml` | [Run 25098000468](https://github.com/honua-io/honua-server/actions/runs/25098000468), 2026-04-29, `trunk`, SHA `b650a32152e2f3f0da88683b899a2af05ba3b18f`; artifact `cite-wms-conformance-results-56`; summary `cite-wms-results/cite-wms-summary.md` | PASS: 199 total, 199 passed, 0 failed, 0 skipped | Good internal service-level evidence for the documented GetCapabilities/GetMap scope. Still not certification because no OGC submission/review and the retained artifact is not durable release evidence. |
| WMTS 1.0 | Service-level CITE | `ogccite/ets-wmts10:1.11-teamengine-6.0.0-RC2`; workflow profile `default` | `scripts/conformance/cite/run-cite-wmts-tests.sh`; `.github/workflows/cite-wmts-conformance.yml` | Latest workflow attempt: [Run 25155545936](https://github.com/honua-io/honua-server/actions/runs/25155545936), 2026-04-30, `trunk`, SHA `16deacc8337d7e012cd7c89d2ecda466c9a97d4a`, startup failure with no jobs and no artifacts. Latest retained service evidence: [Run 24824149578](https://github.com/honua-io/honua-server/actions/runs/24824149578), 2026-04-23, `trunk`, SHA `93ada0a560272790fe4127f5a273192d17220144`; artifact `cite-wmts-conformance-results-56`; summary `cite-wmts-results/cite-wmts-summary.md`. Earlier passing retained run: [24498936417](https://github.com/honua-io/honua-server/actions/runs/24498936417), artifact `cite-wmts-conformance-results-55` | NOT CLEAN/latest: run 25155545936 did not start. Latest retained service run failed 4 tests, 60 total, 56 passed, 0 skipped. | Not certification-ready until rerun. Issue #870 repairs the workflow startup permission and corrects the `WebMercatorQuad` `SupportedCRS` URN expected by the GoogleMapsCompatible well-known scale set, which was the concrete failure in the retained artifact. |
| WCS 2.0.1 / WCS 2.0 ETS | Service-level CITE | `ogccite/ets-wcs20:1.22-teamengine-6.0.0-RC2`; generated TeamEngine params use `ets-wcs20-1.22`; default profile `core`; optional `crs`, `extensions`, `full` | `scripts/conformance/cite/run-cite-wcs20-tests.sh`; `.github/workflows/cite-wcs20-conformance.yml` | No GitHub Actions run returned for `.github/workflows/cite-wcs20-conformance.yml` at snapshot time; expected summary path `cite-wcs20-results/cite-wcs20-summary.md` | PENDING EVIDENCE | Harness is present, but it needs a first retained Actions artifact. Current docs identify expected thin-slice limitations: XML POST/SOAP, GML coverage output, WCPS/processing, scaling, interpolation, range subsetting, broad CRS extension coverage, and EO-WCS. Not certification-ready. |
| KML 2.2 | Format-level CITE | `ogccite/ets-kml22:latest`; suite `ets-kml22`; single validation pass; `--profile` accepted only for CLI consistency | `scripts/conformance/cite/run-cite-kml22-tests.sh`; `.github/workflows/cite-kml22-conformance.yml` | Latest: [Run 24873892139](https://github.com/honua-io/honua-server/actions/runs/24873892139), 2026-04-24, `trunk`, SHA `93ada0a560272790fe4127f5a273192d17220144`; artifact `cite-kml22-conformance-results-4`; retained `output.kml` only, no summary | CANCELLED/no retained CITE summary; recent history returned no passing run | Format evidence only. Pin or otherwise record the suite image used, restore a retained passing summary, and do not use it as service certification evidence. |
| GML 3.2 | Format-level CITE | `ogccite/ets-gml32:latest`; suite `ets-gml32`; single validation pass; validates GML emitted by OGC API Features content negotiation | `scripts/conformance/cite/run-cite-gml32-tests.sh`; `.github/workflows/cite-gml32-conformance.yml` | Latest: [Run 24925341250](https://github.com/honua-io/honua-server/actions/runs/24925341250), 2026-04-25, `trunk`, SHA `f2503eb560992fb3e352ffe77a3817fb9eb63aa3`; artifact `cite-gml32-conformance-results-4`; retained `output.gml` only, no summary | CANCELLED/no retained CITE summary; recent history returned no passing run | Format evidence only. Pin or otherwise record the suite image used, restore a retained passing summary, and do not use it as service certification evidence. |
| GeoPackage 1.2 | Format-level CITE | `ogccite/ets-gpkg12:latest`; suite `ets-gpkg12`; single validation pass; validates an exported GeoPackage file | `scripts/conformance/cite/run-cite-gpkg12-tests.sh`; `.github/workflows/cite-gpkg12-conformance.yml` | Latest: [Run 24923191030](https://github.com/honua-io/honua-server/actions/runs/24923191030), 2026-04-25, `trunk`, SHA `f2503eb560992fb3e352ffe77a3817fb9eb63aa3`; artifact `cite-gpkg12-conformance-results-4`; retained `export.gpkg` only, no summary | CANCELLED/no retained CITE summary; recent history returned no passing run | Format evidence only. Pin or otherwise record the suite image used, restore a retained passing summary, and do not use it as service certification evidence. |
| OGC API - Maps | Non-CITE OGC conformance | Honua integration conformance tests for advertised OGC API - Maps classes; no official CITE artifact | `scripts/conformance/ogc/run-ogc-maps-conformance-tests.sh`; `.github/workflows/ogc-maps-conformance.yml` | [Run 24879237091](https://github.com/honua-io/honua-server/actions/runs/24879237091), 2026-04-24, `trunk`, SHA `93ada0a560272790fe4127f5a273192d17220144`; artifact `ogc-maps-conformance-results-22`; summary `ogc-maps-results/ogc-maps-summary.md` | PASS: 36 total, 36 passed, 0 failed, 0 skipped | Useful non-CITE evidence only. Do not use for OGC certification until an official accepted Maps certification path is selected and run. |
| WMS 1.1.1, WFS 1.1.0, WFS 1.0.0 | Manual legacy CITE | Official legacy Basic profiles; no automated workflow lane | Runbook only: `docs/contributor/cite-legacy-ogc-conformance-testing.md` | No retained release evidence linked in this snapshot. Required evidence: TeamEngine session id, XML results, HTML report, capabilities document, and Honua commit SHA | MANUAL PENDING | Endpoint integration tests cover compatibility wire shapes, but the manual CITE Basic evidence is pending. Do not claim legacy CITE certification. |

## Submission Path If Reopened

Use the current OGC Compliance Program and TEAM Engine sources as the authority:

- OGC Compliance Program: <https://www.ogc.org/compliance/>
- OGC Validator / TEAM Engine: <https://cite.ogc.org/te2/>
- Compliance Testing Program Policies and Procedures:
  <https://docs.ogc.org/pol/08-134r11.html>

Before a formal submission, Honua must:

1. Pick a narrow product/version/standard revision scope, such as one edition
   and one service-level standard revision.
2. Run the applicable official TEAM Engine/CITE suite against the exact release
   candidate build and selected conformance classes or profile.
3. Preserve the TeamEngine session id, raw XML or TestNG output, HTML report
   when available, runner logs, captured capabilities document or generated
   file, selected profile, suite image tag and digest when possible, Honua
   commit SHA, product version, edition, and advertised conformance classes.
4. Store that evidence in durable release/certification storage. Do not rely on
   the 30-day GitHub Actions artifact as the only record.
5. Register or update the implementation in the OGC implementation records and
   provide the validation result details required by the Compliance Program.
6. Wait for OGC review and approval before using "Certified OGC Compliant" or
   any compliance mark language, and follow the applicable trademark/licensing
   requirements.

## Re-Entry Criteria

Reopen the decision from "defer" to "go" only when all of these are true for a
specific scoped certification target:

- The target is a service-level CITE lane with a stable official suite version
  or a recorded suite image digest.
- The latest scheduled run and an on-demand release-candidate run are clean
  passes with nonzero tests and retained summaries.
- Any known drift is resolved. For the current baseline this means at least WFS
  2.0 and WMTS 1.0 are clean if they are in scope.
- WCS 2.0.1 has first-run Actions evidence and documented limitations are either
  out of scope for the chosen profile or fixed.
- Format-level validators are treated as supporting evidence only, unless OGC's
  selected certification path for that artifact explicitly requires them.
- Evidence is archived outside normal expiring Actions artifacts.
- Public documentation and release notes continue to distinguish internal
  conformance evidence from OGC certification until OGC approval is complete.

## Related Runbooks

- [CITE OGC API Features](cite-conformance-testing.md)
- [CITE OGC API Tiles](cite-tiles-conformance-testing.md)
- [CITE WFS 2.0](cite-wfs20-conformance-testing.md)
- [CITE WMS 1.3](cite-wms-conformance-testing.md)
- [CITE WMTS 1.0](cite-wmts-conformance-testing.md)
- [CITE WCS 2.0](cite-wcs20-conformance-testing.md)
- [CITE KML 2.2](cite-kml22-conformance-testing.md)
- [CITE GML 3.2](cite-gml32-conformance-testing.md)
- [CITE GeoPackage 1.2](cite-gpkg12-conformance-testing.md)
- [OGC API - Maps Conformance](ogc-maps-conformance-testing.md)
- [Legacy OGC CITE](cite-legacy-ogc-conformance-testing.md)
- [CI Gate Model](../ci/gate-model.md)
- [CI Workflow Inventory](../ci/workflow-inventory.md)
