// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Optional feature store capability for returning Geobuf-encoded feature collections.
/// </summary>
public interface IGeobufFeatureStore
{
    /// <summary>
    /// Queries features as a Geobuf payload.
    /// </summary>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="query">Query parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Geobuf payload, or null when no features match.</returns>
    Task<byte[]?> QueryGeobufAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);
}
