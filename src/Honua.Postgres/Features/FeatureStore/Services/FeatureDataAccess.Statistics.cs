// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Npgsql;
using CoreParameterizedQuery = Honua.Core.Features.FeatureStore.Domain.ParameterizedQuery;

namespace Honua.Postgres.Features.FeatureStore.Services;

internal sealed partial class FeatureDataAccess
{
    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> ExecuteStatisticsQueryAsync(
        CoreParameterizedQuery query,
        FeatureQuery featureQuery,
        int layerId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(query.Sql, connection);

            command.Parameters.AddWithValue(layerId);
            foreach (var param in query.WhereParameters)
            {
                command.Parameters.AddWithValue(param);
            }

            ApplyCommandTimeout(command, _queryTimeoutSeconds);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var rows = new List<IReadOnlyDictionary<string, object?>>();
            var fieldCount = 0;
            string[]? fieldNames = null;

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (fieldNames == null)
                {
                    fieldCount = reader.FieldCount;
                    fieldNames = new string[fieldCount];
                    for (var i = 0; i < fieldCount; i++)
                    {
                        fieldNames[i] = reader.GetName(i);
                    }
                }

                var row = new Dictionary<string, object?>(fieldCount, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < fieldCount; i++)
                {
                    row[fieldNames[i]] = reader.IsDBNull(i)
                        ? null
                        : ConvertStatisticsValue(reader.GetValue(i));
                }

                rows.Add(row);
            }

            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("statistics", stopwatch.ElapsedMilliseconds, rows.Count);
            RecordPerformanceQuery("statistics", layerId, stopwatch.ElapsedMilliseconds, rows.Count);
            LogSlowQuery("statistics", stopwatch.ElapsedMilliseconds, layerId, rows.Count);

            return rows.ToImmutableArray();
        }
        catch (Exception)
        {
            stopwatch.Stop();
            _cacheManager.RecordQueryMetrics("statistics_error", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private static object? ConvertStatisticsValue(object value)
    {
        return value switch
        {
            decimal d => Convert.ToDouble(d, CultureInfo.InvariantCulture),
            long l => l,
            int i => (long)i,
            double d => d,
            float f => (double)f,
            string s => s,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt,
                dt.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : dt.Kind)),
            DateTimeOffset dto => dto,
            // Pass arrays through untouched so spatial-join carry fields
            // (array_agg(text) → string[]) reach the JSON serializer as real
            // collections instead of getting stringified to "System.String[]".
            Array arr => arr,
            _ => value.ToString()
        };
    }
}
