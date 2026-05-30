// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Outcome of an OGC API Features collection import. The shape mirrors other migration import
/// services so operator dashboards can render uniform success/warning information.
/// </summary>
public sealed record OgcApiFeaturesImportResult
{
    /// <summary>
    /// Whether the import completed without error. The sink may still have written rows when
    /// <see cref="Success"/> is <c>false</c>: <see cref="FeaturesImported"/> reflects the actual
    /// count persisted by the sink before the failure was observed.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Collection identifier that was imported.
    /// </summary>
    public required string CollectionId { get; init; }

    /// <summary>
    /// Schema-qualified target identifier the sink wrote to, in <c>schema.table</c> form.
    /// </summary>
    public required string Target { get; init; }

    /// <summary>
    /// Number of features written to the sink. Idempotent re-runs return the count of rows touched
    /// by the apply, regardless of whether they were already present.
    /// </summary>
    public int FeaturesImported { get; init; }

    /// <summary>
    /// Number of features that were skipped because the source advertised them but the importer
    /// could not project them (missing identifier, invalid geometry, etc.).
    /// </summary>
    public int FeaturesSkipped { get; init; }

    /// <summary>
    /// Number of pages fetched from the source. Useful for paging probes and for diagnosing
    /// servers that mis-advertise <c>next</c> links.
    /// </summary>
    public int PagesFetched { get; init; }

    /// <summary>
    /// Whether the source reported a <c>next</c> link that was suppressed because the importer
    /// reached <see cref="OgcApiFeaturesImportRequest.MaxPages"/> or
    /// <see cref="OgcApiFeaturesImportRequest.MaxFeatures"/>.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Optional error message when <see cref="Success"/> is <c>false</c>.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Optional stable error code when <see cref="Success"/> is <c>false</c>.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Warning messages surfaced by the importer or sink (idempotent skips, unknown CRS, etc.).
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Wall-clock duration of the import run.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// True when the importer detected a change in filter/bbox/datetime relative to the previous
    /// run against the same target. The importer does NOT delete rows that fell out of the new
    /// scope; instead it logs a warning and surfaces a manual-review record so operators can
    /// reconcile the catalog explicitly. See <see cref="Warnings"/> for the operator-facing
    /// description and <c>ManualReviewReason</c> for the structured reason.
    /// </summary>
    public bool ScopeDriftDetected { get; init; }

    /// <summary>
    /// Optional manual-review reason recorded when <see cref="ScopeDriftDetected"/> is <c>true</c>.
    /// Operators can route this through the migration manifest review queue.
    /// </summary>
    public string? ManualReviewReason { get; init; }

    /// <summary>
    /// Structured schema-mapping diagnostics emitted when the source collection's properties do
    /// not align cleanly with the target table columns. Clean (automated) matches do NOT appear
    /// here — only properties that triggered an assisted, manual-review, or unsupported finding.
    /// See <see cref="OgcApiFeaturesSchemaMappingDiagnostic"/> for the classification taxonomy.
    /// </summary>
    public IReadOnlyList<OgcApiFeaturesSchemaMappingDiagnostic> MappingDiagnostics { get; init; } = [];

    /// <summary>
    /// Source-advertised feature count taken from <c>numberMatched</c> on the first items page.
    /// <c>null</c> when the source did not advertise a total (in which case the inline feature
    /// count parity probe reports a <see cref="OgcApiFeaturesFeatureCountParityStates.NotApplicable"/>
    /// state).
    /// </summary>
    public long? SourceFeatureCountReported { get; init; }

    /// <summary>
    /// Inline feature-count parity probe comparing the source-advertised <c>numberMatched</c>
    /// against the number of features the importer successfully wrote to the sink during this run.
    /// This is the lightweight per-import parity signal — heavier per-feature parity probes
    /// (sampled geometry, attribute hashing) belong on a separate parity-runner slice.
    /// </summary>
    public OgcApiFeaturesFeatureCountParity? FeatureCountParity { get; init; }
}

/// <summary>
/// Stable values for the inline OGC API Features feature-count parity probe state.
/// </summary>
public static class OgcApiFeaturesFeatureCountParityStates
{
    /// <summary>The source-advertised count matches the imported count exactly.</summary>
    public const string Pass = "pass";

    /// <summary>The source-advertised count diverges from the imported count.</summary>
    public const string Fail = "fail";

    /// <summary>
    /// The probe could not produce a deterministic verdict — for example, the source did not
    /// advertise a <c>numberMatched</c> total, the importer truncated early at the operator's cap,
    /// or filter-pushdown made the source total non-comparable to the imported subset.
    /// </summary>
    public const string NotApplicable = "not-applicable";
}

/// <summary>
/// Inline feature-count parity probe emitted by the OGC API Features collection importer. The
/// probe compares the source-advertised total feature count (<c>numberMatched</c>) against the
/// number of features successfully written to the sink for the same run. It deliberately stays
/// scoped to the importer's view of the world so operators get a fast, deterministic post-import
/// parity signal without requiring a separate parity runner pass.
/// </summary>
public sealed record OgcApiFeaturesFeatureCountParity
{
    /// <summary>
    /// Stable probe identifier. Matches the parity-stage naming convention used elsewhere in the
    /// migration pipeline so downstream evidence aggregators can dedupe by id.
    /// </summary>
    public string ProbeId { get; init; } = "feature-count";

    /// <summary>
    /// Probe state: <see cref="OgcApiFeaturesFeatureCountParityStates.Pass"/>,
    /// <see cref="OgcApiFeaturesFeatureCountParityStates.Fail"/>, or
    /// <see cref="OgcApiFeaturesFeatureCountParityStates.NotApplicable"/>.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Source-advertised feature count when known. Lifted from <c>numberMatched</c> on the first
    /// items page.
    /// </summary>
    public long? Expected { get; init; }

    /// <summary>
    /// Number of features successfully written to the sink during this run.
    /// </summary>
    public long Observed { get; init; }

    /// <summary>
    /// Operator-facing summary describing why the probe was assigned its state. Must not contain
    /// credentials or other secrets — the importer constructs the summary from numeric counts and
    /// known scope-truncation reasons only.
    /// </summary>
    public required string Summary { get; init; }
}

/// <summary>
/// Stable error codes surfaced by the OGC API Features collection importer.
/// </summary>
public static class OgcApiFeaturesImportErrorCodes
{
    /// <summary>The supplied OGC API Features service URL was malformed or disallowed.</summary>
    public const string InvalidServiceUrl = "ogc_api_features.invalid_service_url";

    /// <summary>The collection metadata or items endpoint could not be reached.</summary>
    public const string SourceUnreachable = "ogc_api_features.source_unreachable";

    /// <summary>The source advertised the collection but no items endpoint could be resolved.</summary>
    public const string MissingItemsEndpoint = "ogc_api_features.missing_items_endpoint";

    /// <summary>The items endpoint did not return a JSON FeatureCollection.</summary>
    public const string UnsupportedItemsEncoding = "ogc_api_features.unsupported_items_encoding";

    /// <summary>An OGC API Features parsing error blocked the import.</summary>
    public const string InvalidItemsDocument = "ogc_api_features.invalid_items_document";

    /// <summary>The catalog sink rejected the write (DDL or insert failure).</summary>
    public const string SinkFailure = "ogc_api_features.sink_failure";

    /// <summary>The import timed out before the source emitted all pages.</summary>
    public const string Timeout = "ogc_api_features.timeout";

    /// <summary>The CQL2 filter expression was empty or otherwise rejected by the importer.</summary>
    public const string InvalidFilter = "ogc_api_features.invalid_filter";

    /// <summary>The bbox tuple length, ordering, or numeric content was invalid.</summary>
    public const string InvalidBbox = "ogc_api_features.invalid_bbox";

    /// <summary>The datetime expression could not be parsed as an RFC3339 instant or interval.</summary>
    public const string InvalidDatetime = "ogc_api_features.invalid_datetime";
}
