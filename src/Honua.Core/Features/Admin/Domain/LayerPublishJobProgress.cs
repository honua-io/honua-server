// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Admin.Domain;

/// <summary>
/// Poll document for a layer publish that exceeded the synchronous row threshold
/// (honua-server#5948). Reuses <see cref="OperationType.Publishing"/>.
/// </summary>
public sealed record LayerPublishJobProgress : IOperationProgress
{
    /// <summary>Job id returned to the caller.</summary>
    public required string OperationId { get; init; }

    /// <summary>Queued, processing, or a terminal state.</summary>
    public required OperationStatus Status { get; init; }

    /// <summary>Rows inserted into the canonical features table so far.</summary>
    public long RowsMaterialized { get; init; }

    /// <summary>Source rows counted when the publish was accepted.</summary>
    public long RowsTotal { get; init; }

    /// <summary>Published layer id once the publication transaction commits.</summary>
    public int? LayerId { get; init; }

    /// <summary>Secure connection the source table was published from.</summary>
    public Guid ConnectionId { get; init; }

    /// <summary>Route key the caller used, when it was not itself the connection id.</summary>
    public string? ConnectionKey { get; init; }

    /// <summary>When the job was accepted.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the job reached a terminal state.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Failure text when the publish did not commit.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Operator-facing phase.</summary>
    public string? CurrentPhase { get; init; }

    /// <summary>Warnings recorded for the publish. Empty for this job.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Rows materialized over the counted total, or 100 once completed.</summary>
    public double? PercentComplete => RowsTotal <= 0
        ? Status == OperationStatus.Completed ? 100d : null
        : Math.Min(100d, RowsMaterialized * 100d / RowsTotal);

    /// <summary>Elapsed time since the job was accepted.</summary>
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    /// <inheritdoc />
    public OperationType Type => OperationType.Publishing;
}
