// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Honua.Core.Features.FeatureStore.Domain;
using Npgsql;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed class SelectQueryRequest
{
    public required CoreParameterizedQuery Query { get; init; }
    public required FeatureQuery FeatureQuery { get; init; }
    public required int LayerId { get; init; }
}

internal sealed partial class FeatureDataAccess
{
    private sealed class SelectExecutionPlan<T>
    {
        public required SelectQueryRequest Request { get; init; }
        public required string MetricName { get; init; }
        public required TotalCountProjectionMode TotalCountMode { get; init; }
        public required Func<NpgsqlDataReader, FeatureRowReadContext, T> ReadRow { get; init; }
    }

    internal Task<(ImmutableArray<Feature> Items, long? TotalCount)> ExecuteOptimizedSelectQueryAsync(
        SelectQueryRequest request,
        CancellationToken cancellationToken)
        => ExecuteSelectWithTotalCountAsync(
            new SelectExecutionPlan<Feature>
            {
                Request = request,
                MetricName = "select",
                TotalCountMode = TotalCountProjectionMode.ExcludeAttribute,
                ReadRow = ReadFeature
            },
            cancellationToken);

    internal Task<(ImmutableArray<GmlFeature> Items, long? TotalCount)> ExecuteOptimizedSelectGmlQueryAsync(
        SelectQueryRequest request,
        CancellationToken cancellationToken)
        => ExecuteSelectWithTotalCountAsync(
            new SelectExecutionPlan<GmlFeature>
            {
                Request = request,
                MetricName = "select_gml",
                TotalCountMode = TotalCountProjectionMode.ExcludeAttribute,
                ReadRow = ReadGmlFeature
            },
            cancellationToken);

    internal Task<(ImmutableArray<EncodedGeoJsonFeature> Items, long? TotalCount)> ExecuteOptimizedSelectGeoJsonQueryAsync(
        SelectQueryRequest request,
        CancellationToken cancellationToken)
        => ExecuteSelectWithTotalCountAsync(
            new SelectExecutionPlan<EncodedGeoJsonFeature>
            {
                Request = request,
                MetricName = "select_geojson",
                TotalCountMode = TotalCountProjectionMode.ExcludeAttribute,
                ReadRow = ReadGeoJsonFeature
            },
            cancellationToken);

    internal Task<(ImmutableArray<KmlFeature> Items, long? TotalCount)> ExecuteOptimizedSelectKmlQueryAsync(
        SelectQueryRequest request,
        CancellationToken cancellationToken)
        => ExecuteSelectWithTotalCountAsync(
            new SelectExecutionPlan<KmlFeature>
            {
                Request = request,
                MetricName = "select_kml",
                TotalCountMode = TotalCountProjectionMode.ExcludeAttribute,
                ReadRow = ReadKmlFeature
            },
            cancellationToken);

    private async Task<(ImmutableArray<T> Items, long? TotalCount)> ExecuteSelectWithTotalCountAsync<T>(
        SelectExecutionPlan<T> plan,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var request = plan.Request;
        try
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = await CreateCommandAsync(
                connection,
                request.Query.Sql,
                cmd => AddQueryParameters(cmd, request.FeatureQuery, request.LayerId, request.Query.WhereParameters),
                cancellationToken).ConfigureAwait(false);

            var (items, totalCount) = await ReadSelectRowsAsync(command, plan, cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics(plan.MetricName, stopwatch.ElapsedMilliseconds, items.Length);
            RecordPerformanceQuery(plan.MetricName, request.LayerId, stopwatch.ElapsedMilliseconds, items.Length);
            LogSlowQuery(plan.MetricName, stopwatch.ElapsedMilliseconds, request.LayerId, items.Length);

            return (items, totalCount);
        }
        catch (Exception)
        {
            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics(plan.MetricName + "_error", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private static async Task<(ImmutableArray<T> Items, long? TotalCount)> ReadSelectRowsAsync<T>(
        NpgsqlCommand command,
        SelectExecutionPlan<T> plan,
        CancellationToken cancellationToken)
    {
        var features = ImmutableArray.CreateBuilder<T>(ResolveBuilderCapacity(plan.Request.FeatureQuery));
        long? totalCount = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var ordinals = FeatureRowOrdinals.Resolve(reader);
        var rowContext = new FeatureRowReadContext
        {
            AttributesReader = new FeatureAttributeReadContext(),
            Ordinals = ordinals,
            TotalCountMode = plan.TotalCountMode
        };
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!totalCount.HasValue &&
                ordinals.TotalCountOrdinal >= 0 &&
                !reader.IsDBNull(ordinals.TotalCountOrdinal))
            {
                totalCount = reader.GetFieldValue<long>(ordinals.TotalCountOrdinal);
            }

            features.Add(plan.ReadRow(reader, rowContext));
        }

        return (features.DrainToImmutable(), totalCount);
    }

    private async IAsyncEnumerable<T> StreamRowsAsync<T>(
        SelectQueryRequest request,
        Func<NpgsqlDataReader, FeatureRowReadContext, T> readRow,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = await CreateCommandAsync(
            connection,
            request.Query.Sql,
            cmd => AddQueryParameters(cmd, request.FeatureQuery, request.LayerId, request.Query.WhereParameters),
            cancellationToken).ConfigureAwait(false);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rowContext = new FeatureRowReadContext
        {
            AttributesReader = new FeatureAttributeReadContext(),
            Ordinals = FeatureRowOrdinals.Resolve(reader)
        };
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return readRow(reader, rowContext);
        }
    }
}
