// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.ReadOnlyProviders;

/// <summary>
/// Relationship store that rejects all relationship queries with
/// <see cref="NotSupportedException"/>. Registered by read-only feature providers (DuckDB,
/// MySQL/MariaDB) — both documented as not supporting relationship queries — so DI
/// consumers that require <see cref="IRelationshipStore"/> (for example
/// <c>Honua.Protocols.OData.Services.ODataSearchService</c>, a mandatory dependency wired
/// unconditionally regardless of provider) can activate.
/// </summary>
/// <remarks>
/// Found under honua-server#2947 (secondary-provider HTTP-stack GA proof): with no
/// <see cref="IRelationshipStore"/> registration at all, every OData request failed DI
/// activation outright under <c>DataSource:Provider=duckdb</c> or <c>mysql</c> — not just
/// requests that use OData's relationship/search surface. Only <c>Honua.Postgres</c> ever
/// registered a real implementation.
/// </remarks>
public sealed class ReadOnlyRelationshipStore : IRelationshipStore
{
    private readonly string _providerName;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlyRelationshipStore"/> class.
    /// </summary>
    /// <param name="providerName">
    /// Display name of the read-only provider, used in the rejection message
    /// (for example <c>"DuckDB"</c> or <c>"MySQL/MariaDB"</c>).
    /// </param>
    public ReadOnlyRelationshipStore(string providerName)
    {
        _providerName = providerName;
    }

    /// <inheritdoc />
    public Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{_providerName} provider does not support relationship queries.");
}
