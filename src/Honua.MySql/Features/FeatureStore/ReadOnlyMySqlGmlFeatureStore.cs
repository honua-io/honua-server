// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.MySql.Features.FeatureStore;

/// <summary>
/// GML feature store that rejects all GML query operations.
/// Registered when the MySQL/MariaDB provider is active since the slice does not support
/// native GML encoding.
/// </summary>
internal sealed class ReadOnlyMySqlGmlFeatureStore : IGmlFeatureStore
{
    /// <inheritdoc />
    public Task<QueryResult<GmlFeature>> QueryGmlAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("MySQL/MariaDB provider does not support GML feature queries.");
}
