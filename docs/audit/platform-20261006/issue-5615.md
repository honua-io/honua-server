# Issue 5615 audit disposition

Re-verification was performed against the current worktree. Work stopped after the first three S1 findings were fixed and tested so that the remaining findings would not be changed shallowly.

| Finding id | Outcome | Evidence |
|---|---|---|
| `SRV-DB-003` | fixed | `SRV_DB_003_FixedBoundariesOverLimit_ReturnsValidationError` proves that more than 1,000 boundaries is rejected. `FeatureServerRequestHandlers.Bins.cs:425-446` also rejects non-finite, duplicate, and unsorted values before SQL construction. This changes invalid-request behavior to a protocol validation error. |
| `SRV-GS-001` | not attempted | Re-verified at `FeatureServerQueryHandler.cs:856-895` and `FeatureServerQueryHandler.cs:1857`: PBF and service-level ID responses still retain materializing paths. Deferred after completing the preceding finding in severity order. |
| `SRV-GS-002` | fixed | `SRV_GS_002_Densify_CumulativeRequestVertexBudget_Returns400WithoutOom` proves two individually valid geometries exceeding the aggregate budget are rejected. `GeometryServiceHandler.cs:2837-2863` preflights the entire request before densifying any geometry. This changes over-budget requests to a protocol 400 response. |
| `SRV-GS-003` | fixed | `SRV_GS_003_TrueCurveConversion_EnforcesVertexBudget` proves WKB conversion rejects curve expansion over its 50,000-vertex budget. `GeoServicesGeometryConverter.cs:154-165` now uses the budgeted curve densifier. This changes over-budget geometry conversion to an `ArgumentException`, which protocol handlers map to invalid input. |
| `SRV-INF-005` | not attempted | Re-verified at `PgRoutingProvider.cs:421`: closest-facility processing still enters the per-incident solve implementation without an aggregate solve-pair control. Deferred after the preceding S1 findings. |
| `SRV-OGC-006` | not attempted | Re-verified at `ODataBatchOperationHandler.cs:28` and `ODataBatchOperationHandler.cs:441`: the only top-level operation cap remains 1,000, with no read-specific budget. Deferred after the preceding S1 findings. |
| `SRV-INF-008` | not attempted | Re-verified at `LimitsEnforcementMiddleware.cs:139`: request size selection still uses request-path matching rather than authenticated endpoint metadata. S2 work was not started while S1 findings remained. |
| `SRV-OGC-010` | not attempted | Re-verified at `WmsRequestHandlers.cs:379`: requested WMS layer resolution still has no request layer cap or duplicate elimination. S2 work was not started while S1 findings remained. |
| `SRV-OGC-011` | not attempted | Re-verified at `ODataStreamingQueryHandler.Delta.cs:68`: durable delta handling still has no caller or storage quota at its entry point. S2 work was not started while S1 findings remained. |
| `SRV-AI-002` | not attempted | Re-verified at `McpEndpointExtensions.cs:506`: bounded body reading still sizes its initial `MemoryStream` from the declared content length. S2 work was not started while S1 findings remained. |
| `SRV-AI-001` | not attempted | Re-verified at `RenderMapTool.cs:102-105`: arrays are allocated and iterated directly from the uncapped layer count. Post-cut S2 work was not started while must-fix S1 findings remained. |
| `SRV-OGC-009` | not attempted | Re-verified at `Wfs20Handler.Transaction.cs:988`: transaction target IDs still resolve before the per-action checks at lines 498 and 592. Post-cut S2 work was not started while must-fix S1 findings remained. |
