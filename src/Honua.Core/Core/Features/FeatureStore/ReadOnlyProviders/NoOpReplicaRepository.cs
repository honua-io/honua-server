// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

public sealed class NoOpReplicaRepository : IReplicaRepository
{
    public bool SupportsReplicaPersistence => false;

    public Task UpsertAsync(ReplicaRecord record, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> TryUpdateSyncStateAsync(
        ReplicaRecord record,
        long expectedLastSyncGeneration,
        long expectedUploadBaseGeneration,
        CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<ReplicaRecord?> GetAsync(string replicaId, CancellationToken cancellationToken = default)
        => Task.FromResult<ReplicaRecord?>(null);

    public Task<IReadOnlyList<ReplicaRecord>> ListByServiceAsync(string serviceId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ReplicaRecord>>([]);

    public Task<bool> RemoveAsync(string replicaId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
