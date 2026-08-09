// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.MySql;

namespace Honua.MySql.Features.FeatureStore;

/// <summary>
/// MySQL/MariaDB read-only feature store. Provides query, count, and extent paths over
/// user-managed tables; statistics, edits, MVT, and KNN paths are intentionally
/// declared unsupported in <see cref="FeatureProviderCapabilities.ReadOnlyMySql"/>.
/// Streaming methods iterate the underlying data access in pages so DI consumers that
/// require <see cref="IStreamingFeatureStore"/> can resolve under <c>DataSource:Provider=mysql</c>.
/// </summary>
internal sealed class MySqlFeatureStore :
    IFeatureDataProvider,
    IFeatureReader,
    IPagedFeatureReader,
    IStreamingFeatureStore
{
    private const int DefaultStreamingPageSize = 1000;

    private readonly IFeatureQueryBuilder _queryBuilder;
    private readonly IFeatureDataAccess _dataAccess;
    private readonly IMetadataV2GraphProvider? _v2Provider;
    private readonly IFilterExpressionService? _filterExpressionService;

    public MySqlFeatureStore(
        IFeatureQueryBuilder queryBuilder,
        IFeatureDataAccess dataAccess,
        IMetadataV2GraphProvider? v2Provider = null,
        IFilterExpressionService? filterExpressionService = null)
    {
        _queryBuilder = queryBuilder ?? throw new ArgumentNullException(nameof(queryBuilder));
        _dataAccess = dataAccess ?? throw new ArgumentNullException(nameof(dataAccess));
        _v2Provider = v2Provider;
        _filterExpressionService = filterExpressionService;
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
    public async Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var query = await ApplyPermanentFilterAsync(layerId, new FeatureQuery(), cancellationToken).ConfigureAwait(false);
        if (query.EnforcedSqlFilter is null)
        {
            return await _dataAccess.GetFeatureAsync(layerId, featureId, cancellationToken).ConfigureAwait(false);
        }
        var pageQuery = query with { ObjectIds = ImmutableArray.Create(featureId), Limit = 1 };
        var selectQuery = _queryBuilder.BuildSelectQuery(layerId, pageQuery);
        var features = await _dataAccess.ExecuteSelectQueryAsync(selectQuery, pageQuery, layerId, cancellationToken).ConfigureAwait(false);
        return features.IsDefaultOrEmpty ? null : features[0];
    }

    /// <inheritdoc />
    public async Task<QueryResult<Feature>> QueryAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
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
        => throw MySqlUnsupportedFeature.Create(
            "Native FlatGeobuf output is not supported by the MySQL/MariaDB provider in this slice. " +
            "The FeatureServer guard rejects f=fgb against this provider before reaching the store; " +
            "this throw is defense-in-depth in case a caller invokes the reader directly.");

    /// <inheritdoc />
    public async Task<ImmutableArray<long>> QueryObjectIdsAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var objectIdsQuery = _queryBuilder.BuildObjectIdsQuery(layerId, query);
        return await _dataAccess.ExecuteSelectObjectIdsQueryAsync(objectIdsQuery, query, layerId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        var countQuery = _queryBuilder.BuildCountQuery(layerId, query);
        return await _dataAccess.ExecuteCountQueryAsync(countQuery, query, layerId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FeatureExtent?> GetExtentAsync(
        int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
    {
        var effective = await ApplyPermanentFilterAsync(layerId, query ?? new FeatureQuery(), cancellationToken).ConfigureAwait(false);
        var extentQuery = _queryBuilder.BuildExtentQuery(layerId, effective);
        return await _dataAccess.GetExtentAsync(layerId, extentQuery, effective, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => throw MySqlUnsupportedFeature.Create(
            "Aggregate statistics queries are not supported by the MySQL/MariaDB provider in this slice. " +
            "Check FeatureProviderCapabilities.SupportsStatistics before invoking.");

    /// <inheritdoc />
    public Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId, string fieldName, TemporalPropertyType propertyType, CancellationToken cancellationToken = default)
        => throw MySqlUnsupportedFeature.Create(
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
        => throw MySqlUnsupportedFeature.Create(
            "Top-features queries are not supported by the MySQL/MariaDB provider in this slice.");

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
        int layerId, FeatureQuery query, DateBinDefinition dateBin, CancellationToken cancellationToken = default)
        => throw MySqlUnsupportedFeature.Create(
            "Date-binning queries are not supported by the MySQL/MariaDB provider in this slice.");

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(
        int layerId, FeatureQuery query, BinDefinition binDefinition, CancellationToken cancellationToken = default)
        => throw MySqlUnsupportedFeature.Create(
            "Numeric-binning queries are not supported by the MySQL/MariaDB provider in this slice.");

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(
        int layerId, FeatureQuery query, H3AggregationQuery h3Query, CancellationToken cancellationToken = default)
        => throw MySqlUnsupportedFeature.Create(
            "H3 hexagonal aggregation is not supported by the MySQL/MariaDB provider.");

    /// <inheritdoc />
    public async Task<PagedQueryResult<Feature>> QueryPageAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        query = await ApplyPermanentFilterAsync(layerId, query, cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc />
    public async IAsyncEnumerable<Feature> StreamFeaturesAsync(
        int layerId,
        FeatureQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var batch in StreamFeatureBatchesAsync(layerId, query, DefaultStreamingPageSize, cancellationToken).ConfigureAwait(false))
        {
            foreach (var feature in batch)
            {
                yield return feature;
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IReadOnlyList<Feature>> StreamFeatureBatchesAsync(
        int layerId,
        FeatureQuery query,
        int batchSize = 1000,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The slice has no native cursor/streaming over MySQL connections, so iterate the
        // existing select path in fixed-size pages. Pagination preserves the user-supplied
        // LIMIT/OFFSET when present. The query builder injects an ascending primary-key
        // ORDER BY when LIMIT or OFFSET is set without a caller-supplied OrderBy — every
        // page query below has Limit set, so each emitted SELECT is deterministically
        // ordered without the store doing any extra bookkeeping.
        var effectiveBatchSize = batchSize > 0 ? batchSize : DefaultStreamingPageSize;
        var requestedLimit = query.Limit;
        var startOffset = query.Offset ?? 0;
        var emitted = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Treat any present Limit as a budget — including 0. A FeatureQuery with
            // Limit = 0 is a valid request for "no rows" (used by capability probes /
            // metadata fetches that reuse the streaming surface), and must short-circuit
            // before issuing a SELECT. Only a missing limit means "stream all rows".
            var remainingBudget = requestedLimit.HasValue
                ? requestedLimit.Value - emitted
                : (int?)null;

            if (remainingBudget is <= 0)
            {
                yield break;
            }

            var pageSize = remainingBudget.HasValue
                ? Math.Min(effectiveBatchSize, remainingBudget.Value)
                : effectiveBatchSize;

            var pageQuery = query with
            {
                Limit = pageSize,
                Offset = startOffset + emitted
            };

            var selectQuery = _queryBuilder.BuildSelectQuery(layerId, pageQuery);
            var page = await _dataAccess
                .ExecuteSelectQueryAsync(selectQuery, pageQuery, layerId, cancellationToken)
                .ConfigureAwait(false);

            if (page.IsDefaultOrEmpty || page.Length == 0)
            {
                yield break;
            }

            yield return page;
            emitted += page.Length;

            if (page.Length < pageSize)
            {
                yield break;
            }
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<GmlFeature> StreamGmlFeaturesAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
        => throw MySqlUnsupportedFeature.Create(
            "Streaming GML features is not supported by the MySQL/MariaDB provider in this slice.");

    /// <summary>
    /// Resolves and applies the layer's permanent (row-visibility) filter to the query.
    /// Idempotent: queries already carrying an enforced filter are returned unchanged.
    /// </summary>
    private async Task<FeatureQuery> ApplyPermanentFilterAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        if (query.EnforcedSqlFilter != null)
        {
            return query;
        }

        var enforcedFilter = await PermanentFilterResolver
            .ResolveAsync(_v2Provider, _filterExpressionService, layerId, cancellationToken)
            .ConfigureAwait(false);

        if (enforcedFilter != null)
        {
            query = query with { EnforcedSqlFilter = enforcedFilter };
        }

        return query;
    }
}
