// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.MySql.Features.FeatureStore;

/// <summary>
/// MySQL/MariaDB read-only feature store. Provides query, count, and extent paths over
/// user-managed tables; statistics, edits, MVT, and KNN paths are intentionally
/// declared unsupported in <see cref="FeatureProviderCapabilities.ReadOnlyMySql"/>.
/// </summary>
internal sealed class MySqlFeatureStore :
    IFeatureDataProvider,
    IFeatureReader,
    IPagedFeatureReader
{
    private readonly IFeatureQueryBuilder _queryBuilder;
    private readonly IFeatureDataAccess _dataAccess;

    public MySqlFeatureStore(
        IFeatureQueryBuilder queryBuilder,
        IFeatureDataAccess dataAccess)
    {
        _queryBuilder = queryBuilder ?? throw new ArgumentNullException(nameof(queryBuilder));
        _dataAccess = dataAccess ?? throw new ArgumentNullException(nameof(dataAccess));
    }

    /// <inheritdoc />
    public string ProviderName => DataProviderNames.MySql;

    /// <inheritdoc />
    public FeatureProviderCapabilities Capabilities => FeatureProviderCapabilities.ReadOnlyMySql;

    /// <inheritdoc />
    public IFeatureReader Reader => this;

    /// <inheritdoc />
    public IFeatureWriter? Writer => null;

    /// <inheritdoc />
    public Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
        => _dataAccess.GetFeatureAsync(layerId, featureId, cancellationToken);

    /// <inheritdoc />
    public async Task<QueryResult<Feature>> QueryAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var countQuery = _queryBuilder.BuildCountQuery(layerId, query);
        var totalCount = await _dataAccess.ExecuteCountQueryAsync(countQuery, query, layerId, cancellationToken).ConfigureAwait(false);

        if (totalCount == 0)
        {
            return QueryResult<Feature>.Empty();
        }

        var selectQuery = _queryBuilder.BuildSelectQuery(layerId, query);
        var features = await _dataAccess.ExecuteSelectQueryAsync(selectQuery, query, layerId, cancellationToken).ConfigureAwait(false);

        var hasMore = false;
        if (query.Limit.HasValue && query.Limit.Value > 0)
        {
            var offset = query.Offset ?? 0;
            hasMore = totalCount > offset + features.Length;
        }

        return QueryResult<Feature>.Create(totalCount, features, hasMore);
    }

    /// <inheritdoc />
    public Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => Task.FromResult<byte[]?>(null);

    /// <inheritdoc />
    public async Task<ImmutableArray<long>> QueryObjectIdsAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var objectIdsQuery = _queryBuilder.BuildObjectIdsQuery(layerId, query);
        return await _dataAccess.ExecuteSelectObjectIdsQueryAsync(objectIdsQuery, query, layerId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var countQuery = _queryBuilder.BuildCountQuery(layerId, query);
        return await _dataAccess.ExecuteCountQueryAsync(countQuery, query, layerId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FeatureExtent?> GetExtentAsync(
        int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
    {
        var effective = query ?? new FeatureQuery();
        var extentQuery = _queryBuilder.BuildExtentQuery(layerId, effective);
        return await _dataAccess.GetExtentAsync(layerId, extentQuery, effective, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Aggregate statistics queries are not supported by the MySQL/MariaDB provider in this slice. " +
            "Check FeatureProviderCapabilities.SupportsStatistics before invoking.");

    /// <inheritdoc />
    public Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId, string fieldName, FieldType fieldType, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Temporal extent queries are not supported by the MySQL/MariaDB provider in this slice.");

    /// <inheritdoc />
    public async Task<EstimateResult> GetEstimatesAsync(int layerId, CancellationToken cancellationToken = default)
    {
        var countTask = CountAsync(layerId, new FeatureQuery(), cancellationToken);
        var extentTask = GetExtentAsync(layerId, null, cancellationToken);
        await Task.WhenAll(countTask, extentTask).ConfigureAwait(false);

        return new EstimateResult
        {
            EstimatedCount = await countTask.ConfigureAwait(false),
            Extent = await extentTask.ConfigureAwait(false)
        };
    }

    /// <inheritdoc />
    public Task<QueryResult<Feature>> QueryTopFeaturesAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Top-features queries are not supported by the MySQL/MariaDB provider in this slice.");

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
        int layerId, FeatureQuery query, DateBinDefinition dateBin, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Date-binning queries are not supported by the MySQL/MariaDB provider in this slice.");

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(
        int layerId, FeatureQuery query, BinDefinition binDefinition, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Numeric-binning queries are not supported by the MySQL/MariaDB provider in this slice.");

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(
        int layerId, FeatureQuery query, H3AggregationQuery h3Query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "H3 hexagonal aggregation is not supported by the MySQL/MariaDB provider.");

    /// <inheritdoc />
    public async Task<PagedQueryResult<Feature>> QueryPageAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.Limit.HasValue || query.Limit.Value == int.MaxValue)
        {
            var result = await QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            return PagedQueryResult<Feature>.Create(result.Items, result.HasMoreResults, result.TotalCount);
        }

        // Fetch one extra row to detect whether more results exist without an exact COUNT.
        var pageQuery = query with { Limit = query.Limit.Value + 1 };
        var selectQuery = _queryBuilder.BuildSelectQuery(layerId, pageQuery);
        var features = await _dataAccess.ExecuteSelectQueryAsync(selectQuery, pageQuery, layerId, cancellationToken).ConfigureAwait(false);

        if (features.Length == 0)
        {
            return PagedQueryResult<Feature>.Empty();
        }

        var hasMore = features.Length > query.Limit.Value;
        var items = hasMore ? features.Take(query.Limit.Value).ToImmutableArray() : features;

        return PagedQueryResult<Feature>.Create(items, hasMore);
    }
}
