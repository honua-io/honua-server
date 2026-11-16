// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

public sealed class NoOpChangeTracker : IChangeTracker
{
    public Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0L);

    public Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
        long sinceGeneration,
        int[] layerIds,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FeatureChange>>(Array.Empty<FeatureChange>());

    public Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
        long sinceGeneration,
        int[] layerIds,
        IReadOnlySet<long>? objectIds,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FeatureChange>>(Array.Empty<FeatureChange>());
}
