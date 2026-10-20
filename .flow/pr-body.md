test(imageserver): prove basic ImageServer reads on a seeded mosaic and deny unentitled callers
# Pull Request

## Issue Link
Refs honua-io/honua-release#210: test-evidence ruling A hardening (honua-flow `docs/2026.1-quality-contract.md`, amendment "test-evidence ruling a", 2026-09-04). This is the second Honua.Protocols.GeoServices.Tests slice, after #4360. Part 1, the aggregate quality score, merged in #4357. This is hardening, not a first-release gate.

## Summary
`ImageServerBasicTests` held six endpoint checks that passed without proving anything:
- Five accepted `200` or `404` (one also accepted `204`) and asserted at most the presence of a few JSON properties.
- The service-name dispatch sweep accepted `200`, `204`, `404` or `501` from 32 route calls.

All of them ran on the dev-auth fixture, so every caller was an admin. Most runs also had no rasters seeded, so the "or 404" branch was the one that usually passed.

Each site now has two cases, both on the seeded Issue-522 mosaic: west (x 0..2, value 20), overlap-newest (x 1..3, value 5, newest) and east (x 2..4, value 40), y 0..2, EPSG:4326.
- **Success:** a non-admin principal holding the granted `imagery-reader` role reads actual values back.
- **Denial:** an anonymous caller and a principal holding only `cartography-reader` must each receive the bare GeoServices error envelope (499 / 403), with no raster names, pixel values, href, extent or statistics. The case also checks that nothing changed: the metadata graph revision, every layer-0 raster row (pixel md5) and the persisted layer statistics are identical before and after the denials. The entitled request is then replayed and must still return the same values.

Every `[Endpoint]`/`[Operation]` annotation is kept. Each new case carries the annotations of the route it proves, and the service-name denial case carries the same 32 endpoint annotations as the dispatch case.

## Per-site table

| Site (old → new name) | Old assertion proved | New success case proves | New denial case |
| --- | --- | --- | --- |
| `GetServiceInfo_ValidLayerId_ReturnsServiceMetadata` (name kept), GET `/rest/services/{id}/ImageServer` | Status was 200 or 404. If 200: `serviceDescription` and `capabilities` present, `currentVersion` absent. Admin caller. | 200 with no `error`. `extent` and `fullExtent` are exactly 0,0,4,2 (wkid 4326). Pixel size 1×1, `bandCount` 1, `pixelType` `F32`. Min/max/mean are exactly 5 / 40 / 17.5. No `currentVersion`. | `GetServiceInfo_DeniedPrincipals_DiscloseNothingAndChangeNothing` |
| `ExportImage_WithValidParameters_ReturnsImageOrNotFound` → `ExportImage_WithValidParameters_ReturnsMosaicPixels`, GET `/exportImage` | Status was 200 or 404. If 200: `href`, `width`, `height`, `extent` present. | `width` 8 and `height` 4. `extent` is 0,0,4,2 (wkid 4326). The `href` is fetched as the same principal, returns `image/tiff`, and decodes in PostGIS (`ST_FromGDALRaster`) to 8×4 over 0,0,4,2 with pixels 20 / 5 / 40 at x = 0.25 / 1.75 / 3.75. Neither denied principal can fetch that href (404). | `ExportImage_DeniedPrincipals_DiscloseNothingAndChangeNothing` |
| `ExportImage_PostFormBody_ReturnsImageOrNotFound` → `ExportImage_PostFormBody_ReturnsMosaicPixels`, POST `/exportImage` | Status was 200 or 404. Nothing else. | Same values as the GET case, from the form body. | `ExportImage_PostFormBody_DeniedPrincipals_DiscloseNothingAndChangeNothing` |
| `Identify_PostFormBody_ReturnsIdentifyResultOrNotFound` → `Identify_PostFormBody_ReturnsPixelValueOfCoveringRaster`, POST `/identify` | Status was 200 or 404 for point 0,0. That point sits on the raster corner and identifies as `NoData`. | At 3.5,1: `name` `east`, `value` `"40"`, `Band_1` 40, `HasData` true, location 3.5,1 (wkid 4326). At 0.5,1: `west`, 20. | `Identify_PostFormBody_DeniedPrincipals_DiscloseNothingAndChangeNothing` |
| `GetImageTile_WithValidCoordinates_ReturnsTileOrNotFound` → `GetImageTile_InsideMosaic_RendersCoveringRasterValue`, GET `/tile/{level}/{row}/{col}` | Status was 200, 204 or 404. If 200: content type `image/png`. | Two zoom-10 tiles, each strictly inside one raster (10/510/513 in west, 10/510/521 in east), decode to 256×256 PNGs. Every pixel is opaque gray 20 and gray 40 respectively. | `GetImageTile_DeniedPrincipals_DiscloseNothingAndChangeNothing` |
| `ServiceNameRoutes_DispatchToImageServerSurface` (name kept), 32 `/rest/services/{serviceId}/ImageServer/...` endpoints (18 GET, 14 POST) | Each response was one of 200/204/404/501, over an empty raster layer. | Every name route returns 200 with a non-error, non-empty body, equal to the numeric route on the same request. JSON is compared structurally, and every POST-by-name is compared with GET-by-id. Only the bound publication's identity may differ ("test-layer" versus "0"). Exports are compared by their decoded pixels. Tiles must be byte-identical. KMZ must place its `LatLonBox` at N2 S0 E4 W0. | `ServiceNameRoutes_DeniedPrincipals_DiscloseNothingAndChangeNothing`: all 32 name-route calls, both denied principals, plus the unchanged-state fingerprint and an entitled positive control. |

No site claims the `// EVIDENCE: registration only` exception: every route here is exercised from the test host.

## Changes Made
- `tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/ImageServer/ImageServerBasicTests.cs`:
  - The six sites above become six success cases plus six denial cases.
  - The class now also takes `ImageServerParameterFixture`: dev-auth off, admin bypass off, layer 0 restricted to `imagery-reader`.
  - Untouched cases (zarr identify/export, inline PNG, numeric-leading service id, invalid format, non-existent layer) still use the existing dev-auth fixture.
- `tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/ImageServer/ImageServerParameterValidationTests.cs` (`ImageServerParameterFixture` only):
  - **Old:** the fixture restricted the resource of the first publication at layer index 0. That is the "test" **FeatureServer** resource (`res-layer-0`), not the raster resource.
  - **New:** it also restricts the ImageServer publication's raster resource (`res-image-test`). The fixture now matches its own summary ("restricted raster resource").
  - **Why:** the new service-name denial case found this. With only the feature resource restricted, `/rest/services/test/ImageServer?f=json` served the full service info to a principal without the role.
  - **Impact:** `ImageServerParameterValidationTests` uses only numeric routes and is unaffected (29/29 pass).
- Regenerated `docs/gis/data/feature-catalog.json`, `docs/gis/data/capability-matrix.v1.json`, `docs/okf/capabilities/serve.geoservices-imageserver.md` and `docs/okf/domain-graph.v1.json` with the repo generators, on the tree merged with trunk `405198df5`. The only changes are the renamed and added ImageServer proving-test links and the counts derived from them.
- `.flow/pr-body.md`: this description.

## Found while doing this (filed, not fixed here)
- #6452: the numeric route `/rest/services/{id}/ImageServer` authorizes against the **first publication sharing the storage layer id**, not the raster resource it reads.
  - **Reproduced:** with only `res-image-test` restricted, anonymous and wrong-role callers received service info, identify pixel values (`east`, 40) and tiles from `/rest/services/0/ImageServer`. The service-name route refused them correctly.
  - **This PR:** restricting both resources keeps its assertions truthful. It does not cover #6452; a regression test belongs with the fix.

## Testing
- [x] Integration tests added/updated
- [x] Architecture tests pass (relevant guards, see below)
- [ ] Manual testing performed

Windows SDK on PATH, Release, `-maxcpucount:4`, `TreatWarningsAsErrors=true`, Docker Desktop PostGIS testcontainers:

```
dotnet build tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj -c Release -maxcpucount:4 -p:TreatWarningsAsErrors=true
dotnet test tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ImageServer.ImageServerBasicTests"            # 18 passed
dotnet test tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ImageServer.ImageServerParameterValidationTests"  # 29 passed
HONUA_EMIT_FEATURE_CATALOG=1 dotnet test tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj -c Release --filter "FullyQualifiedName~FeatureCatalogEmitter"
python scripts/ci/generate-capability-matrix.py && python scripts/ci/generate-capability-concepts.py && python scripts/ci/generate-okf-graph.py
dotnet test tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ApiSurfaceCoverageTests|FullyQualifiedName~CapabilityImpactCompletenessTests|FullyQualifiedName~FeatureCatalogDriftTests|FullyQualifiedName~GaSurfaceProvingTestFloorTests|FullyQualifiedName~OperationCoverageTests|FullyQualifiedName~TierTraitEnforcementTests|FullyQualifiedName~TestAttributeEnforcementTests|FullyQualifiedName~GeoServicesParityMatrixDriftTests|FullyQualifiedName~ProtocolHarnessAssignmentDriftTests"  # 46 passed
python scripts/ci/generate-capability-matrix.py --check; python scripts/ci/generate-capability-concepts.py --check; python scripts/ci/generate-okf-graph.py --check; python scripts/ci/generate-llms-txt.py --check; python scripts/ci/check-okf-bundle.py
dotnet format whitespace|style tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj --verify-no-changes --include <the two changed files>
```

On the final squashed head, both classes together passed 47 of 47 (18 + 29), and the architecture guards passed 46 of 46. Two earlier runs on that head failed every case before any assertion ran. The errors were `Failed to connect to Docker endpoint` and Npgsql connection refused/aborted, because Docker Desktop had exited under host load. They were re-run after a Docker Desktop restart.

How the oracles were established: before writing the assertions, a throwaway probe (not committed) recorded what each route returns on the seeded mosaic for the entitled, anonymous and wrong-role principals.

`scripts/ci/pre-pr-check.sh` was not run: it is a bash script, and this lane runs natively on Windows. The equivalent scoped checks are listed above.

## Gate Impact
- [x] PR gates (build, test, governance)
- [ ] Nightly gates (conformance, performance, security)
- [ ] Release gates (packaging, publishing)
- [ ] Deploy gates (promotion, post-apply validation)
- [ ] None — no gate impact

No threshold or gate was weakened. The changed tests stay in the existing `Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer` namespace and `Database.GeoServicesRaster` collection, so their CI shard is unchanged.

## Docs or Contract Impact
- [x] Documentation updated (generated proving-test links only)
- [ ] OpenAPI spec changed
- [ ] Protobuf/gRPC contract changed
- [ ] Control plane SDK surface changed
- [ ] None — no docs or contract impact

## Release/Deploy Impact
- [x] None — standard merge-and-release flow
- [ ] Requires coordinated release across repos
- [ ] Requires database migration
- [ ] Requires infrastructure changes
- [ ] Requires environment variable or secret changes

## Breaking Changes
None. Test-only; no production code changes.

## Follow-ups (not in this PR)
- #6452: fix the numeric ImageServer route's resource binding and add its regression test.
- Remaining ruling-A sites, each a separate per-project PR:
  - `Honua.Server.Tests`:
    - `HealthEndpointsTests` (`NotBe(NotFound)` on live/ready).
    - `Features/Admin/ServiceSettingsEndpointsTests` (four `OK`-or-`NotFound` sites).
  - `Honua.CloudIntegration.Tests`: `CandidateManagedApiKeyAotCertificationTests` (`OK`-or-`NotFound` on rotate).
- Reviewed and left as is, because they already assert a refusal plus no disclosure, or only explain semantics:
  - `ImageServerHttpAuthorizationTests:198` (SOAP denial; Fault present, no metadata).
  - `SensorThingsCoreCrudTests:394` and `RedisOffDirectPublishHostTests:152` (denial-only arms).
  - `ImageServerIdentifyHandlerTests:146` and the two `OgcProcesses*` "not 404" comments (explanatory comments, not assertions).

---

## Pre-PR Checklist
- [ ] Ran `scripts/ci/pre-pr-check.sh` and all checks passed (Windows lane; scoped native equivalents above)
- [x] Commit messages follow conventional format: `type: description (#issue)`
- [x] PR title matches main commit message
- [x] Issue number linked above
- [x] Tests added for new functionality
- [ ] If protocol/auth behavior changed: updated compatibility contract
- [ ] If breaking admin/control-plane API changes: updated migration guide
- [ ] `OPENAPI_BREAKING_CHANGE_APPROVED` — intentional Admin API break acknowledged for this PR
