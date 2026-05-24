// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Wire shape for a full investigation record (#1168).
/// </summary>
internal sealed class InvestigationResponse
{
    /// <summary>Stable identifier.</summary>
    public required string InvestigationId { get; init; }

    /// <summary>Human-readable title.</summary>
    public required string Title { get; init; }

    /// <summary>Status (lower-case: open / closed).</summary>
    public required string Status { get; init; }

    /// <summary>When the investigation was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Actor that created the investigation.</summary>
    public required string CreatedBy { get; init; }

    /// <summary>Last update timestamp.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Optional free-form summary.</summary>
    public string? Summary { get; init; }

    /// <summary>Pinned events.</summary>
    public required IReadOnlyList<InvestigationPinResponse> Pins { get; init; }

    /// <summary>Linked resources.</summary>
    public required IReadOnlyList<InvestigationLinkResponse> Links { get; init; }
}

/// <summary>
/// Wire shape for an investigation summary in list responses.
/// </summary>
internal sealed class InvestigationSummaryResponse
{
    /// <summary>Stable identifier.</summary>
    public required string InvestigationId { get; init; }

    /// <summary>Human-readable title.</summary>
    public required string Title { get; init; }

    /// <summary>Status (lower-case: open / closed).</summary>
    public required string Status { get; init; }

    /// <summary>When the investigation was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Actor that created the investigation.</summary>
    public required string CreatedBy { get; init; }

    /// <summary>Last update timestamp.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Optional free-form summary.</summary>
    public string? Summary { get; init; }

    /// <summary>Number of pinned events.</summary>
    public required int PinCount { get; init; }

    /// <summary>Number of linked resources.</summary>
    public required int LinkCount { get; init; }
}

/// <summary>
/// Wire shape for an event pin.
/// </summary>
internal sealed class InvestigationPinResponse
{
    /// <summary>Pin identifier.</summary>
    public required long PinId { get; init; }

    /// <summary>Source-prefixed event ref (e.g. "alert:42").</summary>
    public required string EventRef { get; init; }

    /// <summary>Event kind (lower-case).</summary>
    public required string EventKind { get; init; }

    /// <summary>Original event timestamp.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Optional operator note.</summary>
    public string? Note { get; init; }

    /// <summary>When the pin was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Actor that created the pin.</summary>
    public required string CreatedBy { get; init; }
}

/// <summary>
/// Wire shape for a linked resource.
/// </summary>
internal sealed class InvestigationLinkResponse
{
    /// <summary>Link identifier.</summary>
    public required long LinkId { get; init; }

    /// <summary>Resource kind (lower-case: alert/job/release/changeSet).</summary>
    public required string ResourceKind { get; init; }

    /// <summary>Stable resource identifier.</summary>
    public required string ResourceId { get; init; }

    /// <summary>Optional operator note.</summary>
    public string? Note { get; init; }

    /// <summary>When the link was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Actor that created the link.</summary>
    public required string CreatedBy { get; init; }
}

/// <summary>
/// Page of investigation summaries.
/// </summary>
internal sealed class InvestigationPageResponse
{
    /// <summary>Items returned in this page (most recently updated first).</summary>
    public required IReadOnlyList<InvestigationSummaryResponse> Items { get; init; }

    /// <summary>Cursor for the next page or null when none.</summary>
    public string? NextCursor { get; init; }
}

/// <summary>
/// Body for creating an investigation.
/// </summary>
internal sealed class CreateInvestigationRequest
{
    /// <summary>Human-readable title.</summary>
    public required string Title { get; init; }

    /// <summary>Optional free-form summary.</summary>
    public string? Summary { get; init; }
}

/// <summary>
/// Body for updating an investigation. Null fields are left unchanged.
/// </summary>
internal sealed class UpdateInvestigationRequest
{
    /// <summary>New title, when changing.</summary>
    public string? Title { get; init; }

    /// <summary>New summary, when changing.</summary>
    public string? Summary { get; init; }

    /// <summary>New status (open / closed), when changing.</summary>
    public string? Status { get; init; }
}

/// <summary>
/// Body for adding an event pin.
/// </summary>
internal sealed class AddInvestigationPinRequest
{
    /// <summary>Source-prefixed event ref (e.g. "alert:42").</summary>
    public required string EventRef { get; init; }

    /// <summary>Event kind (alert/audit/job/release/syncConflict/temporalOp/log).</summary>
    public required string EventKind { get; init; }

    /// <summary>Original event timestamp.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Optional operator note.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Body for adding a resource link.
/// </summary>
internal sealed class AddInvestigationLinkRequest
{
    /// <summary>Resource kind (alert / job / release / changeSet).</summary>
    public required string ResourceKind { get; init; }

    /// <summary>Stable resource identifier.</summary>
    public required string ResourceId { get; init; }

    /// <summary>Optional operator note.</summary>
    public string? Note { get; init; }
}
