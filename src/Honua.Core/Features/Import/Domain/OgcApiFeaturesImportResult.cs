// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

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
