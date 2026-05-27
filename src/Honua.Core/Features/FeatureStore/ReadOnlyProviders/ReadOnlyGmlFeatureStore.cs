// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

/// <summary>
/// GML feature store that rejects all GML query operations with <see cref="NotSupportedException"/>.
/// Registered by feature providers (DuckDB, MySQL/MariaDB) that do not support native GML
/// encoding so the WFS 2.0 path can activate under DI.
/// </summary>
public sealed class ReadOnlyGmlFeatureStore : IGmlFeatureStore
{
    private readonly string _providerName;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlyGmlFeatureStore"/> class.
    /// </summary>
    /// <param name="providerName">
    /// Display name of the provider, used in the rejection message
    /// (for example <c>"DuckDB"</c> or <c>"MySQL/MariaDB"</c>).
    /// </param>
    public ReadOnlyGmlFeatureStore(string providerName)
    {
        _providerName = providerName;
    }

    /// <inheritdoc />
    public Task<QueryResult<GmlFeature>> QueryGmlAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{_providerName} provider does not support GML feature queries.");
}
