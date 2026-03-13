# Release Checklist

Use this checklist for every MVP release.

## Core Release Gates

- [ ] CI green on `trunk`
- [ ] Full production audit run completed: `./scripts/run-production-audit.sh --mode full`
- [ ] Audit artifacts reviewed and attached from `.audit/runs/<timestamp>/summary.md`
- [ ] Conformance workflows pass (OGC Features, OGC Tiles, WMS, WMTS)
- [ ] OpenAPI contract governance checks pass
- [ ] Control-plane SDK artifacts generated and attached to release

## Compatibility Contract Updates (Required)

- [ ] Update [MVP Compatibility Contract](../user/MVP_COMPATIBILITY_CONTRACT.md)
- [ ] Execute [Client Templates + Manual Smoke Runbook](../user/CLIENT_TEMPLATE_RUNBOOK.md)
- [ ] Confirm supported/partial/unsupported protocol notes are current
- [ ] Confirm newly added or removed public query/output formats are reflected in API examples and coverage matrices
- [ ] Confirm replication limitations section reflects runtime behavior

### Tested Client Versions (Required)

Update from certification workflow outputs and manual validation logs:
- [ ] Update [Client Template Version Matrix](../user/CLIENT_TEMPLATE_VERSION_MATRIX.md) with exact client versions, run date, and evidence links from `#320`

| Client | Version tested | Protocol(s) | Result | Notes |
|---|---|---|---|---|
| ArcGIS Pro | _update_ | FeatureServer / MapServer | _update_ | _update_ |
| QGIS | _update_ | OGC API Features / OGC API Tiles | _update_ | _update_ |
| Power BI Desktop | _update_ | OData v4 | _update_ | _update_ |
| Excel | _update_ | OData v4 | _update_ | _update_ |
| MapLibre GL JS | _update_ | MVT / Tiles | _update_ | _update_ |

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
