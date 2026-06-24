// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.FieldWorkflows.Review;

namespace Honua.Core.Features.FieldWorkflows.Export;

/// <summary>
/// Server-owned, format identifiers for back-office field export packages (#1160).
/// </summary>
public static class FieldExportFormat
{
    /// <summary>GeoJSON <c>FeatureCollection</c> record set.</summary>
    public const string GeoJson = "geojson";

    /// <summary>Flat CSV record set.</summary>
    public const string Csv = "csv";

    /// <summary>Enumerates the supported export formats.</summary>
    public static readonly string[] All = [GeoJson, Csv];

    /// <summary>Whether the supplied value is a recognized export format.</summary>
    public static bool IsValid(string? format)
        => format is not null && Array.Exists(All, f => string.Equals(f, format, StringComparison.Ordinal));

    /// <summary>The MIME content type emitted for the supplied format.</summary>
    public static string ContentType(string format) => format switch
    {
        GeoJson => "application/geo+json",
        Csv => "text/csv; charset=utf-8",
        _ => "application/octet-stream"
    };

    /// <summary>The file extension (without leading dot) for the supplied format.</summary>
    public static string Extension(string format) => format switch
    {
        GeoJson => "geojson",
        Csv => "csv",
        _ => "bin"
    };
}

/// <summary>
/// Request describing a back-office field export package: the record-set filter
/// (project/form/service/layer/date-range/review-state) and the desired output
/// format. The filter mirrors <see cref="FieldReviewQuery"/> so an export selects
/// exactly the reviewed records the review surface exposes. This is the canonical
/// server contract consumed by the console and by mobile SDK clients; mobile must
/// not define a local equivalent.
/// </summary>
public sealed class FieldExportRequest
{
    /// <summary>Output format, one of <see cref="FieldExportFormat"/>. Defaults to GeoJSON.</summary>
    [JsonPropertyName("format")]
    public string Format { get; init; } = FieldExportFormat.GeoJson;

    /// <summary>Restrict to a single form package id.</summary>
    [JsonPropertyName("formId")]
    public string? FormId { get; init; }

    /// <summary>Restrict to a single target service id (project scope).</summary>
    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; init; }

    /// <summary>Restrict to a single target layer id.</summary>
    [JsonPropertyName("layerId")]
    public int? LayerId { get; init; }

    /// <summary>Restrict to a single review status, one of <see cref="FieldReviewStatus"/>.</summary>
    [JsonPropertyName("reviewStatus")]
    public string? ReviewStatus { get; init; }

    /// <summary>Restrict to records assigned to a reviewer.</summary>
    [JsonPropertyName("assignedTo")]
    public string? AssignedTo { get; init; }

    /// <summary>Restrict to a single sync status, one of <see cref="FieldSyncStatus"/>.</summary>
    [JsonPropertyName("syncStatus")]
    public string? SyncStatus { get; init; }

    /// <summary>When set, restrict to records flagged as conflicting.</summary>
    [JsonPropertyName("hasConflict")]
    public bool? HasConflict { get; init; }

    /// <summary>Inclusive lower bound on submission time.</summary>
    [JsonPropertyName("submittedFrom")]
    public DateTimeOffset? SubmittedFrom { get; init; }

    /// <summary>Inclusive upper bound on submission time.</summary>
    [JsonPropertyName("submittedTo")]
    public DateTimeOffset? SubmittedTo { get; init; }
}

/// <summary>
/// Durable, audited record of a generated field export package. Persisted on every
/// export so back-office operators retain an audit trail of what was exported, by
/// whom, and against which filter. Mobile screens can list these records to surface
/// export availability without re-running the export.
/// </summary>
public sealed class FieldExportRecord
{
    /// <summary>Stable export identifier.</summary>
    [JsonPropertyName("exportId")]
    public Guid ExportId { get; init; }

    /// <summary>Output format the package was generated in.</summary>
    [JsonPropertyName("format")]
    public string Format { get; init; } = string.Empty;

    /// <summary>Number of submission records included in the package.</summary>
    [JsonPropertyName("recordCount")]
    public long RecordCount { get; init; }

    /// <summary>Sanitized JSON description of the filter applied to the export.</summary>
    [JsonPropertyName("filter")]
    public string Filter { get; init; } = "{}";

    /// <summary>Actor that generated the export.</summary>
    [JsonPropertyName("requestedBy")]
    public string RequestedBy { get; init; } = string.Empty;

    /// <summary>UTC timestamp the export was generated.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Page of export records returned by an export-history listing.
/// </summary>
public sealed class FieldExportRecordListResult
{
    /// <summary>Export records in this page, newest first.</summary>
    [JsonPropertyName("items")]
    public FieldExportRecord[] Items { get; init; } = [];

    /// <summary>Total export records, ignoring paging.</summary>
    [JsonPropertyName("total")]
    public long Total { get; init; }

    /// <summary>Limit applied to this page.</summary>
    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    /// <summary>Offset applied to this page.</summary>
    [JsonPropertyName("offset")]
    public int Offset { get; init; }
}

/// <summary>
/// The materialized output of a field export package: the selected record set and
/// the durable record describing the export.
/// </summary>
public sealed class FieldExportPackage
{
    /// <summary>The durable export record (also persisted for the audit trail).</summary>
    public FieldExportRecord Record { get; init; } = new();

    /// <summary>The selected submission records included in the package.</summary>
    public FieldSubmissionRecord[] Items { get; init; } = [];
}
