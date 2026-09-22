# Catalog authority and typed GP defaults on the 2026.1 candidate 548b7a5 (server#4783, #4634)

Date: 2026-09-15. Candidate: `ghcr.io/honua-io/honua-server@sha256:29974ee7b722e3ae15c3b891024e5e70800f412188aeccf5ec3d32d9dac675c1`, revision `548b7a5263da5a3f2381eb43f232687cdf92b0bf` (honua-release PR #349). The pin contains every server change both issues depend on:

| PR | Change | Issue |
|---|---|---|
| #4823 | catalog and service metadata authorization | #4783 |
| #4855 | GeoServices operations through the grant-then-policy seam (merge `a29728734f`) | #4783 |
| #4615 | typed GPServer scalar defaults, explicit null keys, typed SOAP discovery defaults | #4634 |
| #4801 | endpoint regressions for false/true, numeric, text and absent defaults | #4634 |
| #4760 | installed-SDK receipt harness | #4634 |

Each merge commit was checked with `git merge-base --is-ancestor <merge> 548b7a5263da5a3f2381eb43f232687cdf92b0bf`.

## server#4783: catalog visibility agrees with service and task access

### Replay

`catalog-authority-548b7a5-2026-09-15-replay.sh` boots the digest with PostGIS 16-3.4, Redis, a throwaway key-ring PKCS#12 and `Licensing__Mode=Disabled`. It seeds the authz fixture the issue was reported on: honua-esri-compat `docker/seed/arcgis-compat.sql` at `a235e3e` (sha256 `d48e8b6b…`), compiled to Metadata v2 with the pin's own `tests/seed/base-schema.sql` helper. It then runs `catalog-authority-548b7a5-2026-09-15-probe.py` and tears everything down.

| Service | Seeded policy |
|---|---|
| `arcgis_compat_protected` | `allowedRoles=["admin"]` |
| `arcgis_compat_scoped` | `allowedRoles=["scoped-api-key"]` |
| `browser_compat` | anonymous |

Principals, with the role the pinned `ApiKeyAuthenticationHandler` assigns. Every key is minted through `POST /api/v1/admin/api-keys/` and revoked at the end:

| Principal | Credential | Role | Expected visible fixture services |
|---|---|---|---|
| `bootstrap` | admin password (`X-API-Key`) | admin | all three |
| `admin-key` | `admin:*` | admin | all three |
| `role-matched-key` | `read:arcgis_compat_scoped` | `scoped-api-key` | scoped, browser |
| `second-scoped-key` | `read:browser_compat` | `scoped-api-key` | scoped, browser |
| `non-matching-key` | `admin:read` | `scoped-admin-key` | browser |
| `anonymous` | none | — | browser |

The expectations come from the seeded policies and the handler's role assignment, not from observed output.

For every principal the probe:

1. reads `GET /rest/services?f=json` and SOAP `POST /services` `GetServiceDescriptions`, and requires identical `(Name, Type, RestUrl)` sets;
2. requires the visible fixture services to equal the expectation;
3. on each of the three services opens 17 routes. A listed service must answer each with a non-error JSON document; an unlisted one must refuse each with Esri 403 (authenticated) or 499 (anonymous) and no layer name in the body:
   - FeatureServer: service, layer, layer `query`, service `query`, `queryDomains`
   - MapServer: service, layer, `export`, `identify`, `find`, `legend`, `layers`, `queryDomains`, layer `query`
   - GPServer: service, task `geometry.buffer`, Esri alias `Buffer`
4. posts `geometry.buffer/submitJob`. Execution is `OperatorOperation.Execute`, a separate authority from the metadata the catalog hands off, so only full-admin principals are expected to get a `jobId`; other keys expect 403 and anonymous 499.

### Result: 348/348 rows passed

Transcript: `catalog-authority-548b7a5-2026-09-15-transcript.json`. It records the running image ID and revision label (both asserted equal to the pin before probing) and carries no credential.

| Principal | Listed fixture services | protected (17 routes) | scoped (17 routes) | browser (17 routes) | submitJob ×3 |
|---|---|---|---|---|---|
| bootstrap | protected, scoped, browser | all open | all open | all open | `jobId` |
| admin-key | protected, scoped, browser | all open | all open | all open | `jobId` |
| role-matched-key | scoped, browser | all 403 | all open | all open | 403 |
| second-scoped-key | scoped, browser | all 403 | all open | all open | 403 |
| non-matching-key | browser | all 403 | all 403 | all open | 403 |
| anonymous | browser | all 499 | all 499 | all open | 499 |

REST/SOAP catalog parity held for all six principals. Every key revoked with status `revoked`, and each revoked key was then refused on `arcgis_compat_scoped/FeatureServer`.

This is the defect from the issue, inverted. On `7ba4226` admin saw `arcgis_compat_scoped` in both catalogs, but FeatureServer and MapServer refused it with 403. On the candidate, admin opens every route on every service it is shown. Every other principal is refused on exactly the services the catalogs omit for it.

Observation, not a defect of this issue: `allowedRoles` names a role. Every scoped non-admin API key authenticates as `scoped-api-key`, whatever service its permission names. So `second-scoped-key` (`read:browser_compat`) opens `arcgis_compat_scoped`, exactly as the catalogs advertise to it. A policy that must admit only one key has to name something narrower than the shared role.

Rerun for a later pin: `HONUA_SERVER_IMAGE=<ref@digest> HONUA_SERVER_REVISION=<sha> ESRI_COMPAT_REF=<commit> ./catalog-authority-548b7a5-2026-09-15-replay.sh <transcript.json>`. It takes about 3 minutes, on port 18780 by default (`REPLAY_PORT`).

### Other evidence on the same digest

- **SOAP-CAT-EXT-04:** honua-esri-compat#101 license-free run [34914670765](https://github.com/honua-io/honua-esri-compat/actions/runs/34914670765) reports `C/-OP-SOAP-CATALOG-REST-HANDOFF` passing: "all 13 advertised REST handoff URL(s) resolved". Its `logs/soap-catalog.json` shows 13 authenticated handoffs, all 200, with 7 services for the anonymous control. #4744, which the criterion also waited on, closed on 2026-09-13.
- **In-repo regressions (on trunk):**
  - `GeoservicesSoapCatalogDiscoveryTests.RestCatalog_AdminAndRoleOperationHandoffsAgreeWithCatalogVisibility` (#4855): 22 operation handoffs for admin, role-matched, wrong-role and anonymous callers.
  - The #4823 metadata handoff tests.

## server#4634: typed scalar defaults and installed Esri SDK import on the candidate

### Installed ArcGIS API for Python and ArcPy replay

honua-esri-compat `gp-toolbox-replay.yml` on trunk, [run 34915379462](https://github.com/honua-io/honua-esri-compat/actions/runs/34915379462) (2026-09-15, success, workflow commit `78bd077c`). It ran on the licensed Windows runner against an owned fixture whose container image the workflow asserts equal to `sha256:29974ee7…`. Artifact `focused-gp-replay` (id 10375769912, digest `sha256:6e49e698…`) is retained here as `gp-toolbox-replay-548b7a5-2026-09-15-licensed-execution.json`.

| Receipt field | Value |
|---|---|
| `source` / `image` | `548b7a5263…` / `sha256:29974ee7…` |
| `tls_verified` | `true` |
| `arcgis` / `arcpy` | `2.4.3` / `3.7.1` |
| `sdk_import` | passed, `advertised_task_count` 119 |
| `sdk_remote_area` | passed; decoded `MeasureResult` `value` 12 for a 3 × 4 rectangle (EPSG:3857) the oracle encodes from the OGC WKB byte layout |
| `arcpy_import` | passed |
| `arcpy_remote_area` | passed, `job_status` 4 `Completed`, decoded `value` 12 |

The `Windows fatal exception: code 0xe0000001` stacks in the job log come from ArcPy's own import path (`arcpy/geoprocessing/_base.py`, reached through `arcgis._impl._geometry_engine`). Python's `faulthandler` prints them for first-chance exceptions that ArcPy handles itself. The run completed and wrote the passing receipt.

### Typed defaults across the full callable catalog

`gp-typed-defaults-548b7a5-2026-09-15-catalog.json` records one read-only pass over `GET /rest/services/browser_compat/GPServer/{task}?f=json` for all 119 tasks on the same pinned container (revision and image asserted). Rule: GPBoolean → JSON boolean, GPLong → JSON integer, GPDouble → JSON number, GPString → JSON string, absent → explicit `null`.

- Parameters without a `defaultValue` key: **0**.
- Mistyped defaults: **0**.
- Kinds observed:
  - GPBoolean: 10 `bool`.
  - GPLong: 13 `int`, 43 `null`.
  - GPDouble: 19 numbers, 57 `null`.
  - GPString: 39 `str`, 322 `null`.
  - Every record set, raster and multi-value default: `null`.
- The defaults named in the issue: `geometry.buffer/geodesic` = `false` and `geometry.simplify/preserveTopology` = `true`, both under the hex task name and the Esri alias. Also recorded: `enrichment.enrich/maxInputFeatures` = `250000`, `surface.slope/zFactor` = `1`.

Canonical/OGC textual defaults under fr-FR (`GPServerDefaultValueTests`), typed SOAP discovery defaults (`GPServerSoapEndpointsTests`) and the endpoint regressions (`GPServerEsriTaskAliasEndpointTests`) are in-repo tests from #4615, #4760 and #4801.

## Disposition

**server#4783**, all three acceptance criteria met:

| Criterion | Evidence |
|---|---|
| One admin authority rule for role-restricted services across the REST catalog, the SOAP catalog and the FeatureServer, MapServer and GPServer service and task routes | #4823 + #4855, confirmed on the candidate by the 348/348 replay |
| HTTP test pinning catalog visibility and service/task access for admin, role-matched, non-matching and anonymous callers | `RestCatalog_AdminAndRoleOperationHandoffsAgreeWithCatalogVisibility` (#4855), plus this replay on the certified image |
| honua-esri-compat#101 `SOAP-CAT-EXT-04` passes at the next certified image, with #4744 fixed | run 34914670765 on `sha256:29974ee7…`; #4744 closed |

**server#4634**, all five acceptance criteria met:

| Criterion | Evidence |
|---|---|
| Correctly typed scalar defaults across the full callable catalog, explicit null keys kept | #4615; 119-task catalog pass on the candidate, 0 missing keys, 0 mistyped |
| Canonical/OGC defaults and request semantics preserved | #4615 `GPServerDefaultValueTests` |
| Typed SOAP discovery defaults without inferring SOAP execution scope | #4615/#4760 `GPServerSoapEndpointsTests` |
| Endpoint regressions for false/true, numeric, text and absent defaults, AOT-safe | #4801 |
| Full toolbox import and decoded remote output with the installed Esri SDK on the actual corrected candidate | run 34915379462 on `sha256:29974ee7…`: SDK 2.4.3 imports 119 tasks, ArcPy imports, both decode area 12 |
