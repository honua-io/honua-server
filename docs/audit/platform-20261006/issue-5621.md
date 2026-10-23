# Audit record: issue 5621

| Finding id | Outcome | Evidence |
|---|---|---|
| `SRV-GRPC-001` | fixed | `SRV_GRPC_001_QueryFeatures_SourceBackedPublication_UsesRoutedReader` proves that a source-backed publication query uses the reader selected by `FeatureProviderQueryRouter`, rather than the default snapshot reader. `HonuaFeatureService.ApplyEdits` also rejects storage mappings that do not support managed writes with `FAILED_PRECONDITION`. |
| `SRV-GRPC-002` | fixed | `SRV_GRPC_002_QueryFeaturesStream_WithoutRequestedCount_HasNoQueryLimit` proves that an unbounded streaming request passes a `FeatureQuery` whose `Limit` is `null`; the stream therefore is not silently capped at the default record count. |
