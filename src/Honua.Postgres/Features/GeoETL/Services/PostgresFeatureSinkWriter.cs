// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Import;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.GeoETL.Services;

/// <summary>
/// PostgreSQL / PostGIS implementation of <see cref="IFeatureSinkWriter"/> for the
/// GeoETL Honua-layer sink. Mirrors the <c>StreamingFileImportService</c> insert path:
/// it ensures the target through <c>honua.create_import_table</c> and inserts batches
/// through <c>honua.insert_import_feature</c> using the same WKB + properties JSONB
/// projection and the same set-based <c>unnest</c> insert shape.
/// </summary>
/// <remarks>
/// Phase 1 rollback is the soft-delete batch id (ADR-0038). Each row's properties
/// JSONB carries a reserved <c>__pipeline_batch_id</c> key so a failed run can
/// <c>DELETE ... WHERE properties->>'__pipeline_batch_id' = @batchId</c>. The dedicated
/// <c>pipeline_batch_id</c> column escalation is documented as remaining work.
/// </remarks>
internal sealed partial class PostgresFeatureSinkWriter(
    IDatabaseConnectionProvider connectionProvider,
    ILogger<PostgresFeatureSinkWriter> logger) : IFeatureSinkWriter
{
    /// <summary>Reserved property key tagging every row with its run batch id.</summary>
    public const string BatchIdPropertyKey = "__pipeline_batch_id";

    private const string CreateImportTableSql =
        "SELECT honua.create_import_table(@schema_name, @table_name, @target_srid)";

    private const string InsertBatchSql = """
        SELECT honua.insert_import_feature(
            @schema_name,
            @table_name,
            payload.wkb,
            payload.source_srid,
            @target_srid,
            payload.properties)
        FROM unnest(@wkbs, @source_srids, @properties) AS payload(wkb, source_srid, properties)
        """;

    /// <inheritdoc />
    public async Task EnsureTargetAsync(
        string schema,
        string table,
        int targetSrid,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;

        await using var command = new NpgsqlCommand(CreateImportTableSql, connection);
        command.Parameters.AddWithValue("schema_name", schema);
        command.Parameters.AddWithValue("table_name", table);
        command.Parameters.AddWithValue("target_srid", targetSrid);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        Log.TargetEnsured(logger, schema, table, targetSrid);
    }

    /// <inheritdoc />
    public async Task<long> InsertBatchAsync(
        string schema,
        string table,
        IReadOnlyList<IFeature> features,
        int sourceSrid,
        int targetSrid,
        string batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (features.Count == 0)
        {
            return 0;
        }

        var wkbWriter = new WKBWriter();
        var wkbs = new byte[]?[features.Count];
        var sourceSrids = new int[features.Count];
        var properties = new string[features.Count];

        for (var i = 0; i < features.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var feature = features[i];
            wkbs[i] = feature.Geometry is null ? null : wkbWriter.Write(feature.Geometry);
            var featureSrid = feature.Geometry?.SRID;
            sourceSrids[i] = featureSrid is > 0 ? featureSrid.Value : sourceSrid;
            properties[i] = BuildPropertiesJson(feature, batchId);
        }

        await using var lease = await connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlConnection connection = lease;

        await using var command = new NpgsqlCommand(InsertBatchSql, connection);
        command.Parameters.AddWithValue("schema_name", schema);
        command.Parameters.AddWithValue("table_name", table);
        command.Parameters.Add("target_srid", NpgsqlDbType.Integer).Value = targetSrid;
        command.Parameters.Add("wkbs", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = wkbs;
        command.Parameters.Add("source_srids", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value = sourceSrids;
        command.Parameters.Add("properties", NpgsqlDbType.Array | NpgsqlDbType.Jsonb).Value = properties;

        long inserted = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            inserted++;
        }

        Log.BatchInserted(logger, inserted, schema, table, batchId);
        return inserted;
    }

    private static string BuildPropertiesJson(IFeature feature, string batchId)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [BatchIdPropertyKey] = batchId
        };

        if (feature.Attributes is { } attributes)
        {
            var names = attributes.GetNames();
            var values = attributes.GetValues();
            for (var i = 0; i < names.Length; i++)
            {
                properties[names[i]] = values[i];
            }
        }

        return JsonSerializer.Serialize(properties, ImportJsonContext.Default.DictionaryStringObject);
    }

    private static partial class Log
    {
        [LoggerMessage(9700, LogLevel.Information,
            "GeoETL sink ensured target {Schema}.{Table} (SRID {TargetSrid})")]
        public static partial void TargetEnsured(ILogger logger, string schema, string table, int targetSrid);

        [LoggerMessage(9701, LogLevel.Information,
            "GeoETL sink inserted {Count} feature(s) into {Schema}.{Table} (batch {BatchId})")]
        public static partial void BatchInserted(ILogger logger, long count, string schema, string table, string batchId);
    }
}
