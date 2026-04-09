// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Data;
using System.Text.Json;
using Microsoft.Extensions.ObjectPool;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Extensions for efficient bulk import operations with memory management
/// PERFORMANCE FIX: Enhanced with JSON optimization and object pooling
/// </summary>
internal static class BulkImportExtensions
{
    private const string BulkInsertSql = "SELECT processed_count, failed_count FROM honua.bulk_insert_import_features(@table_name, @wkb_array, @source_srid_array, @target_srid, @properties_array)";
    private const string PrepareCopyTableSql = "SELECT honua.prepare_bulk_copy_table(@table_name)";
    private const string FinalizeCopyTableSql = "SELECT honua.finalize_bulk_copy(@source_table, @target_table)";

    // PERFORMANCE FIX: Object pooling for dictionary creation to reduce allocations
    private static readonly ObjectPool<Dictionary<string, object?>> _dictionaryPool = new DefaultObjectPool<Dictionary<string, object?>>(
        new DictionaryPooledObjectPolicy<string, object?>(), maximumRetained: 100);

    /// <summary>
    /// Performs bulk insert using array parameters for optimal performance.
    /// PERFORMANCE FIX: Now supports per-row error handling via PostgreSQL bulk_insert_import_features function.
    /// </summary>
    public static async Task<(int imported, int failed)> BulkInsertFeaturesAsync(
        this NpgsqlConnection connection,
        string tableName,
        IReadOnlyList<IFeature> features,
        int targetSrid,
        WKBWriter wkbWriter,
        CancellationToken cancellationToken)
    {
        return await BulkInsertFeaturesAsync(connection, tableName, features, targetSrid, 0, wkbWriter, cancellationToken);
    }

    /// <summary>
    /// Performs bulk insert using array parameters for optimal performance with fallback source SRID.
    /// PERFORMANCE FIX: Supports per-row error handling via PostgreSQL bulk_insert_import_features function.
    /// </summary>
    public static async Task<(int imported, int failed)> BulkInsertFeaturesAsync(
        this NpgsqlConnection connection,
        string tableName,
        IReadOnlyList<IFeature> features,
        int targetSrid,
        int fallbackSourceSrid,
        WKBWriter wkbWriter,
        CancellationToken cancellationToken)
    {
        if (!features.Any())
            return (0, 0);

        var wkbArray = new byte[features.Count][];
        var sridArray = new int[features.Count];
        var propertiesArray = new string[features.Count];

        // Pre-allocate and serialize in parallel where possible
        var tasks = new Task[features.Count];
        for (var i = 0; i < features.Count; i++)
        {
            var index = i;
            var feature = features[i];

            tasks[i] = Task.Run(() =>
            {
                // WKB serialization
                wkbArray[index] = CreateWkb(feature, wkbWriter) ?? Array.Empty<byte>();

                // SRID extraction with fallback (matches individual insert behavior)
                var featureSrid = feature.Geometry?.SRID;
                sridArray[index] = featureSrid is > 0 ? featureSrid.Value : fallbackSourceSrid;

                // JSON serialization
                propertiesArray[index] = BuildPropertiesJson(feature);
            }, cancellationToken);
        }

        await Task.WhenAll(tasks);

        await using var command = new NpgsqlCommand(BulkInsertSql, connection);
        command.Parameters.Add("table_name", NpgsqlDbType.Text).Value = tableName;
        command.Parameters.Add("wkb_array", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = wkbArray;
        command.Parameters.Add("source_srid_array", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value = sridArray;
        command.Parameters.Add("target_srid", NpgsqlDbType.Integer).Value = targetSrid;
        command.Parameters.Add("properties_array", NpgsqlDbType.Array | NpgsqlDbType.Jsonb).Value = propertiesArray;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            var imported = reader.GetInt32("processed_count");
            var failed = reader.GetInt32("failed_count");
            return (imported, failed);
        }

        return (0, features.Count);
    }

    /// <summary>
    /// Performs high-performance bulk insert using COPY for very large batches
    /// </summary>
    public static async Task<int> BulkCopyFeaturesAsync(
        this NpgsqlConnection connection,
        string tableName,
        IAsyncEnumerable<IFeature> features,
        int targetSrid,
        WKBWriter wkbWriter,
        CancellationToken cancellationToken)
    {
        // Prepare temporary table
        await using var prepareCommand = new NpgsqlCommand(PrepareCopyTableSql, connection);
        prepareCommand.Parameters.AddWithValue("table_name", tableName);
        var tempTableName = (string)(await prepareCommand.ExecuteScalarAsync(cancellationToken) ?? throw new InvalidOperationException("Failed to create temporary table"));

        // Stream features into temp table using COPY
        var copyStatement = $"COPY {tempTableName} (wkb, source_srid, target_srid, properties) FROM STDIN (FORMAT BINARY)";

        await using var writer = await connection.BeginBinaryImportAsync(copyStatement, cancellationToken);

        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            await writer.StartRowAsync(cancellationToken);

            var wkb = CreateWkb(feature, wkbWriter);
            await writer.WriteAsync(wkb ?? Array.Empty<byte>(), NpgsqlDbType.Bytea, cancellationToken);

            var sourceSrid = feature.Geometry?.SRID ?? 0;
            await writer.WriteAsync(sourceSrid, NpgsqlDbType.Integer, cancellationToken);

            await writer.WriteAsync(targetSrid, NpgsqlDbType.Integer, cancellationToken);

            var propertiesJson = BuildPropertiesJson(feature);
            await writer.WriteAsync(propertiesJson, NpgsqlDbType.Jsonb, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);

        // Finalize by moving from temp table to target table
        await using var finalizeCommand = new NpgsqlCommand(FinalizeCopyTableSql, connection);
        finalizeCommand.Parameters.AddWithValue("source_table", tempTableName ?? throw new InvalidOperationException("Temporary table name is null"));
        finalizeCommand.Parameters.AddWithValue("target_table", tableName);

        return (int)(await finalizeCommand.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static byte[]? CreateWkb(IFeature feature, WKBWriter writer)
    {
        return feature.Geometry != null ? writer.Write(feature.Geometry) : null;
    }

    /// <summary>
    /// PERFORMANCE FIX: Optimized JSON building using object pooling and pre-compiled context
    /// </summary>
    private static string BuildPropertiesJson(IFeature feature)
    {
        if (feature.Attributes == null || feature.Attributes.GetNames().Length == 0)
            return "{}";

        // PERFORMANCE FIX: Use pooled dictionary to reduce allocations
        var properties = _dictionaryPool.Get();
        try
        {
            foreach (var name in feature.Attributes.GetNames())
            {
                properties[name] = feature.Attributes[name];
            }

            // Use the shared import source-generated context so native AOT publish
            // does not fall back to reflection-based metadata generation.
            return JsonSerializer.Serialize(properties, ImportJsonContext.Default.DictionaryStringObject);
        }
        finally
        {
            // Clear and return dictionary to pool
            properties.Clear();
            _dictionaryPool.Return(properties);
        }
    }

    /// <summary>
    /// PERFORMANCE FIX: Custom dictionary pool policy for object pooling
    /// </summary>
    private sealed class DictionaryPooledObjectPolicy<TKey, TValue> : IPooledObjectPolicy<Dictionary<TKey, TValue>>
        where TKey : notnull
    {
        public Dictionary<TKey, TValue> Create()
        {
            return new Dictionary<TKey, TValue>(capacity: 16); // Pre-size for typical feature attribute count
        }

        public bool Return(Dictionary<TKey, TValue> obj)
        {
            // Only return to pool if not too large to avoid memory bloat
            return obj.Count <= 100;
        }
    }
}
