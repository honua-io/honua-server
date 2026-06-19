// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Redshift.Features.FeatureStore.Services;

namespace Honua.Redshift.Features.FeatureStore;

/// <summary>
/// Read-only Amazon Redshift feature provider (#1712).
/// </summary>
/// <remarks>
/// <para>Implements the canonical <see cref="IFeatureDataProvider"/> + <see cref="IFeatureReader"/>
/// surface for layers backed by Redshift <c>GEOMETRY</c>/<c>GEOGRAPHY</c> columns. Connectivity uses
/// Npgsql (Redshift speaks the PostgreSQL wire protocol), but spatial behavior relies on Redshift's
/// native spatial SQL functions rather than PostGIS. Edits, native MVT, native FlatGeobuf/Geobuf/GML,
/// statistics aggregates, top-features, bins, and H3 are deliberately disabled on this slice and are
/// advertised as such via <see cref="FeatureProviderCapabilities"/>.</para>
/// <para>Layer/storage configuration flows through the Metadata v2 provider binding passed by
/// <see cref="FeatureProviderQueryRouter"/>.</para>
/// </remarks>
internal sealed class RedshiftFeatureStore : IFeatureDataProvider, IFeatureReader, IBindableFeatureDataProvider
{
    private static readonly FeatureProviderCapabilities _capabilities = FeatureProviderCapabilities.ReadOnlyMySql;

    private readonly RedshiftFeatureDataAccess _dataAccess;
    private readonly FeatureProviderBinding? _binding;
    private readonly DataConnection? _boundConnection;

    public RedshiftFeatureStore(RedshiftFeatureDataAccess dataAccess)
        : this(dataAccess, binding: null)
    {
    }

    private RedshiftFeatureStore(
        RedshiftFeatureDataAccess dataAccess,
        FeatureProviderBinding? binding)
    {
        _dataAccess = dataAccess ?? throw new ArgumentNullException(nameof(dataAccess));
        _binding = binding;
        _boundConnection = binding?.Connection;
    }

    /// <inheritdoc />
    public string ProviderName => DataProviderNames.Redshift;

    /// <inheritdoc />
    public FeatureProviderCapabilities Capabilities => _capabilities;

    /// <inheritdoc />
    public IFeatureReader Reader => this;

    /// <inheritdoc />
    public IFeatureWriter? Writer => null;

    /// <inheritdoc />
    public IFeatureReader CreateReaderForBinding(FeatureProviderBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return new RedshiftFeatureStore(_dataAccess, binding);
    }

    /// <inheritdoc />
    public async Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var (mapping, attributeColumns) = await ResolveLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var query = new FeatureQuery
        {
            ObjectIds = [featureId],
            Limit = 1
        };

        var sql = RedshiftFeatureQueryBuilder.BuildSelectQuery(mapping, query, attributeColumns);
        var features = await _dataAccess.ExecuteSelectAsync(mapping, sql, attributeColumns, _boundConnection, cancellationToken).ConfigureAwait(false);
        return features.Length == 0 ? null : features[0];
    }

    /// <inheritdoc />
    public async Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var (mapping, attributeColumns) = await ResolveLayerAsync(layerId, cancellationToken).ConfigureAwait(false);

        // Probe one extra row when a Limit is requested so HasMoreResults is reported correctly
        // without paying for a separate COUNT query. The probe row is trimmed before returning.
        var requestedLimit = query.Limit;
        var probeQuery = requestedLimit.HasValue
            ? query with { Limit = requestedLimit.Value + 1 }
            : query;

        var sql = RedshiftFeatureQueryBuilder.BuildSelectQuery(mapping, probeQuery, attributeColumns);
        var features = await _dataAccess.ExecuteSelectAsync(mapping, sql, attributeColumns, _boundConnection, cancellationToken).ConfigureAwait(false);

        var hasMore = false;
        if (requestedLimit.HasValue && features.Length > requestedLimit.Value)
        {
            hasMore = true;
            features = features.RemoveAt(features.Length - 1);
        }

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
        var sql = RedshiftFeatureQueryBuilder.BuildObjectIdsQuery(mapping, query);
        return await _dataAccess.ExecuteObjectIdsAsync(mapping, sql, _boundConnection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var (mapping, _) = await ResolveLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var sql = RedshiftFeatureQueryBuilder.BuildCountQuery(mapping, query);
        return await _dataAccess.ExecuteCountAsync(mapping, sql, _boundConnection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
    {
        var (mapping, _) = await ResolveLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var sql = RedshiftFeatureQueryBuilder.BuildExtentQuery(mapping, query);
        return await _dataAccess.ExecuteExtentAsync(mapping, sql, _boundConnection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => throw NotSupported(nameof(QueryStatisticsAsync), layerId);

    /// <inheritdoc />
    public Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId, string fieldName, TemporalPropertyType propertyType, CancellationToken cancellationToken = default)
        => throw NotSupported(nameof(GetTemporalExtentAsync), layerId);

    /// <inheritdoc />
    public async Task<EstimateResult> GetEstimatesAsync(int layerId, CancellationToken cancellationToken = default)
    {
        var (mapping, _) = await ResolveLayerAsync(layerId, cancellationToken).ConfigureAwait(false);

        var emptyQuery = new FeatureQuery();
        var countTask = _dataAccess.ExecuteCountAsync(mapping, RedshiftFeatureQueryBuilder.BuildCountQuery(mapping, emptyQuery), _boundConnection, cancellationToken);
        var extentTask = _dataAccess.ExecuteExtentAsync(mapping, RedshiftFeatureQueryBuilder.BuildExtentQuery(mapping, emptyQuery), _boundConnection, cancellationToken);
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

    private Task<(RedshiftLayerMapping Mapping, IReadOnlyList<string> AttributeColumns)> ResolveLayerAsync(
        int layerId,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var binding = _binding
            ?? throw new InvalidOperationException(
                "Redshift provider reads require a Metadata v2 provider binding; route requests through FeatureProviderQueryRouter.");
        if (binding.StorageLayerId != layerId)
        {
            throw new InvalidOperationException(
                $"Redshift provider binding targets storage layer {binding.StorageLayerId}, not requested layer {layerId}.");
        }

        var mapping = RedshiftLayerMapping.FromStorage(layerId, binding.StorageMapping);
        var attributeColumns = binding.Resource.SchemaFields
            .Where(f => f.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography)
                && !f.Name.Equals(mapping.PrimaryKeyColumn, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Name)
            .ToArray();

        return Task.FromResult<(RedshiftLayerMapping, IReadOnlyList<string>)>((mapping, attributeColumns));
    }

    private static NotSupportedException NotSupported(string operation, int layerId)
        => new($"Redshift provider does not support '{operation}' for layer {layerId} in this slice.");
}
