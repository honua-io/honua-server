// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Postgres.Features.FeatureStore.Services;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Styling;

/// <summary>
/// PostgreSQL implementation of field profiling for style suggestions.
/// </summary>
internal sealed partial class PostgresFieldProfilingService : IFieldProfilingService
{
    private const int MaxSampleValues = 20;

    /// <summary>
    /// PostgreSQL JSONB type name for native numeric values.  Used with
    /// <c>jsonb_typeof()</c> to restrict numeric aggregates and value queries
    /// to values whose JSONB type matches what <c>ST_AsMVT</c> will expose as
    /// a native number on the tile — keeping profiling and the MapLibre
    /// <c>typeof == "number"</c> guard in agreement.
    /// </summary>
    private const string JsonbNumberType = "'number'";

    private readonly IDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresFieldProfilingService> _logger;
    private readonly string _featuresTable;

    public PostgresFieldProfilingService(
        IDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresFieldProfilingService> logger,
        string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger;
        _featuresTable = DatabaseSchema.GetFeaturesTableName(schemaName);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FieldProfile>> ProfileFieldsAsync(
        int layerId,
        IReadOnlyList<FieldDefinition> fields,
        int sampleLimit,
        CancellationToken cancellationToken = default)
    {
        FieldProfilingStarted(_logger, layerId, fields.Count);
        var sw = Stopwatch.StartNew();

        var profiles = new List<FieldProfile>(fields.Count);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var field in fields)
        {
            var profile = await ProfileSingleFieldAsync(
                connection, layerId, field, sampleLimit, cancellationToken).ConfigureAwait(false);
            if (profile != null)
                profiles.Add(profile);
        }

        FieldProfilingCompleted(_logger, layerId, sw.Elapsed.TotalMilliseconds);
        return profiles;
    }

    /// <inheritdoc />
    public async Task<double[]> GetNumericValuesAsync(
        int layerId,
        string fieldName,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (!FeatureQueryBuilder.IsValidFieldName(fieldName))
            throw new ArgumentException($"Invalid field name: {fieldName}", nameof(fieldName));

        var textPath = DatabaseSchema.BuildJsonPath(fieldName);
        var jsonbPath = DatabaseSchema.BuildJsonbAccessor(fieldName);

        // Sample without ORDER BY to get a representative cross-section of the
        // distribution (storage order is a reasonable proxy for random sampling).
        // Sorting happens in-process after fetching so classification algorithms
        // see the full value range, not just the lowest N values.
        // Filter on jsonb_typeof = 'number' so only native JSONB numbers are
        // included, matching the tile guard's typeof == "number" check.
        var sql = $"""
            SELECT ({textPath})::numeric AS val
            FROM {_featuresTable}
            WHERE {DatabaseSchema.LayerIdColumn} = @layerId
              AND jsonb_typeof({jsonbPath}) = {JsonbNumberType}
            LIMIT @limit
            """;

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        _ = command.Parameters.AddWithValue("@layerId", layerId);
        _ = command.Parameters.AddWithValue("@limit", limit);

        var values = new List<double>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                var val = reader.GetDecimal(0);
                values.Add((double)val);
            }
        }

        var result = values.ToArray();
        Array.Sort(result);
        return result;
    }

    private async Task<FieldProfile?> ProfileSingleFieldAsync(
        NpgsqlConnection connection,
        int layerId,
        FieldDefinition field,
        int sampleLimit,
        CancellationToken cancellationToken)
    {
        if (!FeatureQueryBuilder.IsValidFieldName(field.Name))
            return null; // Skip fields with non-standard names

        var textPath = DatabaseSchema.BuildJsonPath(field.Name);
        var jsonbPath = DatabaseSchema.BuildJsonbAccessor(field.Name);
        var isNumeric = field.Type is FieldType.Integer or FieldType.BigInteger
            or FieldType.Double or FieldType.Float;

        // Build statistics query — CTE bounds the scan to sampleLimit rows.
        // Aggregates on the sample are approximate but sufficient for styling decisions.
        // Use jsonb_typeof to detect native JSONB numbers so that profiling only
        // treats values as numeric when they will also be native numbers on tiles
        // (ST_AsMVT preserves JSONB types).  This keeps profiling aligned with
        // the MapLibre typeof == "number" guard in the generated expression.
        var numericAggregates = isNumeric
            ? $"""
               ,
                   MIN(CASE WHEN jsonb_typeof(field_jsonb) = {JsonbNumberType} THEN (field_text)::numeric END) AS min_val,
                   MAX(CASE WHEN jsonb_typeof(field_jsonb) = {JsonbNumberType} THEN (field_text)::numeric END) AS max_val,
                   AVG(CASE WHEN jsonb_typeof(field_jsonb) = {JsonbNumberType} THEN (field_text)::numeric END) AS mean_val,
                   STDDEV_SAMP(CASE WHEN jsonb_typeof(field_jsonb) = {JsonbNumberType} THEN (field_text)::numeric END) AS stddev_val
               """
            : "";

        var statsSql = $"""
            WITH sampled AS (
                SELECT {textPath} AS field_text, {jsonbPath} AS field_jsonb
                FROM {_featuresTable}
                WHERE {DatabaseSchema.LayerIdColumn} = @layerId
                LIMIT @sampleLimit
            )
            SELECT
                COUNT(*) AS total_count,
                COUNT(field_text) AS non_null_count,
                COUNT(DISTINCT field_text) AS distinct_count
                {numericAggregates}
            FROM sampled
            """;

        await using var statsCmd = new NpgsqlCommand(statsSql, connection);
        _ = statsCmd.Parameters.AddWithValue("@layerId", layerId);
        _ = statsCmd.Parameters.AddWithValue("@sampleLimit", sampleLimit);

        long totalCount = 0, nonNullCount = 0, distinctCount = 0;
        double? minVal = null, maxVal = null, meanVal = null, stddevVal = null;

        await using (var reader = await statsCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                totalCount = reader.GetInt64(0);
                nonNullCount = reader.GetInt64(1);
                distinctCount = reader.GetInt64(2);

                if (isNumeric && reader.FieldCount > 3)
                {
                    if (!reader.IsDBNull(3)) minVal = Convert.ToDouble(reader.GetDecimal(3), CultureInfo.InvariantCulture);
                    if (!reader.IsDBNull(4)) maxVal = Convert.ToDouble(reader.GetDecimal(4), CultureInfo.InvariantCulture);
                    if (!reader.IsDBNull(5)) meanVal = Convert.ToDouble(reader.GetDecimal(5), CultureInfo.InvariantCulture);
                    if (!reader.IsDBNull(6)) stddevVal = Convert.ToDouble(reader.GetDecimal(6), CultureInfo.InvariantCulture);
                }
            }
        }

        // Sample values query — bounded by sampleLimit, output capped at MaxSampleValues
        var sampleSql = $"""
            WITH sampled AS (
                SELECT {textPath} AS field_val
                FROM {_featuresTable}
                WHERE {DatabaseSchema.LayerIdColumn} = @layerId
                LIMIT @sampleLimit
            )
            SELECT field_val AS val, COUNT(*) AS freq
            FROM sampled
            WHERE field_val IS NOT NULL
            GROUP BY 1
            ORDER BY 2 DESC
            LIMIT @outputLimit
            """;

        await using var sampleCmd = new NpgsqlCommand(sampleSql, connection);
        _ = sampleCmd.Parameters.AddWithValue("@layerId", layerId);
        _ = sampleCmd.Parameters.AddWithValue("@sampleLimit", sampleLimit);
        _ = sampleCmd.Parameters.AddWithValue("@outputLimit", MaxSampleValues);

        var sampleValues = new List<SampleValue>();
        await using (var reader = await sampleCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    sampleValues.Add(new SampleValue(
                        reader.GetString(0),
                        reader.GetInt64(1)));
                }
            }
        }

        return new FieldProfile
        {
            FieldName = field.Name,
            FieldType = field.Type.ToString(),
            TotalCount = totalCount,
            NullCount = totalCount - nonNullCount,
            DistinctCount = distinctCount,
            MinValue = minVal,
            MaxValue = maxVal,
            MeanValue = meanVal,
            StandardDeviation = stddevVal,
            SampleValues = sampleValues
        };
    }

    [LoggerMessage(
        EventId = 4575,
        Level = LogLevel.Debug,
        Message = "Field profiling started for layer {LayerId}, {FieldCount} fields")]
    public static partial void FieldProfilingStarted(ILogger logger, int layerId, int fieldCount);

    [LoggerMessage(
        EventId = 4576,
        Level = LogLevel.Debug,
        Message = "Field profiling completed for layer {LayerId} in {ElapsedMs:F2}ms")]
    public static partial void FieldProfilingCompleted(ILogger logger, int layerId, double elapsedMs);
}
