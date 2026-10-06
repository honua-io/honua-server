# Issue 5632 audit resolution

Re-verification was performed against the current worktree. The identifier fix changes observable
STAC behavior: convention-named `stac_id`, `item_id`, and `id` fields are no longer used unless the
selected field is explicitly declared with the `id.primary` semantic role. Otherwise, the stable
provider feature identifier is emitted and used for lookup. This prevents non-unique ordinary
attributes from becoming STAC item identifiers while keeping explicitly configured identifiers.

| Finding id | Outcome | Evidence |
|---|---|---|
| `SRV-OGC-005` | fixed | `SRV_OGC_005_NonUniqueConventionNamedFieldFallsBackToFeatureIdentifier` verifies that a non-unique convention-named `id` attribute is neither emitted nor queried as the item identifier. `StacMappingService.ResolveItemId` and `StacItemIdWhereBuilder.GetCandidateFields` now use only the declared primary identifier. |
| `SRV-OGC-014` | not attempted | Re-verified in `OgcFeaturesQueryHandler.HandleGetItemAsync`: the entity tag is still computed from `responseFeature`, after response-CRS projection. Deferred in favor of completing the higher-severity item-id correction. |
| `SRV-OGC-016` | not attempted | Re-verified in `Wfs20Handler.Transaction.ParseTransactionCoordinateSequence`: `srsDimension` is still read only from the `posList` or passed member element, and the parser still discards Z. Deferred in favor of completing the higher-severity item-id correction. |
| `SRV-OGC-018` | fixed | `SRV_OGC_018_IdFilterUsesTheIdentifierSerializedByStac` verifies that the CQL2 `id` queryable resolves to the explicitly declared primary identifier. Item mapping, identifier lookup, filtering, and canonical sorting now apply the same rule. |
| `SRV-OGC-021` | not attempted | Re-verified in `SearchEndpoints.ExecuteSearchAcrossPublicationsAsync`: `globallyOrderedCandidates` is still allocated only when both `ids` and `sortby` are supplied. Deferred in favor of completing the higher-severity item-id correction. |
| `SRV-OGC-022` | not attempted | Re-verified in `SearchEndpoints.BuildSearchQuery`: GET continuation links still emit the `token` query parameter. Deferred in favor of completing the higher-severity item-id correction. |
| Low-severity single-feature write error mapping | not attempted | Re-verified at `OgcFeaturesCrudHandler` single-feature write handling; no regression test or code change was attempted after the higher-severity work. |
