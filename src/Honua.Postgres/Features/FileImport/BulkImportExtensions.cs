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
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.FileImport;

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

        // Bound serialization concurrency to avoid creating one Task per feature.
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, features.Count))
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, features.Count), parallelOptions, (index, ct) =>
        {
            var feature = features[index];

            // WKBWriter is not thread-safe; use one per worker iteration. CreateWkb
            // picks the writer dimensionality from the actual source geometry so XYZ/
            // XYM/XYZM features keep their Z/M ordinates while plain 2-D geometries use
            // a 2-D writer (forcing emitZ/emitM serializes NaN Z/M ordinates that
            // PostGIS rejects, dropping otherwise-valid rows) (#1981).
            wkbArray[index] = CreateWkb(feature, new WKBWriter()) ?? Array.Empty<byte>();

            var featureSrid = feature.Geometry?.SRID;
            sridArray[index] = featureSrid is > 0 ? featureSrid.Value : fallbackSourceSrid;
            propertiesArray[index] = BuildPropertiesJson(feature);

            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

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

    private static byte[]? CreateWkb(IFeature feature, WKBWriter plainWriter)
    {
        if (feature.Geometry == null)
        {
            return null;
        }

        var writer = SelectWkbWriter(feature.Geometry, plainWriter);
        return writer.Write(feature.Geometry);
    }

    /// <summary>
    /// Picks the WKB writer dimensionality from the actual source geometry so XYZ,
    /// XYM, and XYZM features round-trip their Z/M ordinates instead of being
    /// silently flattened (#1981). 2-D geometries reuse the shared plain writer:
    /// forcing emitZ/emitM on plain XY coordinates serializes NaN Z/M ordinates
    /// that PostGIS rejects, dropping otherwise-valid rows. Higher-dimension
    /// writers are allocated fresh because <see cref="WKBWriter"/> is not
    /// thread-safe and these bulk paths serialize features in parallel.
    /// </summary>
    private static WKBWriter SelectWkbWriter(
        NetTopologySuite.Geometries.Geometry geometry,
        WKBWriter plainWriter)
    {
        var filter = new ZmCoordinateFilter();
        geometry.Apply(filter);
        var hasZ = filter.HasZ;
        var hasM = filter.HasM;

        if (!hasZ && !hasM)
        {
            return plainWriter;
        }

        return new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: hasZ, emitM: hasM);
    }

    /// <summary>
    /// Coordinate-sequence filter that reports whether any coordinate carries a
    /// finite Z or M ordinate, without materializing the coordinate array.
    /// </summary>
    private sealed class ZmCoordinateFilter : NetTopologySuite.Geometries.ICoordinateSequenceFilter
    {
        public bool HasZ { get; private set; }

        public bool HasM { get; private set; }

        public bool Done => HasZ && HasM;

        public bool GeometryChanged => false;

        public void Filter(NetTopologySuite.Geometries.CoordinateSequence seq, int i)
        {
            if (!HasZ && seq.HasZ && !double.IsNaN(seq.GetZ(i)))
            {
                HasZ = true;
            }

            if (!HasM && seq.HasM && !double.IsNaN(seq.GetM(i)))
            {
                HasM = true;
            }
        }
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
