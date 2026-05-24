// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Observability.Domain;

/// <summary>
/// Investigation lifecycle status.
/// </summary>
public enum InvestigationStatus
{
    /// <summary>
    /// Investigation is open and being worked on.
    /// </summary>
    Open = 0,

    /// <summary>
    /// Investigation is closed.
    /// </summary>
    Closed = 1,
}

/// <summary>
/// Resource categories an investigation can link to.
/// </summary>
public enum InvestigationResourceKind
{
    /// <summary>
    /// Linked to an alert event by event id.
    /// </summary>
    Alert = 1,

    /// <summary>
    /// Linked to a job/operation by operation id.
    /// </summary>
    Job = 2,

    /// <summary>
    /// Linked to a release by release id.
    /// </summary>
    Release = 3,

    /// <summary>
    /// Linked to a change-set by change-set id.
    /// </summary>
    ChangeSet = 4,
}

/// <summary>
/// Pinned event reference on an investigation.
/// </summary>
public sealed record InvestigationPin
{
    /// <summary>
    /// Pin row identifier.
    /// </summary>
    public required long PinId { get; init; }

    /// <summary>
    /// Source-prefixed event ref (e.g. "alert:42", "audit:917").
    /// </summary>
    public required string EventRef { get; init; }

    /// <summary>
    /// Kind of the pinned event for fast Console rendering.
    /// </summary>
    public required OperateEventKind EventKind { get; init; }

    /// <summary>
    /// Original event <c>OccurredAt</c> captured at pin time.
    /// </summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Optional operator note.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>
    /// When the pin was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Actor that created the pin.
    /// </summary>
    public required string CreatedBy { get; init; }
}

/// <summary>
/// External resource linked to an investigation.
/// </summary>
public sealed record InvestigationLink
{
    /// <summary>
    /// Link row identifier.
    /// </summary>
    public required long LinkId { get; init; }

    /// <summary>
    /// Linked resource kind.
    /// </summary>
    public required InvestigationResourceKind ResourceKind { get; init; }

    /// <summary>
    /// Stable identifier for the linked resource (job id, release id, alert id, change-set id).
    /// </summary>
    public required string ResourceId { get; init; }

    /// <summary>
    /// Optional operator note.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>
    /// When the link was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Actor that created the link.
    /// </summary>
    public required string CreatedBy { get; init; }
}

/// <summary>
/// Full investigation record with pinned events and linked resources.
/// </summary>
public sealed record InvestigationRecord
{
    /// <summary>
    /// Stable investigation identifier (e.g. <c>inv_AB123</c>).
    /// </summary>
    public required string InvestigationId { get; init; }

    /// <summary>
    /// Human-readable title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Lifecycle status.
    /// </summary>
    public required InvestigationStatus Status { get; init; }

    /// <summary>
    /// When the investigation was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Actor that created the investigation.
    /// </summary>
    public required string CreatedBy { get; init; }

    /// <summary>
    /// Last update timestamp (any mutation refreshes this).
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Optional free-form summary.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Pinned events.
    /// </summary>
    public ImmutableArray<InvestigationPin> Pins { get; init; } = ImmutableArray<InvestigationPin>.Empty;

    /// <summary>
    /// Linked resources.
    /// </summary>
    public ImmutableArray<InvestigationLink> Links { get; init; } = ImmutableArray<InvestigationLink>.Empty;
}

/// <summary>
/// Filter for paginating investigation list responses.
/// </summary>
public sealed record InvestigationFilter
{
    /// <summary>
    /// Optional status filter.
    /// </summary>
    public InvestigationStatus? Status { get; init; }

    /// <summary>
    /// Optional created-by actor filter (exact match).
    /// </summary>
    public string? CreatedBy { get; init; }

    /// <summary>
    /// Optional case-insensitive substring search against <see cref="InvestigationRecord.Title"/>.
    /// </summary>
    public string? TitleContains { get; init; }

    /// <summary>
    /// Maximum number of rows to return. Implementations clamp to a server-side maximum.
    /// </summary>
    public int PageSize { get; init; } = 50;

    /// <summary>
    /// Opaque pagination cursor returned by a prior page.
    /// </summary>
    public string? Cursor { get; init; }
}

/// <summary>
/// Lightweight summary used in list responses (without pin/link arrays).
/// </summary>
public sealed record InvestigationSummary
{
    /// <summary>
    /// Stable investigation identifier.
    /// </summary>
    public required string InvestigationId { get; init; }

    /// <summary>
    /// Human-readable title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Lifecycle status.
    /// </summary>
    public required InvestigationStatus Status { get; init; }

    /// <summary>
    /// When the investigation was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Actor that created the investigation.
    /// </summary>
    public required string CreatedBy { get; init; }

    /// <summary>
    /// Last update timestamp.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Optional free-form summary.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Number of pinned events.
    /// </summary>
    public required int PinCount { get; init; }

    /// <summary>
    /// Number of linked resources.
    /// </summary>
    public required int LinkCount { get; init; }
}

/// <summary>
/// Result page from a list-investigations call.
/// </summary>
public sealed record InvestigationPage
{
    /// <summary>
    /// Items returned in this page, ordered most recently updated first.
    /// </summary>
    public required IReadOnlyList<InvestigationSummary> Items { get; init; }

    /// <summary>
    /// Cursor to retrieve the next page, or null when there are no more rows.
    /// </summary>
    public string? NextCursor { get; init; }
}
