// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// Filter and paging criteria for listing durable workflow operations newest-first.
/// </summary>
public sealed record WorkflowOperationQuery
{
    /// <summary>
    /// Optional workflow-kind filter. When null, operations of every kind are returned.
    /// </summary>
    public WorkflowOperationKind? Kind { get; init; }

    /// <summary>
    /// Optional status filter. When null, operations in every status are returned.
    /// </summary>
    public WorkflowOperationStatus? Status { get; init; }

    /// <summary>
    /// One-based page number.
    /// </summary>
    public int Page { get; init; } = 1;

    /// <summary>
    /// Requested page size. Implementations clamp this to a server-side maximum.
    /// </summary>
    public int PageSize { get; init; } = 50;
}

/// <summary>
/// A single page of durable workflow operations ordered newest-first.
/// </summary>
public sealed record WorkflowOperationPage
{
    /// <summary>
    /// Items on this page, ordered newest-first by <see cref="WorkflowOperationRecord.CreatedAt"/>.
    /// </summary>
    public required IReadOnlyList<WorkflowOperationRecord> Items { get; init; }

    /// <summary>
    /// One-based page number that produced this page.
    /// </summary>
    public required int Page { get; init; }

    /// <summary>
    /// Effective page size applied when producing this page.
    /// </summary>
    public required int PageSize { get; init; }

    /// <summary>
    /// Total number of operations matching the filter within the materialized window.
    /// </summary>
    public required int TotalCount { get; init; }

    /// <summary>
    /// Whether at least one more page exists after this one.
    /// </summary>
    public bool HasMore { get; init; }
}
