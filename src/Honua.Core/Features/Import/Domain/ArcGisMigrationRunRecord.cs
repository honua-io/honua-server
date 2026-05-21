// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Stable parity status values surfaced through the admin migration evidence list.
/// </summary>
public static class ArcGisMigrationRunStatuses
{
    /// <summary>The run has a manifest but parity has not been executed yet.</summary>
    public const string ManifestOnly = "manifest-only";

    /// <summary>The run has parity classified <c>pass</c> across all probes.</summary>
    public const string Pass = "pass";

    /// <summary>The run has parity classified <c>warn</c> on at least one probe.</summary>
    public const string Warn = "warn";

    /// <summary>The run has parity classified <c>fail</c> on at least one probe.</summary>
    public const string Fail = "fail";
}

/// <summary>
/// Run-level metadata persisted alongside the manifest and parity artifacts.
/// </summary>
public sealed record ArcGisMigrationRunRecord
{
    /// <summary>
    /// Stable run identifier (typically a GUID string). Required.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Canonical source URL the manifest was generated from.
    /// </summary>
    public required string SourceUrl { get; init; }

    /// <summary>
    /// Human-readable source display name (copied from the inventory identity).
    /// </summary>
    public string? SourceDisplayName { get; init; }

    /// <summary>
    /// Optional source version copied from the inventory identity.
    /// </summary>
    public string? SourceVersion { get; init; }

    /// <summary>
    /// Wall-clock time the run was recorded.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Optional actor identifier (operator id or service account) that triggered the run.
    /// </summary>
    public string? Actor { get; init; }
}

/// <summary>
/// Filter / paging parameters for <see cref="Abstractions.IArcGisMigrationEvidenceStore.ListAsync"/>.
/// </summary>
public sealed record ArcGisMigrationRunFilter
{
    /// <summary>
    /// Optional case-insensitive substring match on <see cref="ArcGisMigrationRunRecord.SourceUrl"/>.
    /// </summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// Optional parity status filter. Must be one of <see cref="ArcGisMigrationRunStatuses"/>.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// Zero-based page index. Clamped to <c>&gt;= 0</c>.
    /// </summary>
    public int Page { get; init; }

    /// <summary>
    /// Page size. Clamped to the <c>[1, 100]</c> range by the endpoint layer.
    /// </summary>
    public int PageSize { get; init; } = 25;
}

/// <summary>
/// Page of ArcGIS migration run summaries.
/// </summary>
public sealed record ArcGisMigrationRunListResult
{
    /// <summary>
    /// Run summaries on this page, newest first.
    /// </summary>
    public ArcGisMigrationRunSummary[] Items { get; init; } = [];

    /// <summary>
    /// Total number of runs matching the filter across all pages.
    /// </summary>
    public int TotalCount { get; init; }

    /// <summary>
    /// Echoed page index.
    /// </summary>
    public int Page { get; init; }

    /// <summary>
    /// Echoed page size.
    /// </summary>
    public int PageSize { get; init; }
}

/// <summary>
/// Lightweight summary returned by the admin list endpoint.
/// </summary>
public sealed record ArcGisMigrationRunSummary
{
    /// <summary>
    /// Stable run identifier.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Canonical source URL.
    /// </summary>
    public required string SourceUrl { get; init; }

    /// <summary>
    /// Optional source display name.
    /// </summary>
    public string? SourceDisplayName { get; init; }

    /// <summary>
    /// Optional source version.
    /// </summary>
    public string? SourceVersion { get; init; }

    /// <summary>
    /// Wall-clock time the run was recorded.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Optional actor identifier.
    /// </summary>
    public string? Actor { get; init; }

    /// <summary>
    /// Parity status. One of <see cref="ArcGisMigrationRunStatuses"/>.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// <c>true</c> when the run has a slice-5 parity artifact persisted.
    /// </summary>
    public bool HasParity { get; init; }

    /// <summary>
    /// Number of target resources in the manifest. Useful for table summary rows in the UI.
    /// </summary>
    public int TargetResourceCount { get; init; }
}
