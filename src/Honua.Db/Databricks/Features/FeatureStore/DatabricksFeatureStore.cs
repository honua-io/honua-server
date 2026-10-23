// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Db.Databricks.Features.FeatureStore.Services;
using Honua.Db.Databricks.Features.Infrastructure;

namespace Honua.Db.Databricks.Features.FeatureStore;

/// <summary>
/// Read-only Databricks feature provider. Each query is translated to Databricks SQL
/// and submitted to a SQL Warehouse through the Statement Execution REST API.
/// </summary>
/// <remarks>
/// <para>Implements the canonical <see cref="IFeatureDataProvider"/> +
/// <see cref="IFeatureReader"/> surface so Databricks-backed layers participate in the
/// Honua catalog through the shared <c>FeatureProviderQueryRouter</c> just like the
/// other secondary providers. The provider is best-effort and read-only; writes,
/// statistics, top-features, bins, H3 aggregation, and native MVT/FlatGeobuf/Geobuf/GML
/// output are intentionally disabled. Shared formatters handle every output format above
/// the canonical <see cref="Feature"/> stream.</para>
/// <para>Spatial-function availability (<c>ST_*</c>) depends on the Databricks runtime /
/// DBSQL the warehouse runs; spatial-filter and extent queries are therefore best-effort.</para>
/// </remarks>
internal sealed class DatabricksFeatureStore : IFeatureDataProvider, IFeatureReader, IBindableFeatureDataProvider
{
    private static readonly FeatureProviderCapabilities _capabilities = new()
    {
        SupportsQuery = true,
        SupportsCount = true,
        SupportsExtent = true,
        SupportsStatistics = true,
        Edits = FeatureProviderEditCapabilities.ReadOnly,
        Outputs = new FeatureProviderOutputCapabilities
        {
            SupportsStreamingGeoJson = false,
            SupportsNativeMvt = false,
            SupportsNativeFlatGeobuf = false,
            SupportsNativeGeobuf = false,
            SupportsNativeGml = false,
        },
    };

    private readonly DatabricksLayerMappingRegistry _mappings;
    private readonly IDatabricksFeatureQueryBuilder _queryBuilder;
    private readonly IDatabricksFeatureDataAccess _dataAccess;
    private readonly LayerReadSecurityResolver? _readSecurity;
    private readonly FeatureProviderBinding? _binding;

    public DatabricksFeatureStore(
        DatabricksLayerMappingRegistry mappings,
        IDatabricksFeatureQueryBuilder queryBuilder,
        IDatabricksFeatureDataAccess dataAccess,
        LayerReadSecurityResolver? readSecurity = null)
        : this(mappings, queryBuilder, dataAccess, readSecurity, binding: null)
    {
    }

    private DatabricksFeatureStore(
        DatabricksLayerMappingRegistry mappings,
        IDatabricksFeatureQueryBuilder queryBuilder,
        IDatabricksFeatureDataAccess dataAccess,
        LayerReadSecurityResolver? readSecurity,
        FeatureProviderBinding? binding)
    {
        _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
        _queryBuilder = queryBuilder ?? throw new ArgumentNullException(nameof(queryBuilder));
        _dataAccess = dataAccess ?? throw new ArgumentNullException(nameof(dataAccess));
        _readSecurity = readSecurity;
        _binding = binding;
    }

    /// <inheritdoc />
    public IFeatureReader CreateReaderForBinding(FeatureProviderBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return new DatabricksFeatureStore(_mappings, _queryBuilder, _dataAccess, _readSecurity, binding);
    }

    /// <inheritdoc />
    public string ProviderName => DataProviderNames.Databricks;

    /// <inheritdoc />
    public FeatureProviderCapabilities Capabilities => _capabilities;

    /// <inheritdoc />
    public IFeatureReader Reader => this;

    /// <inheritdoc />
    public IFeatureWriter? Writer => null;

    /// <inheritdoc />
    public async Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var query = new FeatureQuery
        {
            ObjectIds = [featureId],
            Limit = 1,
        };
        var result = await QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        return result.Items.Length == 0 ? null : result.Items[0];
    }

    /// <inheritdoc />
    public async Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var mapping = await ResolveMappingAsync(layerId, cancellationToken).ConfigureAwait(false);
        var statement = _queryBuilder.BuildSelect(mapping, query);
        var features = await _dataAccess.ExecuteSelectAsync(mapping, statement, cancellationToken).ConfigureAwait(false);

        // When the page is smaller than the requested limit, the result is complete and
        // the page length is the total. Otherwise issue a COUNT for the accurate total so
        // callers can compute numberMatched / next-page links.
        long totalCount;
        var hasMore = false;
        if (query.Limit is int limit && limit > 0 && features.Length >= limit)
        {
            totalCount = await CountAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            var consumed = (long)(query.Offset ?? 0) + features.Length;
            hasMore = consumed < totalCount;
        }
        else
        {
            totalCount = (long)(query.Offset ?? 0) + features.Length;
        }

        return QueryResult<Feature>.Create(totalCount, features, hasMore);
    }

    /// <inheritdoc />
    public Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => Task.FromResult<byte[]?>(null);

    /// <inheritdoc />
    public async Task<ImmutableArray<long>> QueryObjectIdsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var mapping = await ResolveMappingAsync(layerId, cancellationToken).ConfigureAwait(false);
        var statement = _queryBuilder.BuildObjectIds(mapping, query);
        return await _dataAccess.ExecuteObjectIdsAsync(statement, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var mapping = await ResolveMappingAsync(layerId, cancellationToken).ConfigureAwait(false);
        var statement = _queryBuilder.BuildCount(mapping, query);
        return await _dataAccess.ExecuteCountAsync(statement, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
    {
        var mapping = await ResolveMappingAsync(layerId, cancellationToken).ConfigureAwait(false);
        var statement = _queryBuilder.BuildExtent(mapping, query);
        return await _dataAccess.ExecuteExtentAsync(mapping, statement, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EstimateResult> GetEstimatesAsync(int layerId, CancellationToken cancellationToken = default)
    {
        var countTask = CountAsync(layerId, default, cancellationToken);
        var extentTask = GetExtentAsync(layerId, null, cancellationToken);
        await Task.WhenAll(countTask, extentTask).ConfigureAwait(false);

        return new EstimateResult
        {
            EstimatedCount = await countTask.ConfigureAwait(false),
            Extent = await extentTask.ConfigureAwait(false),
        };
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var mapping = await ResolveMappingAsync(layerId, cancellationToken).ConfigureAwait(false);
        var statement = _queryBuilder.BuildStatistics(mapping, query);
        return await _dataAccess.ExecuteStatisticsAsync(statement, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId, string fieldName, TemporalPropertyType propertyType, CancellationToken cancellationToken = default)
        => throw NotSupported(nameof(GetTemporalExtentAsync), layerId);

    /// <inheritdoc />
    public Task<QueryResult<Feature>> QueryTopFeaturesAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => throw NotSupported(nameof(QueryTopFeaturesAsync), layerId);

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
        int layerId, FeatureQuery query, DateBinDefinition dateBin, CancellationToken cancellationToken = default)
        => throw NotSupported(nameof(QueryDateBinsAsync), layerId);

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(
        int layerId, FeatureQuery query, BinDefinition binDefinition, CancellationToken cancellationToken = default)
        => throw NotSupported(nameof(QueryBinsAsync), layerId);

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(
        int layerId, FeatureQuery query, H3AggregationQuery h3Query, CancellationToken cancellationToken = default)
        => throw NotSupported(nameof(QueryH3Async), layerId);

    /// <summary>
    /// Resolves the layer mapping for a read. Every read path resolves its layer here, so this
    /// is the single seam that refuses a read whose layer carries a read policy this provider
    /// cannot enforce (permanent filter, row-level security predicate or field masks).
    /// </summary>
    private async Task<DatabricksLayerMapping> ResolveMappingAsync(int layerId, CancellationToken cancellationToken)
    {
        if (_readSecurity is not null)
        {
            await _readSecurity
                .EnsureNoUnenforcedPolicyAsync("Databricks", layerId, _binding?.Resource, rejectPermanentFilter: true, cancellationToken)
                .ConfigureAwait(false);
        }

        return _mappings.Resolve(layerId);
    }

    private static NotSupportedException NotSupported(string operation, int layerId)
        => new($"Databricks provider does not support '{operation}' for layer {layerId} in this slice.");
}
