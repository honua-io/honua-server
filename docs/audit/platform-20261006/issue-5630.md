# Issue 5630 audit disposition

Re-verification was performed against the current worktree at `7c422ec` before changes. This fix unit deliberately completes the highest-severity finding and one self-contained S2 geometry finding; the remaining S2 findings were re-verified but not attempted rather than changed without focused regression coverage.

| Finding id | Outcome | Evidence |
|---|---|---|
| `SRV-INF-003` | fixed | `SRV_INF_003_ApplyEdits_WithStructuredPayloadOverGenericLimit_ReachesHandler` posts an `adds` form value larger than 8 KiB and proves the edit reaches the handler. `InputValidationMiddleware.IsGeoServicesProtocolPayloadField` now exempts only the structured edit fields and list-valued query fields on GeoServices routes from the generic text-length limit; their protocol payload and feature-count limits remain in force. Observable behavior changes from HTTP 400 to normal protocol handling for these large fields. |
| `SRV-DB-023` | not attempted | Re-verified in `FeatureServerQueryHandler` and `GeometryServiceHandler.HandleProjectAsync`: FeatureServer still resolves the raw SRID pair while GeometryServer validates geodetic bases and conditionally drops the selected transformation for projected endpoints. Deferred because a correct composed projection pipeline requires focused provider coverage. |
| `SRV-GS-006` | not attempted | Re-verified in `GeoServicesSpatialFilterBuilder.NormalizeDistance`: omitted `units` still selects `DistanceUnit.Meters`, and the input SRID is not passed to normalization. |
| `SRV-GS-008` | not attempted | Re-verified in `FeatureServerRequestHandlers.Bins.cs`: analytics handlers still construct their `FeatureQuery` without all allow-listed geometry/time/object-id filters. |
| `SRV-GS-009` | not attempted | Re-verified in `GeometryServiceHandler.HandleProjectAsync`: a selected explicit transformation is still cleared when the transformation base SRIDs differ from projected endpoint SRIDs. Deferred with `SRV-DB-023`. |
| `SRV-GS-013` | not attempted | Re-verified in `FeatureServerRequestHandlers.Maintenance.cs`: `ExecuteAppendAsync` still constructs `ApplyEditsRequest` with `RollbackOnFailure = false` and does not parse or reject upsert controls. |
| `SRV-GS-014` | not attempted | Re-verified in `AttachmentEndpoints.cs`: public route object IDs still flow directly to feature-reader and attachment-store calls without the custom object-ID mapping used by edits. |
| `SRV-GS-019` | fixed | `SRV_GS_019_ConvertPolygon_WithNestedLake_AssignsHoleToSmallestContainingShell` proves a two-level nested lake belongs to the inner shell and the result is valid. Polygon conversion now uses each hole's interior point and selects the smallest covering shell rather than the first covering shell. Observable geometry assembly is corrected for nested rings. |
| ZM point ordinate handling (low severity) | not attempted | Checklist item deferred; higher-severity findings were prioritized. |
| Authorization/rate-limiter ordering (low severity) | not attempted | Checklist item deferred; higher-severity findings were prioritized. |
