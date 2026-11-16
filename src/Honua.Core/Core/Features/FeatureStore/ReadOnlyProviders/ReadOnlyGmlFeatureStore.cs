// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

public sealed class ReadOnlyGmlFeatureStore : IGmlFeatureStore
{
    private readonly string _providerName;

    public ReadOnlyGmlFeatureStore(string providerName)
    {
        _providerName = providerName;
    }

    public Task<QueryResult<GmlFeature>> QueryGmlAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{_providerName} provider does not support GML feature queries.");
}
