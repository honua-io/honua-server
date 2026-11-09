// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Temporal.Domain;

namespace Honua.Core.Features.Temporal.Abstractions;

public interface ITemporalHistoryStore
{
    Task<IReadOnlyList<TemporalChangeRecord>> GetFeatureRevisionsAsync(
        int storageLayerId,
        long objectId,
        long afterGeneration,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TemporalChangeRecord>> GetChangesInWindowAsync(
        int storageLayerId,
        long fromGeneration,
        long toGeneration,
        long afterObjectId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<int> CountChangedFeaturesAsync(
        int storageLayerId,
        long fromGeneration,
        long toGeneration,
        CancellationToken cancellationToken = default);

    Task<long?> ResolveCheckpointGenerationAsync(
        int storageLayerId,
        TemporalCheckpointKind kind,
        string value,
        long upperBoundGeneration,
        CancellationToken cancellationToken = default);
}
