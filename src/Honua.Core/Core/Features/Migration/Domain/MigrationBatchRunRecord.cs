// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

// The source URL must already have userinfo, query, and fragment stripped before it is stored.
public sealed record MigrationBatchRunRecord
{
    public required string BatchId { get; init; }

    public required string SourceKind { get; init; }

    public string SourceUrl { get; init; } = string.Empty;

    public string? SourceDisplayName { get; init; }

    public required MigrationBatchRunStatus Status { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public int TotalChildren { get; init; }

    public int SucceededChildren { get; init; }

    public int FailedChildren { get; init; }

    public int CancelledChildren { get; init; }

    public bool ApplyRelationships { get; init; }

    public bool RelationshipsApplied { get; init; }

    public string? StatusNote { get; init; }

    public string? FidelityVerdict { get; init; }

    public MigrationFidelityDifference[] FidelityDifferences { get; init; } = [];
}

public enum MigrationBatchRunStatus
{
    Running,

    Succeeded,

    Failed,

    Cancelled,

    NeedsReview
}
