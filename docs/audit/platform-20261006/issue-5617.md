# Issue 5617 audit record

All six prioritized findings were re-verified against the current worktree before changes. The fixes intentionally change observable filter behavior: valid epoch-millisecond temporal values, typed source timestamps, untyped FES string values, UUID/time literals, empty JSONB sort values, and fractional single-precision values now produce typed, null-safe SQL rather than errors or missed matches. Wire shapes and public APIs are unchanged.

| Finding id | Outcome | Evidence |
|---|---|---|
| SRV-DB-004 | fixed | `FeatureQueryBuilderTemporalAuditTests.SRV_DB_004_TemporalFilterAndDateBinsUseEpochAwareExpressions` proves temporal predicates and date bins emit epoch-aware conversions. |
| SRV-DB-006 | fixed | `PostgresStorageMappedFeatureReaderSqlTests.SRV_DB_006_SourceTemporalFilterConvertsTypedColumnToTextBeforeEpochDetection` proves typed source columns are converted to text before empty/epoch detection. |
| SRV-DB-007 | fixed | `FilterExpressionNormalizerTests.SRV_DB_007_StringFieldPreservesUntypedFesLiteralLexicalValue` proves an untyped FES literal retains the leading zero required by a string property; normalization also now coerces UUIDs and IN-list members. |
| SRV-DB-017 | fixed | `FilterExpressionNormalizerTests.SRV_DB_017_TimeFieldCoercesTextLiteralToTimeOnly` and `PostgresSqlFilterTranslatorTests.SRV_DB_017_TimePropertyUsesDirectTimeCast` prove time literals become `TimeOnly` parameters and time columns avoid timestamp casts. |
| SRV-DB-020 | fixed | `PostgresStorageMappedFeatureReaderSqlTests.SRV_DB_020_JsonbNumericSortTreatsEmptyStringAsNull` proves typed JSONB sort values pass through `NULLIF` before casting. |
| SRV-DB-021 | fixed | `PostgresSqlFilterTranslatorTests.SRV_DB_021_FloatPropertyComparesAtDoublePrecision` proves Float JSONB values are compared at the double precision used by numeric parameters. |
| GeoServices SQL function allow-list | not attempted | S3 backlog item; deferred after completing all higher-severity findings. |
| CQL2 aggregate calls in WHERE | not attempted | S3 backlog item; deferred after completing all higher-severity findings. |
| FES `matchCase="false"` comparisons | not attempted | S3 backlog item; deferred after completing all higher-severity findings. |
