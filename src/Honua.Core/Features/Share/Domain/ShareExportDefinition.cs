// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Share.Domain;

/// <summary>
/// A persisted, server-owned scheduled Share export definition. Anchored to a
/// stable Console resource identifier when available, with <c>(serviceName, layerId)</c>
/// retained as the compatibility locator for layer-backed resources.
/// </summary>
/// <remarks>
/// Scheduled execution is out of scope for the API surface that owns this record;
/// <see cref="NextRunAt"/> reflects intent only until a scheduler host service is wired.
/// Secrets must be referenced by vault/environment key in <see cref="DestinationConfig"/>
/// rather than stored verbatim; the store redacts known secret-shaped values on read.
/// </remarks>
public sealed record ShareExportDefinition
{
    /// <summary>Stable export definition identifier.</summary>
    public required string ExportId { get; init; }

    /// <summary>Optional stable Console content/resource identifier this export belongs to.</summary>
    public string? ResourceId { get; init; }

    /// <summary>Service name the export is anchored to.</summary>
    public required string ServiceName { get; init; }

    /// <summary>Layer identifier the export is anchored to.</summary>
    public required int LayerId { get; init; }

    /// <summary>Optional human-readable display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Destination family this export delivers to.</summary>
    public required ShareExportDestinationType DestinationType { get; init; }

    /// <summary>
    /// Resolved destination availability captured at the most recent write. Console
    /// renders an advisory badge for <see cref="ShareExportDestinationStatus.Unsupported"/>
    /// and <see cref="ShareExportDestinationStatus.NotConfigured"/> states.
    /// </summary>
    public required ShareExportDestinationStatus DestinationStatus { get; init; }

    /// <summary>
    /// Per-destination configuration with secret-shaped values redacted on read.
    /// </summary>
    public IReadOnlyDictionary<string, string> DestinationConfig { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Output format identifier (for example CSV, GeoJSON, Shapefile, GeoPackage).</summary>
    public required string Format { get; init; }

    /// <summary>Cron expression (UTC) describing the intended schedule.</summary>
    public required string Schedule { get; init; }

    /// <summary>Whether the schedule is active or paused.</summary>
    public ShareExportScheduleState ScheduleState { get; init; } = ShareExportScheduleState.Active;

    /// <summary>Time the definition was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Time the definition was most recently updated.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Time of the most recent run, when any run exists.</summary>
    public DateTimeOffset? LastRunAt { get; init; }

    /// <summary>Intended next run time derived from the schedule, when computable.</summary>
    public DateTimeOffset? NextRunAt { get; init; }
}

/// <summary>
/// Filter and cursor criteria for listing scheduled Share export definitions.
/// </summary>
public sealed record ShareExportDefinitionQuery
{
    /// <summary>Optional service-name filter.</summary>
    public string? ServiceName { get; init; }

    /// <summary>Optional stable Console content/resource identifier filter.</summary>
    public string? ResourceId { get; init; }

    /// <summary>Optional layer-identifier filter.</summary>
    public int? LayerId { get; init; }

    /// <summary>Optional destination-family filter.</summary>
    public ShareExportDestinationType? DestinationType { get; init; }

    /// <summary>Optional schedule-state filter.</summary>
    public ShareExportScheduleState? ScheduleState { get; init; }

    /// <summary>Opaque continuation cursor from a previous page.</summary>
    public string? Cursor { get; init; }

    /// <summary>Maximum number of items to return.</summary>
    public int Limit { get; init; } = 50;
}

/// <summary>
/// A page of scheduled Share export definitions ordered newest first.
/// </summary>
public sealed record ShareExportDefinitionPage
{
    /// <summary>Definitions in this page.</summary>
    public required IReadOnlyList<ShareExportDefinition> Items { get; init; }

    /// <summary>Opaque continuation cursor, or null when no further pages exist.</summary>
    public string? NextCursor { get; init; }
}
