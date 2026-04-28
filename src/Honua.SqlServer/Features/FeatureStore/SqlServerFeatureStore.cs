// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.SqlServer.Features.FeatureStore.Services;

namespace Honua.SqlServer.Features.FeatureStore;

/// <summary>
/// Read-only SQL Server feature provider for the spatial provider thin slice (#850).
/// </summary>
/// <remarks>
/// <para>Implements the canonical <see cref="IFeatureDataProvider"/> + <see cref="IFeatureReader"/>
/// surface for layers backed by SQL Server <c>geometry</c>/<c>geography</c> columns. Edits, native
/// MVT, native FlatGeobuf/Geobuf/GML, statistics aggregates, top-features, bins, and H3 are
/// deliberately disabled on this slice and are advertised as such via
/// <see cref="FeatureProviderCapabilities"/>.</para>
/// <para>Layer/storage configuration flows through the existing <see cref="LayerStorageMapping"/>
/// model resolved from <see cref="ILayerCatalog"/>; the provider does not introduce its own
/// model types.</para>
/// </remarks>
internal sealed class SqlServerFeatureStore : IFeatureDataProvider, IFeatureReader
{
    private static readonly FeatureProviderCapabilities _capabilities = new()
    {
        SupportsQuery = true,
        SupportsCount = true,
        SupportsExtent = true,
        SupportsStatistics = false,
        Edits = FeatureProviderEditCapabilities.ReadOnly,
        Outputs = new FeatureProviderOutputCapabilities
        {
            SupportsStreamingGeoJson = false,
            SupportsNativeMvt = false,
            SupportsNativeFlatGeobuf = false,
            SupportsNativeGeobuf = false,
            SupportsNativeGml = false
        }
    };

    private readonly SqlServerFeatureDataAccess _dataAccess;
    private readonly ILayerCatalog _layerCatalog;

    public SqlServerFeatureStore(
        SqlServerFeatureDataAccess dataAccess,
        ILayerCatalog layerCatalog)
    {
        _dataAccess = dataAccess ?? throw new ArgumentNullException(nameof(dataAccess));
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
    }

    /// <inheritdoc />
    public string ProviderName => DataProviderNames.SqlServer;

    /// <inheritdoc />
    public FeatureProviderCapabilities Capabilities => _capabilities;

    /// <inheritdoc />
    public IFeatureReader Reader => this;

    /// <inheritdoc />
    public IFeatureWriter? Writer => null;

    /// <inheritdoc />
    public async Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var (mapping, attributeColumns) = await ResolveLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var query = new FeatureQuery
        {
            ObjectIds = [featureId],
            Limit = 1
        };

        var sql = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, attributeColumns);
        var features = await _dataAccess.ExecuteSelectAsync(mapping, sql, attributeColumns, cancellationToken).ConfigureAwait(false);
        return features.Length == 0 ? null : features[0];
    }

    /// <inheritdoc />
    public async Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var (mapping, attributeColumns) = await ResolveLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var sql = SqlServerFeatureQueryBuilder.BuildSelectQuery(mapping, query, attributeColumns);
        var features = await _dataAccess.ExecuteSelectAsync(mapping, sql, attributeColumns, cancellationToken).ConfigureAwait(false);

        var hasMore = query.Limit.HasValue && features.Length >= query.Limit.Value;
        return QueryResult<Feature>.Create(features.Length, features, hasMore);
    }

    /// <inheritdoc />
    public Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        // The provider does not advertise native FlatGeobuf; return null so the server adapter
        // falls back to the in-process formatter.
        return Task.FromResult<byte[]?>(null);
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<long>> QueryObjectIdsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var (mapping, _) = await ResolveLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var sql = SqlServerFeatureQueryBuilder.BuildObjectIdsQuery(mapping, query);
        return await _dataAccess.ExecuteObjectIdsAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var (mapping, _) = await ResolveLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var sql = SqlServerFeatureQueryBuilder.BuildCountQuery(mapping, query);
        return await _dataAccess.ExecuteCountAsync(mapping, sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
    {
        var (mapping, _) = await ResolveLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var sql = SqlServerFeatureQueryBuilder.BuildExtentQuery(mapping, query);
        return await _dataAccess.ExecuteExtentAsync(mapping, sql, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => throw NotSupported(nameof(QueryStatisticsAsync), layerId);

    /// <inheritdoc />
    public Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId, string fieldName, FieldType fieldType, CancellationToken cancellationToken = default)
        => throw NotSupported(nameof(GetTemporalExtentAsync), layerId);

    /// <inheritdoc />
    public async Task<EstimateResult> GetEstimatesAsync(int layerId, CancellationToken cancellationToken = default)
    {
        var (mapping, _) = await ResolveLayerAsync(layerId, cancellationToken).ConfigureAwait(false);

        var emptyQuery = new FeatureQuery();
        var countTask = _dataAccess.ExecuteCountAsync(mapping, SqlServerFeatureQueryBuilder.BuildCountQuery(mapping, emptyQuery), cancellationToken);
        var extentTask = _dataAccess.ExecuteExtentAsync(mapping, SqlServerFeatureQueryBuilder.BuildExtentQuery(mapping, emptyQuery), cancellationToken);
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

    private async Task<(SqlServerLayerMapping Mapping, IReadOnlyList<string> AttributeColumns)> ResolveLayerAsync(
        int layerId,
        CancellationToken cancellationToken)
    {
        var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Layer {layerId} is not registered in the catalog.");

        if (layer.StorageMapping is null)
        {
            throw new InvalidOperationException(
                $"Layer {layerId} does not declare a runtime storage mapping; SQL Server provider requires LayerStorageMapping.");
        }

        var mapping = SqlServerLayerMapping.FromStorage(layerId, layer.StorageMapping);
        var attributeColumns = layer.Fields
            .Where(f => !f.IsGeometry && !f.Name.Equals(mapping.PrimaryKeyColumn, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Name)
            .ToArray();

        return (mapping, attributeColumns);
    }

    private static NotSupportedException NotSupported(string operation, int layerId)
        => new($"SQL Server provider does not support '{operation}' for layer {layerId} in this slice.");
}
