// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

public sealed record MigrationBatchChildRecord
{
    public required string BatchId { get; init; }

    public required int Ordinal { get; init; }

    public required string SourceResourceId { get; init; }

    public required string ServiceUrl { get; init; }

    public required int SourceLayerId { get; init; }

    public required string TableName { get; init; }

    public string? TargetSchema { get; init; }

    public string? ServiceName { get; init; }

    public IReadOnlyList<string> DependsOn { get; init; } = [];

    public required MigrationBatchChildStatus Status { get; init; }

    public string? JobId { get; init; }

    public int? PublishedLayerId { get; init; }

    public string? StatusNote { get; init; }

    public string? FidelityVerdict { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public enum MigrationBatchChildStatus
{
    Pending,

    Running,

    Succeeded,

    Failed,

    NeedsReview,

    Cancelled
}
