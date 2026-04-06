// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.DuckDB.Features.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Honua.DuckDB.Features.FeatureStore.Services;

/// <summary>
/// Executes parameterized queries against DuckDB and materializes Feature objects.
/// DuckDB tables use direct columns (not JSONB), so attribute reading iterates column values.
/// </summary>
internal sealed class DuckDBFeatureDataAccess : IFeatureDataAccess
{
    private const string InternalTotalCountColumn = "__honua_total_count";

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly DuckDBLayerRegistry _layerRegistry;
    private readonly IPerformanceMonitor? _performanceMonitor;
    private readonly ILogger<DuckDBFeatureDataAccess> _logger;

    public DuckDBFeatureDataAccess(
        IDatabaseConnectionProvider connectionProvider,
        DuckDBLayerRegistry layerRegistry,
        IPerformanceMonitor? performanceMonitor,
        ILogger<DuckDBFeatureDataAccess> logger)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _layerRegistry = layerRegistry ?? throw new ArgumentNullException(nameof(layerRegistry));
        _performanceMonitor = performanceMonitor;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<long> ExecuteCountQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, query);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        sw.Stop();

        _performanceMonitor?.RecordDatabaseQuery("count", layerId.ToString(CultureInfo.InvariantCulture), sw.Elapsed, 1);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<Feature>> ExecuteSelectQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        var sw = Stopwatch.StartNew();
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, query);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var features = ImmutableArray.CreateBuilder<Feature>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            features.Add(ReadFeature(reader, mapping));
        }

        sw.Stop();
        _performanceMonitor?.RecordDatabaseQuery("select", layerId.ToString(CultureInfo.InvariantCulture), sw.Elapsed, features.Count);
        return features.ToImmutable();
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<ProjectedPoint>> ExecuteSelectProjectedPointsAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, query);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var points = ImmutableArray.CreateBuilder<ProjectedPoint>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var x = reader.GetDouble(0);
            var y = reader.GetDouble(1);
            points.Add(new ProjectedPoint { X = x, Y = y });
        }

        return points.ToImmutable();
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<GmlFeature>> ExecuteSelectGmlQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        return await ExecuteEncodedGeometryQueryAsync<GmlFeature>(
            query, layerId, cancellationToken,
            (reader, mapping) =>
            {
                var id = reader.GetInt64(0);
                var geometryText = reader.IsDBNull(1) ? null : reader.GetString(1);
                var attributes = ReadAttributesFromColumns(reader, mapping, startIndex: 2);
                return GmlFeature.Create(id, geometryText, attributes);
            });
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<EncodedGeoJsonFeature>> ExecuteSelectGeoJsonQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        return await ExecuteEncodedGeometryQueryAsync<EncodedGeoJsonFeature>(
            query, layerId, cancellationToken,
            (reader, mapping) =>
            {
                var id = reader.GetInt64(0);
                var geometryGeoJson = reader.IsDBNull(1) ? null : reader.GetString(1);
                var attributes = ReadAttributesFromColumns(reader, mapping, startIndex: 2);
                return EncodedGeoJsonFeature.Create(id, geometryGeoJson, attributes);
            });
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<KmlFeature>> ExecuteSelectKmlQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        return await ExecuteEncodedGeometryQueryAsync<KmlFeature>(
            query, layerId, cancellationToken,
            (reader, mapping) =>
            {
                var id = reader.GetInt64(0);
                var geometryText = reader.IsDBNull(1) ? null : reader.GetString(1);
                var attributes = ReadAttributesFromColumns(reader, mapping, startIndex: 2);
                return KmlFeature.Create(id, geometryText, attributes);
            });
    }

    /// <inheritdoc />
    public Task<byte[]?> ExecuteSelectFlatGeobufQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        // DuckDB does not support native FlatGeobuf encoding
        return Task.FromResult<byte[]?>(null);
    }

    /// <inheritdoc />
    public Task<byte[]?> ExecuteSelectGeobufQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        // DuckDB does not support native Geobuf encoding
        return Task.FromResult<byte[]?>(null);
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<long>> ExecuteSelectObjectIdsQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, query);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var ids = ImmutableArray.CreateBuilder<long>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids.ToImmutable();
    }

    /// <inheritdoc />
    public Task<QueryResult<Feature>> QueryRelatedAsync(
        int layerId, RelatedQuery query, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("DuckDB provider does not support relationship queries.");
    }

    /// <inheritdoc />
    public Task<Feature> CreateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("DuckDB provider is read-only.");
    }

    /// <inheritdoc />
    public Task<Feature> UpdateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("DuckDB provider is read-only.");
    }

    /// <inheritdoc />
    public Task<bool> DeleteFeatureAsync(int layerId, long featureId, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("DuckDB provider is read-only.");
    }

    /// <inheritdoc />
    public async Task<Feature?> GetFeatureAsync(int layerId, long featureId, CancellationToken cancellationToken)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        var columnsExpr = string.Join(", ", mapping.AttributeColumns.Select(c => $"\"{c}\""));
        var selectCols = $"{mapping.QuotedObjectIdColumn}, ST_AsWKB({mapping.QuotedGeometryColumn})";
        if (!string.IsNullOrEmpty(columnsExpr))
        {
            selectCols += $", {columnsExpr}";
        }

        var sql = $"SELECT {selectCols} FROM {mapping.QuotedTableName} WHERE {mapping.QuotedObjectIdColumn} = $1";
        var pq = new ParameterizedQuery(sql, [featureId]);

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, pq);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReadFeature(reader, mapping);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<FeatureExtent?> GetExtentAsync(
        int layerId, ParameterizedQuery? query, FeatureQuery featureQuery, CancellationToken cancellationToken)
    {
        if (query == null)
        {
            return null;
        }

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, query);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
            {
                return null;
            }

            return FeatureExtent.Create(
                Convert.ToDouble(reader.GetValue(0), CultureInfo.InvariantCulture), // xmin
                Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture), // ymin
                Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture), // xmax
                Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture), // ymax
                featureQuery.OutputSrid ?? featureQuery.SpatialReferenceSrid ?? 4326);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId, ParameterizedQuery? query, CancellationToken cancellationToken)
    {
        if (query == null)
        {
            return null;
        }

        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, query);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var minValue = reader.IsDBNull(0) ? (DateTimeOffset?)null : new DateTimeOffset(reader.GetDateTime(0), TimeSpan.Zero);
            var maxValue = reader.IsDBNull(1) ? (DateTimeOffset?)null : new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero);
            return TemporalExtentResult.Create(minValue, maxValue);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> ExecuteStatisticsQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, query);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = ImmutableArray.CreateBuilder<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            results.Add(row);
        }

        return results.ToImmutable();
    }

    /// <inheritdoc />
    public Task<byte[]?> GetMvtTileAsync(int layerId, ParameterizedQuery query, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("DuckDB provider does not support native MVT tile generation.");
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Feature> StreamFeaturesAsync(
        int layerId, ParameterizedQuery query, FeatureQuery featureQuery,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, query);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return ReadFeature(reader, mapping);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<GmlFeature> StreamGmlFeaturesAsync(
        int layerId, ParameterizedQuery query, FeatureQuery featureQuery,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, query);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var gml = reader.IsDBNull(1) ? null : reader.GetString(1);
            var attrs = ReadAttributesFromColumns(reader, mapping, startIndex: 2);
            yield return GmlFeature.Create(id, gml, attrs);
        }
    }

    /// <inheritdoc />
    public Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("DuckDB provider is read-only.");
    }

    #region Private helpers

    private static DbCommand CreateCommand(DbConnection connection, ParameterizedQuery query)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = query.Sql;

        for (var i = 0; i < query.WhereParameters.Count; i++)
        {
            var param = cmd.CreateParameter();
            param.Value = query.WhereParameters[i];
            cmd.Parameters.Add(param);
        }

        return cmd;
    }

    private static Feature ReadFeature(DbDataReader reader, DuckDBLayerMapping mapping)
    {
        var id = reader.GetInt64(0);
        var geometry = reader.IsDBNull(1) ? null : ReadBlobValue(reader, 1);
        var attributes = ReadAttributesFromColumns(reader, mapping, startIndex: 2);
        return Feature.Create(id, geometry, attributes);
    }

    /// <summary>
    /// Reads a BLOB column value as byte[]. DuckDB.NET may return either byte[] or
    /// <see cref="Stream"/> (UnmanagedMemoryStream) for binary columns like ST_AsWKB.
    /// </summary>
    private static byte[]? ReadBlobValue(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        if (value is byte[] bytes)
        {
            return bytes;
        }

        if (value is Stream stream)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        return (byte[])value;
    }

    private static ImmutableDictionary<string, object?> ReadAttributesFromColumns(
        DbDataReader reader, DuckDBLayerMapping mapping, int startIndex)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        builder["objectid"] = reader.GetInt64(0);

        for (var i = startIndex; i < reader.FieldCount; i++)
        {
            var fieldName = reader.GetName(i);

            if (fieldName.Equals(InternalTotalCountColumn, StringComparison.OrdinalIgnoreCase))
            {
                builder[InternalTotalCountColumn] = reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);
                continue;
            }

            builder[fieldName] = reader.IsDBNull(i) ? null : ConvertValue(reader.GetValue(i));
        }

        return builder.ToImmutable();
    }

    private static object? ConvertValue(object value)
    {
        return value switch
        {
            // DuckDB may return various numeric types; normalize for consistent serialization
            int i => (long)i,
            short s => (long)s,
            float f => (double)f,
            _ => value
        };
    }

    private async Task<ImmutableArray<T>> ExecuteEncodedGeometryQueryAsync<T>(
        ParameterizedQuery query,
        int layerId,
        CancellationToken cancellationToken,
        Func<DbDataReader, DuckDBLayerMapping, T> readRow)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = CreateCommand(connection, query);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = ImmutableArray.CreateBuilder<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(readRow(reader, mapping));
        }

        return results.ToImmutable();
    }

    #endregion
}
