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
using Honua.Core.Features.Shared.Models;
using Honua.MySql.Features.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Honua.MySql.Features.FeatureStore.Services;

/// <summary>
/// Executes MySQL/MariaDB read queries through <see cref="IDatabaseConnectionProvider"/>
/// and materialises feature, count, and extent results. Edits, statistics, MVT, and
/// other unsupported paths surface <see cref="NotSupportedException"/> consistent with
/// the provider capability declaration.
/// </summary>
internal sealed class MySqlFeatureDataAccess : IFeatureDataAccess
{
    /// <summary>
    /// Activity source for MySQL/MariaDB feature data access spans.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("Honua.MySql.FeatureDataAccess");

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly MySqlLayerMappingRegistry _layerRegistry;
    private readonly MySqlEngineFlavor _engineFlavor;
    private readonly IPerformanceMonitor? _performanceMonitor;
    private readonly ILogger<MySqlFeatureDataAccess> _logger;

    public MySqlFeatureDataAccess(
        IDatabaseConnectionProvider connectionProvider,
        MySqlLayerMappingRegistry layerRegistry,
        IPerformanceMonitor? performanceMonitor,
        ILogger<MySqlFeatureDataAccess> logger,
        MySqlEngineFlavor engineFlavor = MySqlEngineFlavor.Mysql)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _layerRegistry = layerRegistry ?? throw new ArgumentNullException(nameof(layerRegistry));
        _engineFlavor = engineFlavor;
        _performanceMonitor = performanceMonitor;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<long> ExecuteCountQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        return await ExecuteAsync(layerId, "count", async ct =>
        {
            var sw = Stopwatch.StartNew();
            await using var connection = await _connectionProvider.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = CreateCommand(connection, query);
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            sw.Stop();

            RecordQueryMetrics("count", layerId, sw.Elapsed, 1);
            return Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<Feature>> ExecuteSelectQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        return await ExecuteAsync(layerId, "select", async ct =>
        {
            var sw = Stopwatch.StartNew();
            await using var connection = await _connectionProvider.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = CreateCommand(connection, query);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            var features = ImmutableArray.CreateBuilder<Feature>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                features.Add(ReadFeature(reader, mapping));
            }

            sw.Stop();
            RecordQueryMetrics("select", layerId, sw.Elapsed, features.Count);
            return features.ToImmutable();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FeatureExtent?> GetExtentAsync(
        int layerId, ParameterizedQuery? query, FeatureQuery featureQuery, CancellationToken cancellationToken)
    {
        if (query is null)
        {
            return null;
        }

        var mapping = _layerRegistry.GetRequiredMapping(layerId);

        return await ExecuteAsync<FeatureExtent?>(layerId, "extent", async ct =>
        {
            await using var connection = await _connectionProvider.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = CreateCommand(connection, query);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0))
            {
                return null;
            }

            var minX = Convert.ToDouble(reader.GetValue(0), CultureInfo.InvariantCulture);
            var minY = Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture);
            var maxX = Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture);
            var maxY = Convert.ToDouble(reader.GetValue(3), CultureInfo.InvariantCulture);

            return FeatureExtent.Create(minX, minY, maxX, maxY, mapping.Srid);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<long>> ExecuteSelectObjectIdsQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        return await ExecuteAsync(layerId, "select_objectids", async ct =>
        {
            var sw = Stopwatch.StartNew();
            await using var connection = await _connectionProvider.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = CreateCommand(connection, query);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            var ids = ImmutableArray.CreateBuilder<long>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(reader.GetInt64(0));
            }

            sw.Stop();
            RecordQueryMetrics("select_objectids", layerId, sw.Elapsed, ids.Count);
            return ids.ToImmutable();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Feature?> GetFeatureAsync(int layerId, long featureId, CancellationToken cancellationToken)
    {
        var mapping = _layerRegistry.GetRequiredMapping(layerId);
        var attributesSql = mapping.AttributeColumns.Count == 0
            ? string.Empty
            : ", " + string.Join(", ", mapping.AttributeColumns.Select(MySqlIdentifier.Quote));

        var sql =
            $"SELECT {mapping.QuotedPrimaryKeyColumn}, {MySqlSpatialSql.AsWkb(mapping.QuotedGeometryColumn, _engineFlavor)} AS geometry{attributesSql} " +
            $"FROM {mapping.QualifiedTableSql} WHERE {mapping.QuotedPrimaryKeyColumn} = @p0";
        var pq = new ParameterizedQuery(sql, [featureId]);

        return await ExecuteAsync<Feature?>(layerId, "get_feature", async ct =>
        {
            await using var connection = await _connectionProvider.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = CreateCommand(connection, pq);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return ReadFeature(reader, mapping);
            }

            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    #region Unsupported paths

    /// <inheritdoc />
    public Task<ImmutableArray<ProjectedPoint>> ExecuteSelectProjectedPointsAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
        => throw NotSupported(nameof(ExecuteSelectProjectedPointsAsync));

    /// <inheritdoc />
    public Task<ImmutableArray<GmlFeature>> ExecuteSelectGmlQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
        => throw NotSupported(nameof(ExecuteSelectGmlQueryAsync));

    /// <inheritdoc />
    public Task<ImmutableArray<EncodedGeoJsonFeature>> ExecuteSelectGeoJsonQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
        => throw NotSupported(nameof(ExecuteSelectGeoJsonQueryAsync));

    /// <inheritdoc />
    public Task<ImmutableArray<RawGeoJsonFeature>> ExecuteSelectRawGeoJsonQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
        => throw NotSupported(nameof(ExecuteSelectRawGeoJsonQueryAsync));

    /// <inheritdoc />
    public Task<ImmutableArray<KmlFeature>> ExecuteSelectKmlQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
        => throw NotSupported(nameof(ExecuteSelectKmlQueryAsync));

    /// <inheritdoc />
    public Task<byte[]?> ExecuteSelectFlatGeobufQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
        => Task.FromResult<byte[]?>(null);

    /// <inheritdoc />
    public Task<byte[]?> ExecuteSelectGeobufQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
        => Task.FromResult<byte[]?>(null);

    /// <inheritdoc />
    public Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken)
        => throw NotSupported(nameof(QueryRelatedAsync));

    /// <inheritdoc />
    public Task<Feature> CreateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken)
        => throw NotSupported(nameof(CreateFeatureAsync));

    /// <inheritdoc />
    public Task<Feature> UpdateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken)
        => throw NotSupported(nameof(UpdateFeatureAsync));

    /// <inheritdoc />
    public Task<bool> DeleteFeatureAsync(int layerId, long featureId, CancellationToken cancellationToken)
        => throw NotSupported(nameof(DeleteFeatureAsync));

    /// <inheritdoc />
    public Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId, ParameterizedQuery? query, CancellationToken cancellationToken)
        => throw NotSupported(nameof(GetTemporalExtentAsync));

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> ExecuteStatisticsQueryAsync(
        ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
        => throw NotSupported(nameof(ExecuteStatisticsQueryAsync));

    /// <inheritdoc />
    public Task<byte[]?> GetMvtTileAsync(int layerId, ParameterizedQuery query, CancellationToken cancellationToken)
        => throw NotSupported(nameof(GetMvtTileAsync));

#pragma warning disable CS1998
    /// <inheritdoc />
    public async IAsyncEnumerable<Feature> StreamFeaturesAsync(
        int layerId, ParameterizedQuery query, FeatureQuery featureQuery,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        throw NotSupported(nameof(StreamFeaturesAsync));
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<GmlFeature> StreamGmlFeaturesAsync(
        int layerId, ParameterizedQuery query, FeatureQuery featureQuery,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        throw NotSupported(nameof(StreamGmlFeaturesAsync));
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
#pragma warning restore CS1998

    /// <inheritdoc />
    public Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken)
        => throw NotSupported(nameof(ApplyEditsAsync));

    #endregion

    private static NotSupportedException NotSupported(string operation)
        => new($"Operation '{operation}' is not supported by the MySQL/MariaDB provider.");

    private static DbCommand CreateCommand(DbConnection connection, ParameterizedQuery query)
    {
        var cmd = connection.CreateCommand();
        // codeql[cs/sql-injection]: query.Sql is built from internal templates with backtick-quoted
        // identifiers and named placeholders (@pN). Caller-supplied values flow through
        // WhereParameters and are bound below.
        cmd.CommandText = query.Sql;

        for (var i = 0; i < query.WhereParameters.Count; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = $"@p{i}";
            p.Value = query.WhereParameters[i] ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        return cmd;
    }

    private static Feature ReadFeature(DbDataReader reader, MySqlLayerMapping mapping)
    {
        var id = reader.GetInt64(0);
        var geometry = reader.IsDBNull(1) ? null : ReadBinaryValue(reader, 1);
        var attributes = ReadAttributes(reader, mapping, startIndex: 2);
        return Feature.Create(id, geometry, attributes);
    }

    private static byte[]? ReadBinaryValue(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            byte[] bytes => bytes,
            Stream stream => CopyToArray(stream),
            _ => (byte[])value
        };

        static byte[] CopyToArray(Stream stream)
        {
            using (stream)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }

    private static ImmutableDictionary<string, object?> ReadAttributes(
        DbDataReader reader, MySqlLayerMapping mapping, int startIndex)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        builder[FieldNames.ObjectId] = reader.GetInt64(0);

        for (var i = startIndex; i < reader.FieldCount; i++)
        {
            var fieldName = reader.GetName(i);
            builder[fieldName] = reader.IsDBNull(i) ? null : ConvertValue(reader.GetValue(i));
        }

        return builder.ToImmutable();
    }

    private static object? ConvertValue(object value) => value switch
    {
        // Normalize numeric types for consistent serialization
        int i => (long)i,
        short s => (long)s,
        sbyte sb => (long)sb,
        uint ui => (long)ui,
        ushort us => (long)us,
        byte b => (long)b,
        float f => (double)f,
        _ => value
    };

    private async Task<T> ExecuteAsync<T>(
        int layerId,
        string operationType,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity($"mysql.{operationType}", ActivityKind.Client);
        activity?.SetTag("db.system", "mysql");
        activity?.SetTag("db.operation", operationType);
        activity?.SetTag("layer.id", layerId);

        try
        {
            var result = await operation(cancellationToken).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ShouldWrapException(ex))
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            MySqlLog.QueryFailed(_logger, operationType, layerId, ex);
            throw CreateSafeException(layerId, operationType, ex);
        }
    }

    internal static bool ShouldWrapException(Exception ex)
        => ex is DbException or IOException or TimeoutException or InvalidOperationException;

    internal static Exception CreateSafeException(int layerId, string operationType, Exception ex)
    {
        return ex switch
        {
            TimeoutException => new TimeoutException(
                $"MySQL/MariaDB {operationType} query timed out for layer {layerId}.", ex),
            _ => new InvalidOperationException(
                $"MySQL/MariaDB {operationType} query failed for layer {layerId}.", ex)
        };
    }

    private void RecordQueryMetrics(string operationType, int layerId, TimeSpan elapsed, int recordCount)
    {
        _performanceMonitor?.RecordDatabaseQuery(
            operationType,
            layerId.ToString(CultureInfo.InvariantCulture),
            elapsed,
            recordCount);
    }
}
