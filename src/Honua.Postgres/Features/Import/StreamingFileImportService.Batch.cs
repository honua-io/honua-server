// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Text.Json;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Import;

internal sealed partial class StreamingFileImportService
{
    /// <summary>
    /// Insert a batch of features with optional transaction.
    /// </summary>
    private async Task<(int imported, int failed)> InsertBatchAsync(
        NpgsqlConnection connection,
        string schemaName,
        string tableName,
        IReadOnlyList<IFeature> features,
        int sourceSrid,
        int targetSrid,
        WKBWriter wkbWriter,
        CancellationToken cancellationToken)
    {
        var imported = 0;
        var failed = 0;

        // Continue-on-error can't run inside a single transaction because any statement error aborts it.
        var useTransaction = _limits.UseTransactions && !_limits.ContinueOnError;
        await using var transaction = useTransaction
            ? await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            : null;

        try
        {
            try
            {
                imported = await InsertBatchFastAsync(
                    connection,
                    transaction,
                    schemaName,
                    tableName,
                    features,
                    sourceSrid,
                    targetSrid,
                    wkbWriter,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (_limits.ContinueOnError)
            {
                (imported, failed) = await InsertBatchIndividuallyAsync(
                    connection,
                    transaction,
                    schemaName,
                    tableName,
                    features,
                    sourceSrid,
                    targetSrid,
                    wkbWriter,
                    cancellationToken);
            }

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            throw;
        }

        return (imported, failed);
    }

    private async Task<int> InsertBatchFastAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schemaName,
        string tableName,
        IReadOnlyList<IFeature> features,
        int sourceSrid,
        int targetSrid,
        WKBWriter wkbWriter,
        CancellationToken cancellationToken)
    {
        var wkbs = new byte[]?[features.Count];
        var sourceSrids = new int[features.Count];
        var properties = new string[features.Count];

        for (var i = 0; i < features.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var feature = features[i];
            wkbs[i] = CreateWkb(feature, wkbWriter);

            // Use per-feature SRID when available (e.g. multi-layer FileGDBs
            // where each layer may have its own CRS).
            var featureSrid = feature.Geometry?.SRID;
            sourceSrids[i] = featureSrid is > 0 ? featureSrid.Value : sourceSrid;
            properties[i] = BuildPropertiesJson(feature);
        }

        const string sql = """
            SELECT honua.insert_import_feature(
                @schema_name,
                @table_name,
                payload.wkb,
                payload.source_srid,
                @target_srid,
                payload.properties)
            FROM unnest(@wkbs, @source_srids, @properties) AS payload(wkb, source_srid, properties)
            """;

        await using var command = new NpgsqlCommand(sql, connection)
        {
            Transaction = transaction
        };
        command.Parameters.Add("schema_name", NpgsqlDbType.Text).Value = schemaName;
        command.Parameters.Add("table_name", NpgsqlDbType.Text).Value = tableName;
        command.Parameters.Add("target_srid", NpgsqlDbType.Integer).Value = targetSrid;
        command.Parameters.Add("wkbs", NpgsqlDbType.Array | NpgsqlDbType.Bytea).Value = wkbs;
        command.Parameters.Add("source_srids", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value = sourceSrids;
        command.Parameters.Add("properties", NpgsqlDbType.Array | NpgsqlDbType.Jsonb).Value = properties;

        var imported = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            imported++;
        }

        return imported;
    }

    private async Task<(int imported, int failed)> InsertBatchIndividuallyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string schemaName,
        string tableName,
        IReadOnlyList<IFeature> features,
        int sourceSrid,
        int targetSrid,
        WKBWriter wkbWriter,
        CancellationToken cancellationToken)
    {
        var imported = 0;
        var failed = 0;

        await using var command = new NpgsqlCommand(InsertImportFeatureSql, connection)
        {
            Transaction = transaction
        };
        command.Parameters.Add("schema_name", NpgsqlDbType.Text).Value = schemaName;
        command.Parameters.Add("table_name", NpgsqlDbType.Text).Value = tableName;
        var wkbParameter = command.Parameters.Add("wkb", NpgsqlDbType.Bytea);
        var sourceSridParameter = command.Parameters.Add("source_srid", NpgsqlDbType.Integer);
        sourceSridParameter.Value = sourceSrid;
        command.Parameters.Add("target_srid", NpgsqlDbType.Integer).Value = targetSrid;
        var propertiesParameter = command.Parameters.Add("properties", NpgsqlDbType.Jsonb);

        foreach (var feature in features)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                wkbParameter.Value = CreateWkb(feature, wkbWriter) ?? (object)DBNull.Value;
                var featureSrid = feature.Geometry?.SRID;
                sourceSridParameter.Value = featureSrid is > 0 ? featureSrid.Value : sourceSrid;
                propertiesParameter.Value = BuildPropertiesJson(feature);
                await command.ExecuteNonQueryAsync(cancellationToken);
                imported++;
            }
            catch (Exception ex)
            {
                ImportLog.FeatureInsertFailed(_logger, ex, tableName);
                failed++;
                if (!_limits.ContinueOnError)
                {
                    throw;
                }
            }
        }

        return (imported, failed);
    }

    /// <summary>
    /// Build a JSON string of feature properties for import.
    /// </summary>
    private static string BuildPropertiesJson(IFeature feature)
    {
        if (feature.Attributes is null)
        {
            return "{}";
        }

        var names = feature.Attributes.GetNames();
        if (names.Length == 0)
        {
            return "{}";
        }

        var values = feature.Attributes.GetValues();
        var properties = new Dictionary<string, object?>(names.Length, StringComparer.Ordinal);
        for (var i = 0; i < names.Length; i++)
        {
            properties[names[i]] = values[i];
        }

        return JsonSerializer.Serialize(properties, ImportJsonContext.Default.DictionaryStringObject);
    }

    /// <summary>
    /// Create WKB for a feature geometry, enforcing configured validation limits.
    /// </summary>
    private byte[]? CreateWkb(IFeature feature, WKBWriter wkbWriter)
    {
        if (feature.Geometry == null)
        {
            return null;
        }

        if (_limits.ValidateGeometry)
        {
            var validationError = ValidateGeometry(feature.Geometry);
            if (validationError != null)
            {
                if (_limits.SkipInvalidGeometry)
                {
                    return null;
                }

                throw new InvalidOperationException($"Geometry validation failed: {validationError}");
            }
        }

        var writer = HasZ(feature.Geometry)
            ? new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: false)
            : wkbWriter;
        var wkb = writer.Write(feature.Geometry);

        if (_limits.ValidateGeometry && wkb.Length > _limits.MaxWkbSize)
        {
            if (_limits.SkipInvalidGeometry)
            {
                return null;
            }

            throw new InvalidOperationException(
                $"Geometry WKB size ({wkb.Length:N0} bytes) exceeds maximum allowed ({_limits.MaxWkbSize:N0} bytes)");
        }

        return wkb;
    }
}
