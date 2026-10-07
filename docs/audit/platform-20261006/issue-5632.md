# Issue 5632 audit resolution

Re-verification was performed against the current worktree. The identifier fix changes observable
STAC behavior: convention-named `stac_id`, `item_id`, and `id` fields are no longer used unless the
selected field is explicitly declared with the `id.primary` semantic role. Otherwise, the stable
provider feature identifier is emitted and used for lookup. This prevents non-unique ordinary
attributes from becoming STAC item identifiers while keeping explicitly configured identifiers.

PR #5667 review adjudication confirmed two lookup regressions. Single-item lookup now passes
the resource into both candidate matching and final identifier validation, and the unbound
attribute query uses the declared primary field (including names outside the old conventions).
For storage-bound resources with no declared primary identifier or `objectid` schema field,
lookup and `ids` search now use canonical `FeatureQuery.ObjectIds`, allowing each provider to
map its physical key without inventing an `objectid` column. Existing filters, candidate limits,
and duplicate-identifier checks are retained. `StacItemIdentifierEndpointAuditTests` follows
mapped self links for bound and unbound custom identifiers and exercises the unannotated-key
fallback through item GET and ids search. `BoundLookup_UnannotatedSchema_UsesObjectIdsWithoutInventingAField`
covers preserved filters and nonnumeric ids without an unrestricted provider query.

Windows-native review verification: the original runtime failed 7 of the 9 focused identifier
regressions; the fixed runtime passes all 9. Release builds pass with zero warnings and errors,
all 72 MySQL query-builder tests pass, and changed-file whitespace verification passes.
The related STAC identifier/page-reader/provider-routing suites report 9 passes and 12 failures
in tests expecting convention-field precedence. The helper precedence failure was reproduced
against the original PR runtime (2 passes, 1 failure); these existing tests were not modified.

PR Gate run `37569461538` failed in job `112624567388` on
`TierTraitEnforcementTests.EveryTestMethod_MustCarryATierBearingAttribute_OrBeBaselined`.
The three isolated methods in `StacItemIdentifierAuditTests` used plain xUnit attributes,
so tier-filtered gates could not discover them. They now use `UnitTest` and `UnitTheory`
to participate in `Tier=Fast`; assertions, runtime behavior, and the guard baseline are unchanged.
Windows-native gate-fix verification: the Release architecture-test project and its host/test
dependencies build with warnings as errors (zero warnings/errors). All nine focused identifier
cases pass, including the four isolated cases selected by `Tier=Fast`, and all 13
`TierTraitEnforcementTests` pass. Neither focused run skips any cases.

| Finding id | Outcome | Evidence |
|---|---|---|
| `SRV-OGC-005` | fixed | `SRV_OGC_005_NonUniqueConventionNamedFieldFallsBackToFeatureIdentifier` verifies that a non-unique convention-named `id` attribute is neither emitted nor queried as the item identifier. `StacMappingService.ResolveItemId` and `StacItemIdWhereBuilder.GetCandidateFields` now use only the declared primary identifier. |
| `SRV-OGC-014` | not attempted | Re-verified in `OgcFeaturesQueryHandler.HandleGetItemAsync`: the entity tag is still computed from `responseFeature`, after response-CRS projection. Deferred in favor of completing the higher-severity item-id correction. |
| `SRV-OGC-016` | not attempted | Re-verified in `Wfs20Handler.Transaction.ParseTransactionCoordinateSequence`: `srsDimension` is still read only from the `posList` or passed member element, and the parser still discards Z. Deferred in favor of completing the higher-severity item-id correction. |
| `SRV-OGC-018` | fixed | `SRV_OGC_018_IdFilterUsesTheIdentifierSerializedByStac` verifies that the CQL2 `id` queryable resolves to the explicitly declared primary identifier. Item mapping, identifier lookup, filtering, and canonical sorting now apply the same rule. |
| `SRV-OGC-021` | not attempted | Re-verified in `SearchEndpoints.ExecuteSearchAcrossPublicationsAsync`: `globallyOrderedCandidates` is still allocated only when both `ids` and `sortby` are supplied. Deferred in favor of completing the higher-severity item-id correction. |
| `SRV-OGC-022` | not attempted | Re-verified in `SearchEndpoints.BuildSearchQuery`: GET continuation links still emit the `token` query parameter. Deferred in favor of completing the higher-severity item-id correction. |
| Low-severity single-feature write error mapping | not attempted | Re-verified at `OgcFeaturesCrudHandler` single-feature write handling; no regression test or code change was attempted after the higher-severity work. |
