// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IReplicaRepository
{
    bool SupportsReplicaPersistence => true;

    Task UpsertAsync(ReplicaRecord record, CancellationToken cancellationToken = default);

    async Task<ReplicaRecord> RegisterAtCurrentGenerationAsync(
        ReplicaRecord record,
        CancellationToken cancellationToken = default)
    {
        await UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    Task<bool> TryUpdateSyncStateAsync(
        ReplicaRecord record,
        long expectedLastSyncGeneration,
        long expectedUploadBaseGeneration,
        CancellationToken cancellationToken = default);

    Task<ReplicaRecord?> GetAsync(string replicaId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReplicaRecord>> ListByServiceAsync(string serviceId, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default);
}
