// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Features;
using Npgsql;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private static async Task<string> CreateFreshStagingAsync(
        NpgsqlTransaction transaction, (string Schema, int Srid) target, CancellationToken cancellationToken)
    {
        var name = $"stage_{Guid.NewGuid():N}__staging";
        // Strict CREATE refuses collisions; no existing dataset is reused or removed.
        const string formatSql = """
            SELECT format('CREATE SCHEMA IF NOT EXISTS %I; '
                'CREATE TABLE %I.%I (id SERIAL PRIMARY KEY, geometry GEOMETRY CHECK (ST_SRID(geometry) = %s), properties JSONB, created_at TIMESTAMPTZ DEFAULT NOW()); '
                'COMMENT ON TABLE %I.%I IS %L; '
                'CREATE INDEX %I ON %I.%I USING GIST (geometry); '
                'CREATE INDEX %I ON %I.%I USING GIN (properties);',
                @schema, @schema, @name, @srid, @schema, @name, 'honua:import-owned:v1',
                'idx_' || @name || '_geometry', @schema, @name,
                'idx_' || @name || '_properties', @schema, @name)
            """;
        await using var format = new NpgsqlCommand(formatSql, transaction.Connection!, transaction);
        format.Parameters.AddWithValue("schema", target.Schema);
        format.Parameters.AddWithValue("name", name);
        format.Parameters.AddWithValue("srid", target.Srid);
        var sql = (string)(await format.ExecuteScalarAsync(cancellationToken))!;
        await using var create = new NpgsqlCommand(sql, transaction.Connection!, transaction);
        await create.ExecuteNonQueryAsync(cancellationToken);
        return name;
    }

    // Create-only owns a uniquely named staging table and drops only that verified table.
    // Replace and staged append keep the shared sibling cleanup, which also reclaims its sequence and indexes.
    private static Task DropLoadStagingAsync(NpgsqlConnection connection,
        (ImportLoadMode Mode, string Schema, string Table, string Staging) tables, CancellationToken cancellationToken)
        => tables.Mode == ImportLoadMode.CreateOnly
            ? DiscardStagingAsync(connection, (tables.Schema, tables.Staging), cancellationToken)
            : DropStagingTableAsync(connection, tables.Schema, tables.Table, cancellationToken);

    private static async Task DiscardStagingAsync(
        NpgsqlConnection connection, (string Schema, string Staging) tables, CancellationToken cancellationToken)
    {
        // The ownership check locks the table, so the check and the drop share one transaction.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await ImportTableOwnership.AssertOwnedAsync(connection, tables.Schema, tables.Staging, cancellationToken))
        {
            return;
        }

        const string formatSql = "SELECT format('DROP TABLE %I.%I', @schema, @staging)";
        await using var format = new NpgsqlCommand(formatSql, connection, transaction);
        format.Parameters.AddWithValue("schema", tables.Schema);
        format.Parameters.AddWithValue("staging", tables.Staging);
        var sql = (string)(await format.ExecuteScalarAsync(cancellationToken))!;
        await using var drop = new NpgsqlCommand(sql, connection, transaction);
        await drop.ExecuteNonQueryAsync(cancellationToken);
        // Callers clean up with CancellationToken.None; never abandon a half-finished discard.
        await transaction.CommitAsync(CancellationToken.None);
    }

    private void ReportCommittedCompletion(IProgress<ImportProgress>? progress, ImportProgress value)
    {
        try
        {
            progress?.Report(value);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ImportLog.CompletionNotificationFailed(_logger, exception, value.JobId);
        }
    }

    private static partial class ImportLog
    {
        [LoggerMessage(
            EventId = 7448,
            Level = LogLevel.Warning,
            Message = "Completion notification failed after import {JobId} committed")]
        public static partial void CompletionNotificationFailed(ILogger logger, Exception exception, string jobId);
    }

    private async IAsyncEnumerable<IFeature> ReadGeoJsonFeatures(
        Stream source, ImportLoadMode loadMode, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var reader = loadMode == ImportLoadMode.CreateOnly
            ? new StreamingGeoJsonReader(_limits with { MaxFeaturesPerFile = 0 })
            : _geoJsonReader;
        int? expectedCount = loadMode == ImportLoadMode.CreateOnly
            ? await ValidateCreateOnlyGeoJsonAsync(reader, source, cancellationToken)
            : null;
        var count = 0;
        await foreach (var feature in reader.ReadFeaturesAsync(source, cancellationToken))
        {
            count++;
            yield return feature;
        }
        if (expectedCount.HasValue && count != expectedCount.Value)
        {
            throw new InvalidDataException("Create-only GeoJSON input was not read completely.");
        }
    }

    private static async Task<int> ValidateCreateOnlyGeoJsonAsync(
        StreamingGeoJsonReader reader, Stream source, CancellationToken cancellationToken)
    {
        if (!source.CanSeek)
        {
            throw new InvalidDataException("Create-only GeoJSON input must support complete validation and replay.");
        }
        var validation = await reader.ValidateAsync(source, cancellationToken);
        if (!validation.IsValid)
        {
            throw new InvalidDataException("Create-only GeoJSON input failed complete validation.");
        }
        return validation.FeatureCount;
    }

    private async IAsyncEnumerable<IFeature> EnforceCreateOnlyFeatureLimit(
        IAsyncEnumerable<IFeature> features, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var count = 0L;
        await foreach (var feature in features.WithCancellation(cancellationToken))
        {
            if (_limits.MaxFeaturesPerFile > 0 && ++count > _limits.MaxFeaturesPerFile)
            {
                throw new InvalidDataException("Create-only input exceeds the maximum feature count.");
            }
            yield return feature;
        }
    }

    private static async Task PromoteFreshStagingTableAsync(
        NpgsqlTransaction transaction, StagingPromotion promotion, CancellationToken cancellationToken)
    {
        await ImportTableOwnership.AssertExistingOwnedAsync(
            transaction.Connection!, promotion.Schema, promotion.StagingTable, cancellationToken);
        // Rename without DROP: a target created after the absence check must cause a
        // collision, even when its creator does not take the import advisory lock.
        // PostgreSQL format(%I) quotes every identifier, including canonical index names.
        // The key and sequence keep the staging table's unique stem: target-derived names can belong to a sibling.
        const string renameSql = """
            SELECT format(
                'ALTER TABLE %I.%I RENAME TO %I; '
                'ALTER INDEX IF EXISTS %I.%I RENAME TO %I; '
                'ALTER INDEX IF EXISTS %I.%I RENAME TO %I; '
                'ALTER INDEX IF EXISTS %I.%I RENAME TO %I; '
                'ALTER SEQUENCE IF EXISTS %I.%I RENAME TO %I;',
                @schema, @staging, @target,
                @schema, 'idx_' || @staging || '_geometry', honua.import_index_name(@target, 'geometry'),
                @schema, 'idx_' || @staging || '_properties', honua.import_index_name(@target, 'properties'),
                @schema, @staging || '_pkey', left(@staging, -length('__staging')) || '_pkey',
                @schema, @staging || '_id_seq', left(@staging, -length('__staging')) || '_id_seq')
            """;
        await using var format = new NpgsqlCommand(renameSql, transaction.Connection!, transaction);
        format.Parameters.AddWithValue("schema", promotion.Schema);
        format.Parameters.AddWithValue("staging", promotion.StagingTable);
        format.Parameters.AddWithValue("target", promotion.Table);
        var sql = (string)(await format.ExecuteScalarAsync(cancellationToken))!;
        await using var rename = new NpgsqlCommand(sql, transaction.Connection!, transaction);
        await rename.ExecuteNonQueryAsync(cancellationToken);
    }
}
