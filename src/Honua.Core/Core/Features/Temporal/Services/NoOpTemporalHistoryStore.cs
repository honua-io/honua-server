// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Temporal.Abstractions;
using Honua.Core.Features.Temporal.Domain;

namespace Honua.Core.Features.Temporal.Services;

public sealed class NoOpTemporalHistoryStore : ITemporalHistoryStore
{
    private static readonly IReadOnlyList<TemporalChangeRecord> Empty = Array.Empty<TemporalChangeRecord>();

    public Task<IReadOnlyList<TemporalChangeRecord>> GetFeatureRevisionsAsync(
        int storageLayerId,
        long objectId,
        long afterGeneration,
        int limit,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Empty);

    public Task<IReadOnlyList<TemporalChangeRecord>> GetChangesInWindowAsync(
        int storageLayerId,
        long fromGeneration,
        long toGeneration,
        long afterObjectId,
        int limit,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Empty);

    public Task<int> CountChangedFeaturesAsync(
        int storageLayerId,
        long fromGeneration,
        long toGeneration,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public Task<long?> ResolveCheckpointGenerationAsync(
        int storageLayerId,
        TemporalCheckpointKind kind,
        string value,
        long upperBoundGeneration,
        CancellationToken cancellationToken = default)
        => Task.FromResult<long?>(null);
}
