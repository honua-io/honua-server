// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Admin.Domain;

public sealed record LayerPublishJobProgress : IOperationProgress
{
    public required string OperationId { get; init; }

    public required OperationStatus Status { get; init; }

    public long RowsMaterialized { get; init; }

    public long RowsTotal { get; init; }

    public int? LayerId { get; init; }

    public long? MetadataRevision { get; init; }

    public Guid ConnectionId { get; init; }

    public string? ConnectionKey { get; init; }

    public string? TenantId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? ErrorMessage { get; init; }

    public string? CurrentPhase { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public double? PercentComplete => RowsTotal <= 0
        ? Status == OperationStatus.Completed ? 100d : null
        : Math.Min(100d, RowsMaterialized * 100d / RowsTotal);

    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    public OperationType Type => OperationType.Publishing;
}
