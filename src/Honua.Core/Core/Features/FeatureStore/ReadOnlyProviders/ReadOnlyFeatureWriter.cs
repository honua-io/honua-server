// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

public sealed class ReadOnlyFeatureWriteException : NotSupportedException
{
    public ReadOnlyFeatureWriteException(string message)
        : base(message)
    {
    }
}

public sealed class ReadOnlyFeatureWriter : IFeatureWriter
{
    private readonly string _providerName;

    public ReadOnlyFeatureWriter(string providerName)
    {
        _providerName = providerName;
    }

    public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
        => throw new ReadOnlyFeatureWriteException($"{_providerName} provider is read-only. Feature creation is not supported.");

    public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
        => throw new ReadOnlyFeatureWriteException($"{_providerName} provider is read-only. Feature updates are not supported.");

    public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
        => throw new ReadOnlyFeatureWriteException($"{_providerName} provider is read-only. Feature deletion is not supported.");

    public Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken = default)
        => throw new ReadOnlyFeatureWriteException($"{_providerName} provider is read-only. Batch edits are not supported.");
}
