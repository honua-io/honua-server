// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Npgsql;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureDataAccess
{
    public async Task<long> ExecuteCountQueryAsync(CoreParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = await CreateCommandAsync(
                connection,
                query.Sql,
                cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
                cancellationToken).ConfigureAwait(false);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            var count = Convert.ToInt64(result, CultureInfo.InvariantCulture);

            stopwatch.Stop();
            var recordCount = count > int.MaxValue ? int.MaxValue : (int)count;
            _cacheManager.RecordQueryMetrics("count", stopwatch.ElapsedMilliseconds, recordCount);
            RecordPerformanceQuery("count", layerId, stopwatch.ElapsedMilliseconds, recordCount);
            LogSlowQuery("count", stopwatch.ElapsedMilliseconds, layerId, count <= int.MaxValue ? (int?)count : null);

            return count;
        }
        catch (Exception)
        {
            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("count_error", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<ImmutableArray<Feature>> ExecuteSelectQueryAsync(CoreParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = await CreateCommandAsync(
                connection,
                query.Sql,
                cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
                cancellationToken).ConfigureAwait(false);

            var features = new List<Feature>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var feature = await ReadFeatureAsync(reader, cancellationToken);
                features.Add(feature);
            }

            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select", stopwatch.ElapsedMilliseconds, features.Count);
            RecordPerformanceQuery("select", layerId, stopwatch.ElapsedMilliseconds, features.Count);
            LogSlowQuery("select", stopwatch.ElapsedMilliseconds, layerId, features.Count);

            return features.ToImmutableArray();
        }
        catch (Exception)
        {
            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_error", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<ImmutableArray<ProjectedPoint>> ExecuteSelectProjectedPointsAsync(
        CoreParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = await CreateCommandAsync(
                connection,
                query.Sql,
                cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
                cancellationToken).ConfigureAwait(false);

            var capacity = featureQuery.Limit is > 0 and < int.MaxValue
                ? featureQuery.Limit.Value
                : 256;
            var points = new List<ProjectedPoint>(capacity);
            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                points.Add(new ProjectedPoint(reader.GetDouble(0), reader.GetDouble(1)));
            }

            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_points", stopwatch.ElapsedMilliseconds, points.Count);
            RecordPerformanceQuery("select_points", layerId, stopwatch.ElapsedMilliseconds, points.Count);
            LogSlowQuery("select_points", stopwatch.ElapsedMilliseconds, layerId, points.Count);

            return points.ToImmutableArray();
        }
        catch (Exception)
        {
            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_points_error", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<ImmutableArray<RawGeoServicesFeature>> ExecuteSelectRawGeoServicesPointQueryAsync(
        CoreParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = await CreateCommandAsync(
                connection,
                query.Sql,
                cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
                cancellationToken).ConfigureAwait(false);

            var capacity = featureQuery.Limit is > 0 and < int.MaxValue
                ? featureQuery.Limit.Value
                : 256;
            var features = new List<RawGeoServicesFeature>(capacity);
            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                features.Add(ReadRawGeoServicesFeature(reader));
            }

            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_raw_geoservices", stopwatch.ElapsedMilliseconds, features.Count);
            RecordPerformanceQuery("select_raw_geoservices", layerId, stopwatch.ElapsedMilliseconds, features.Count);
            LogSlowQuery("select_raw_geoservices", stopwatch.ElapsedMilliseconds, layerId, features.Count);

            return features.ToImmutableArray();
        }
        catch (Exception)
        {
            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_raw_geoservices_error", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<ImmutableArray<GmlFeature>> ExecuteSelectGmlQueryAsync(CoreParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = await CreateCommandAsync(
                connection,
                query.Sql,
                cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
                cancellationToken).ConfigureAwait(false);

            var features = new List<GmlFeature>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var feature = await ReadGmlFeatureAsync(reader, cancellationToken);
                features.Add(feature);
            }

            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_gml", stopwatch.ElapsedMilliseconds, features.Count);
            RecordPerformanceQuery("select_gml", layerId, stopwatch.ElapsedMilliseconds, features.Count);
            LogSlowQuery("select_gml", stopwatch.ElapsedMilliseconds, layerId, features.Count);

            return features.ToImmutableArray();
        }
        catch (Exception)
        {
            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_gml_error", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<ImmutableArray<EncodedGeoJsonFeature>> ExecuteSelectGeoJsonQueryAsync(
        CoreParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = await CreateCommandAsync(
                connection,
                query.Sql,
                cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
                cancellationToken).ConfigureAwait(false);

            var features = new List<EncodedGeoJsonFeature>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var feature = await ReadGeoJsonFeatureAsync(reader, cancellationToken);
                features.Add(feature);
            }

            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_geojson", stopwatch.ElapsedMilliseconds, features.Count);
            RecordPerformanceQuery("select_geojson", layerId, stopwatch.ElapsedMilliseconds, features.Count);
            LogSlowQuery("select_geojson", stopwatch.ElapsedMilliseconds, layerId, features.Count);

            return features.ToImmutableArray();
        }
        catch (Exception)
        {
            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_geojson_error", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<ImmutableArray<RawGeoJsonFeature>> ExecuteSelectRawGeoJsonQueryAsync(
        CoreParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = await CreateCommandAsync(
                connection,
                query.Sql,
                cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
                cancellationToken).ConfigureAwait(false);

            var features = new List<RawGeoJsonFeature>();
            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var feature = await ReadRawGeoJsonFeatureAsync(reader, cancellationToken).ConfigureAwait(false);
                features.Add(feature);
            }

            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_raw_geojson", stopwatch.ElapsedMilliseconds, features.Count);
            RecordPerformanceQuery("select_raw_geojson", layerId, stopwatch.ElapsedMilliseconds, features.Count);
            LogSlowQuery("select_raw_geojson", stopwatch.ElapsedMilliseconds, layerId, features.Count);

            return features.ToImmutableArray();
        }
        catch (Exception)
        {
            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_raw_geojson_error", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<ImmutableArray<KmlFeature>> ExecuteSelectKmlQueryAsync(
        CoreParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = await CreateCommandAsync(
                connection,
                query.Sql,
                cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
                cancellationToken).ConfigureAwait(false);

            var features = new List<KmlFeature>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var feature = await ReadKmlFeatureAsync(reader, cancellationToken);
                features.Add(feature);
            }

            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_kml", stopwatch.ElapsedMilliseconds, features.Count);
            RecordPerformanceQuery("select_kml", layerId, stopwatch.ElapsedMilliseconds, features.Count);
            LogSlowQuery("select_kml", stopwatch.ElapsedMilliseconds, layerId, features.Count);

            return features.ToImmutableArray();
        }
        catch (Exception)
        {
            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_kml_error", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<byte[]?> ExecuteSelectFlatGeobufQueryAsync(
        CoreParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = await CreateCommandAsync(
                connection,
                query.Sql,
                cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
                cancellationToken).ConfigureAwait(false);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
            {
                stopwatch.Stop();
                _cacheManager.RecordQueryMetrics("select_fgb", stopwatch.ElapsedMilliseconds, 0);
                RecordPerformanceQuery("select_fgb", layerId, stopwatch.ElapsedMilliseconds, 0);
                LogSlowQuery("select_fgb", stopwatch.ElapsedMilliseconds, layerId, 0);
                return null;
            }

            var payload = reader.GetFieldValue<byte[]>(0);

            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_fgb", stopwatch.ElapsedMilliseconds, payload.Length > 0 ? 1 : 0);
            RecordPerformanceQuery("select_fgb", layerId, stopwatch.ElapsedMilliseconds, payload.Length > 0 ? 1 : 0);
            LogSlowQuery("select_fgb", stopwatch.ElapsedMilliseconds, layerId, payload.Length > 0 ? 1 : 0);

            return payload;
        }
        catch (Exception)
        {
            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_fgb_error", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<byte[]?> ExecuteSelectGeobufQueryAsync(
        CoreParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = await CreateCommandAsync(
                connection,
                query.Sql,
                cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
                cancellationToken).ConfigureAwait(false);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
            {
                stopwatch.Stop();
                _cacheManager.RecordQueryMetrics("select_geobuf", stopwatch.ElapsedMilliseconds, 0);
                RecordPerformanceQuery("select_geobuf", layerId, stopwatch.ElapsedMilliseconds, 0);
                LogSlowQuery("select_geobuf", stopwatch.ElapsedMilliseconds, layerId, 0);
                return null;
            }

            var payload = reader.GetFieldValue<byte[]>(0);

            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_geobuf", stopwatch.ElapsedMilliseconds, payload.Length > 0 ? 1 : 0);
            RecordPerformanceQuery("select_geobuf", layerId, stopwatch.ElapsedMilliseconds, payload.Length > 0 ? 1 : 0);
            LogSlowQuery("select_geobuf", stopwatch.ElapsedMilliseconds, layerId, payload.Length > 0 ? 1 : 0);

            return payload;
        }
        catch (Exception)
        {
            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_geobuf_error", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<ImmutableArray<long>> ExecuteSelectObjectIdsQueryAsync(
        CoreParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = await CreateCommandAsync(
                connection,
                query.Sql,
                cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
                cancellationToken).ConfigureAwait(false);

            var objectIds = new List<long>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                objectIds.Add(reader.GetInt64(0));
            }

            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_objectids", stopwatch.ElapsedMilliseconds, objectIds.Count);
            RecordPerformanceQuery("select_objectids", layerId, stopwatch.ElapsedMilliseconds, objectIds.Count);
            LogSlowQuery("select_objectids", stopwatch.ElapsedMilliseconds, layerId, objectIds.Count);

            return objectIds.ToImmutableArray();
        }
        catch (Exception)
        {
            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("select_objectids_error", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async Task<Feature?> GetFeatureAsync(int layerId, long featureId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var geometryStorageType = await _cacheManager.GetGeometryStorageTypeAsync(cancellationToken).ConfigureAwait(false);
        var geometrySelect = _geometryProcessor.GetGeometrySelectExpression(geometryStorageType, new FeatureQuery());

        var sql = $@"
            SELECT objectid, {geometrySelect}, attributes
            FROM {_tableName}
            WHERE layer_id = $1 AND objectid = $2";

        await using var command = CreateSafeCommand(connection, sql);
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);
        ApplyCommandTimeout(command, _queryTimeoutSeconds);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return await ReadFeatureAsync(reader, cancellationToken);
    }

    public async Task<FeatureExtent?> GetExtentAsync(
        int layerId,
        CoreParameterizedQuery? query,
        FeatureQuery featureQuery,
        CancellationToken cancellationToken)
    {
        if (query == null)
        {
            return null;
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateSafeCommand(connection, query.Sql);

        command.Parameters.AddWithValue(layerId);
        foreach (var param in query.WhereParameters)
        {
            command.Parameters.AddWithValue(param);
        }
        ApplyCommandTimeout(command, _queryTimeoutSeconds);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            return null;
        }

        var minx = reader.GetDouble(0);
        var miny = reader.GetDouble(1);
        var maxx = reader.GetDouble(2);
        var maxy = reader.GetDouble(3);

        var spatialReference = featureQuery.OutputSrid
            ?? featureQuery.SpatialReferenceSrid
            ?? SpatialReference.WGS84.Wkid;

        return FeatureExtent.Create(minx, miny, maxx, maxy, spatialReference);
    }

    public async Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId,
        CoreParameterizedQuery? query,
        CancellationToken cancellationToken)
    {
        if (query == null)
        {
            return null;
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateSafeCommand(connection, query.Sql);

        command.Parameters.AddWithValue(layerId);
        foreach (var param in query.WhereParameters)
        {
            command.Parameters.AddWithValue(param);
        }
        ApplyCommandTimeout(command, _queryTimeoutSeconds);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var start = ReadTemporalValue(reader, 0);
        var end = ReadTemporalValue(reader, 1);

        if (start == null && end == null)
        {
            return null;
        }

        return TemporalExtentResult.Create(start, end);
    }

    public async Task<byte[]?> GetMvtTileAsync(int layerId, CoreParameterizedQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateSafeCommand(connection, query.Sql);

        foreach (var param in query.WhereParameters)
        {
            command.Parameters.AddWithValue(param);
        }
        ApplyCommandTimeout(command, _tileTimeoutSeconds);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            return null;
        }

        return reader.GetFieldValue<byte[]>(0);
    }

    private static DateTimeOffset? ReadTemporalValue(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset dto => dto,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(
                dateTime,
                dateTime.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : dateTime.Kind)),
            DateOnly dateOnly => new DateTimeOffset(dateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)),
            string text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) => parsed,
            _ => null
        };
    }

    public async IAsyncEnumerable<Feature> StreamFeaturesAsync(
        int layerId,
        CoreParameterizedQuery query,
        FeatureQuery featureQuery,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = await CreateCommandAsync(
            connection,
            query.Sql,
            cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
            cancellationToken).ConfigureAwait(false);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            yield return await ReadFeatureAsync(reader, cancellationToken);
        }
    }

    public async IAsyncEnumerable<GmlFeature> StreamGmlFeaturesAsync(
        int layerId,
        CoreParameterizedQuery query,
        FeatureQuery featureQuery,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = await CreateCommandAsync(
            connection,
            query.Sql,
            cmd => AddQueryParameters(cmd, featureQuery, layerId, query.WhereParameters),
            cancellationToken).ConfigureAwait(false);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            yield return await ReadGmlFeatureAsync(reader, cancellationToken);
        }
    }
}
