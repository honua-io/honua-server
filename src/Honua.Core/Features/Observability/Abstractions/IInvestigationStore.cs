// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.Observability.Abstractions;

/// <summary>
/// Persists Console-driven investigations (incident scratchpads that pin
/// events and link upstream resources such as alerts, jobs, releases, and
/// change sets).
/// </summary>
public interface IInvestigationStore
{
    /// <summary>
    /// Creates a new investigation.
    /// </summary>
    /// <param name="title">Human-readable title.</param>
    /// <param name="createdBy">Actor creating the investigation.</param>
    /// <param name="summary">Optional summary.</param>
    /// <param name="createdAt">Timestamp to record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<InvestigationRecord> CreateAsync(
        string title,
        string createdBy,
        string? summary,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the full investigation record (including pins and links).
    /// </summary>
    Task<InvestigationRecord?> GetAsync(string investigationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a page of investigation summaries ordered by most recently updated.
    /// </summary>
    Task<InvestigationPage> ListAsync(InvestigationFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates mutable fields on an investigation. Null arguments are ignored.
    /// </summary>
    Task<InvestigationRecord?> UpdateAsync(
        string investigationId,
        string? title,
        string? summary,
        InvestigationStatus? status,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends an event pin to an investigation.
    /// </summary>
    Task<InvestigationRecord?> AddPinAsync(
        string investigationId,
        string eventRef,
        OperateEventKind eventKind,
        DateTimeOffset occurredAt,
        string? note,
        string actor,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a pin from an investigation. Returns the refreshed record or null if not found.
    /// </summary>
    Task<InvestigationRecord?> RemovePinAsync(
        string investigationId,
        long pinId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a resource link to an investigation.
    /// </summary>
    Task<InvestigationRecord?> AddLinkAsync(
        string investigationId,
        InvestigationResourceKind resourceKind,
        string resourceId,
        string? note,
        string actor,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a link from an investigation. Returns the refreshed record or null if not found.
    /// </summary>
    Task<InvestigationRecord?> RemoveLinkAsync(
        string investigationId,
        long linkId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
}
