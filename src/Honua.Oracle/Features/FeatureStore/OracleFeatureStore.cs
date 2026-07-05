// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Oracle.Features.FeatureStore.Services;

namespace Honua.Oracle.Features.FeatureStore;

/// <summary>
/// Read-only Oracle feature provider for the connect-in-place enterprise-geodatabase
/// slice (#1252).
/// </summary>
/// <remarks>
/// <para>Implements the canonical <see cref="IFeatureDataProvider"/> + <see cref="IFeatureReader"/>
/// surface for layers backed by Oracle Spatial <c>SDO_GEOMETRY</c> columns. Edits, native MVT,
/// native FlatGeobuf/Geobuf/GML, statistics aggregates, top-features, bins, and H3 are
/// deliberately disabled on this slice and are advertised as such via
/// <see cref="FeatureProviderCapabilities"/>.</para>
/// <para>ArcSDE <c>ST_Geometry</c>, binary-format, and versioned enterprise-geodatabase tables
/// are detected by <see cref="OracleSpatialGuard"/> at first execution and rejected with
/// <see cref="NotSupportedException"/> before any geometry bytes are read. This is the IP /
/// clean-room enforcement point and must not be bypassed.</para>
/// <para>Layer/storage configuration flows through the Metadata v2 provider binding passed by
/// <see cref="FeatureProviderQueryRouter"/>.</para>
/// </remarks>
internal sealed class OracleFeatureStore : IFeatureDataProvider, IFeatureReader, IBindableFeatureDataProvider
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

    private readonly OracleFeatureDataAccess _dataAccess;
    private readonly OracleSpatialGuard _spatialGuard;
    private readonly FeatureProviderBinding? _binding;
    private readonly DataConnection? _boundConnection;
    private readonly IMetadataV2GraphProvider? _v2Provider;

    public OracleFeatureStore(OracleFeatureDataAccess dataAccess, OracleSpatialGuard spatialGuard)
        : this(dataAccess, spatialGuard, v2Provider: null, binding: null)
    {
    }

    public OracleFeatureStore(OracleFeatureDataAccess dataAccess, OracleSpatialGuard spatialGuard, IMetadataV2GraphProvider? v2Provider)
        : this(dataAccess, spatialGuard, v2Provider, binding: null)
    {
    }

    private OracleFeatureStore(
        OracleFeatureDataAccess dataAccess,
        OracleSpatialGuard spatialGuard,
        IMetadataV2GraphProvider? v2Provider,
        FeatureProviderBinding? binding)
    {
        _dataAccess = dataAccess ?? throw new ArgumentNullException(nameof(dataAccess));
        _spatialGuard = spatialGuard ?? throw new ArgumentNullException(nameof(spatialGuard));
        _v2Provider = v2Provider;
        _binding = binding;
        _boundConnection = binding?.Connection;
    }

    /// <inheritdoc />
    public string ProviderName => DataProviderNames.Oracle;

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

        return new OracleFeatureStore(_dataAccess, _spatialGuard, _v2Provider, binding);
    }

    /// <inheritdoc />
    public async Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        await EnsureNoPermanentFilterAsync(layerId, cancellationToken).ConfigureAwait(false);
        var (mapping, attributeColumns) = await ResolveLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var query = new FeatureQuery
        {
            ObjectIds = [featureId],
            Limit = 1
        };

        var sql = OracleFeatureQueryBuilder.BuildSelectQuery(mapping, query, attributeColumns);
        var features = await _dataAccess.ExecuteSelectAsync(mapping, sql, attributeColumns, _boundConnection, cancellationToken).ConfigureAwait(false);
        return features.Length == 0 ? null : features[0];
    }

    /// <inheritdoc />
    public async Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        await EnsureNoPermanentFilterAsync(layerId, cancellationToken).ConfigureAwait(false);
        var (mapping, attributeColumns) = await ResolveLayerAsync(layerId, cancellationToken).ConfigureAwait(false);

        // Probe one extra row when a Limit is requested so HasMoreResults is reported correctly
        // without paying for a separate COUNT query. The probe row is trimmed before returning.
        var requestedLimit = query.Limit;

        // Probe with Limit + 1 to detect more rows, but only when below int.MaxValue:
        // adding to int.MaxValue overflows to a negative value, which Oracle rejects as a
        // bad FETCH NEXT count (ORA error / 500). At int.MaxValue there is effectively no
        // further page to probe, so issue the query unchanged.
        var canProbe = requestedLimit.HasValue && requestedLimit.Value < int.MaxValue;
        var probeQuery = canProbe
            ? query with { Limit = requestedLimit!.Value + 1 }
            : query;

        var sql = OracleFeatureQueryBuilder.BuildSelectQuery(mapping, probeQuery, attributeColumns);
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
        await EnsureNoPermanentFilterAsync(layerId, cancellationToken).ConfigureAwait(false);
        var (mapping, _) = await ResolveLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var sql = OracleFeatureQueryBuilder.BuildObjectIdsQuery(mapping, query);
        return await _dataAccess.ExecuteObjectIdsAsync(mapping, sql, _boundConnection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        await EnsureNoPermanentFilterAsync(layerId, cancellationToken).ConfigureAwait(false);
        var (mapping, _) = await ResolveLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var sql = OracleFeatureQueryBuilder.BuildCountQuery(mapping, query);
        return await _dataAccess.ExecuteCountAsync(mapping, sql, _boundConnection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
    {
        await EnsureNoPermanentFilterAsync(layerId, cancellationToken).ConfigureAwait(false);
        var (mapping, _) = await ResolveLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        var sql = OracleFeatureQueryBuilder.BuildExtentQuery(mapping, query);
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
        var countTask = _dataAccess.ExecuteCountAsync(mapping, OracleFeatureQueryBuilder.BuildCountQuery(mapping, emptyQuery), _boundConnection, cancellationToken);
        var extentTask = _dataAccess.ExecuteExtentAsync(mapping, OracleFeatureQueryBuilder.BuildExtentQuery(mapping, emptyQuery), _boundConnection, cancellationToken);
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

    /// <summary>
    /// Throws <see cref="NotSupportedException"/> when the layer has a permanent filter configured,
    /// because the shared filter translator emits Postgres-flavored SQL that is not valid Oracle SQL.
    /// Returns immediately when <c>_v2Provider</c> is not registered (no metadata available to check).
    /// </summary>
    private async Task EnsureNoPermanentFilterAsync(int layerId, CancellationToken cancellationToken)
    {
        if (_v2Provider == null)
        {
            return;
        }
        var snapshot = await _v2Provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var resource))
        {
            return;
        }
        var v2Filter = resource.PermanentFilter;
        if (v2Filter != null && !string.IsNullOrWhiteSpace(v2Filter.Expression))
        {
            throw new NotSupportedException(
                $"Layer {layerId} has a permanent (row-visibility) filter configured, but the Oracle provider cannot enforce it: " +
                "the shared filter translator emits Postgres-flavored SQL that is not valid Oracle SQL. " +
                "Configure permanent filters only on Postgres layers, or remove the permanent filter from this layer.");
        }
    }

    private async Task<(OracleLayerMapping Mapping, IReadOnlyList<string> AttributeColumns)> ResolveLayerAsync(
        int layerId,
        CancellationToken cancellationToken)
    {
        var binding = _binding
            ?? throw new InvalidOperationException(
                "Oracle provider reads require a Metadata v2 provider binding; route requests through FeatureProviderQueryRouter.");
        if (binding.StorageLayerId != layerId)
        {
            throw new InvalidOperationException(
                $"Oracle provider binding targets storage layer {binding.StorageLayerId}, not requested layer {layerId}.");
        }

        var mapping = OracleLayerMapping.FromStorage(layerId, binding.StorageMapping);

        // IP / clean-room enforcement: refuse non-SDO_GEOMETRY columns and ArcSDE-versioned
        // tables before any read SQL runs. Result is cached per storage layer.
        await _spatialGuard.EnsureStandardSpatialAsync(mapping, _boundConnection, cancellationToken).ConfigureAwait(false);

        var attributeColumns = binding.Resource.SchemaFields
            .Where(f => f.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography)
                && !f.Name.Equals(mapping.PrimaryKeyColumn, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Name)
            .ToArray();

        return (mapping, attributeColumns);
    }

    private static NotSupportedException NotSupported(string operation, int layerId)
        => new($"Oracle provider does not support '{operation}' for layer {layerId} in this slice.");
}
