// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private sealed class ImportSridValidationException(IReadOnlyList<ImportValidationIssue> issues)
        : Exception(issues[0].Message)
    {
        public IReadOnlyList<ImportValidationIssue> Issues { get; } = issues;
    }

    private sealed record DirectImportGuard(ImportedDatasetSpatialMetadata.Identity Identity, double? CoordinateEpoch);
    private sealed record ImportTargetPreparation
    {
        public required string LoadTableName { get; init; }
        public required bool HadExistingTarget { get; init; }
        public required bool AppendUsesStaging { get; init; }
        public ImportedDatasetSpatialMetadata.Identity? LiveIdentity { get; init; }
        public required int StorageSrid { get; init; }
    }

    private sealed record ImportTargetOptions
    {
        public required ImportRequest Request { get; init; }
        public required string Schema { get; init; }
        public required string Table { get; init; }
        public required int SourceSrid { get; init; }
        public double? CoordinateEpoch { get; init; }
    }

    private async Task<ImportTargetPreparation> PrepareImportTargetAsync(
        NpgsqlConnection connection, ImportTargetOptions options, CancellationToken cancellationToken)
    {
        var storageSrid = options.Request.TargetSrid ?? options.SourceSrid;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await ImportedDatasetSpatialMetadata.IsAvailableAsync(
                connection, transaction, _schemaConfiguration.MetadataSchema, cancellationToken))
        {
            throw new NotSupportedException("Imported dataset spatial metadata migration is required before importing.");
        }

        var existed = await ImportTableExistsAsync(connection, options.Schema, options.Table, cancellationToken, transaction);
        var mode = options.Request.EffectiveLoadMode;
        // A replace drops the live table. Prove ownership before resolving geometry:
        // an unowned table may have no geometry column, and that lookup would report
        // a missing identity instead of IMPORT_TARGET_NOT_OWNED. The swap rechecks
        // under its own lock.
        if (existed && mode == ImportLoadMode.Replace &&
            !await TargetIsImporterOwnedAsync(connection, options.Schema, options.Table, cancellationToken, transaction))
        {
            throw new ImportTableOwnershipException(options.Schema, options.Table);
        }
        ImportedDatasetSpatialMetadata.Identity? identity = null;
        if (existed)
        {
            identity = await ImportedDatasetSpatialMetadata.LockAndResolveAsync(
                connection, transaction, options.Schema, options.Table, "geometry", cancellationToken);
        }
        if (mode != ImportLoadMode.Replace)
        {
            if (!existed)
            {
                await EnsureTableAsync(connection, options.Schema, options.Table, storageSrid, cancellationToken, transaction);
                identity = await ImportedDatasetSpatialMetadata.LockAndResolveAsync(
                    connection, transaction, options.Schema, options.Table, "geometry", cancellationToken);
                await ImportedDatasetSpatialMetadata.WriteAsync(
                    connection, transaction, _schemaConfiguration.MetadataSchema, options.Schema, options.Table, "geometry",
                    identity.Value, storageSrid, options.CoordinateEpoch, cancellationToken);
            }
            else
            {
                var facts = await ImportedDatasetSpatialMetadata.ReadAsync(
                    connection, transaction, _schemaConfiguration.MetadataSchema, options.Schema, options.Table, "geometry",
                    identity!.Value, cancellationToken);
                storageSrid = await ResolveAppendStorageSridAsync(facts, options, cancellationToken);
            }
            if (mode == ImportLoadMode.Upsert)
            {
                await EnsureUpsertKeyAsync(connection, options.Schema, options.Table, options.Request.KeyColumns, cancellationToken, transaction);
            }
        }

        var stagedAppend = mode == ImportLoadMode.Append &&
            await TargetIsImporterOwnedAsync(connection, options.Schema, options.Table, cancellationToken, transaction);
        if (options.Request.CommitParticipant is not null && mode != ImportLoadMode.Replace && !stagedAppend)
        {
            // Keep the direct-mode refusal inside preparation so new schema/facts roll back too.
            throw new InvalidDataException(
                "This import must commit its receipt with the rows. The target is not an importer-owned table.");
        }
        var loadTable = mode == ImportLoadMode.Replace || stagedAppend
            ? await CreateStagingTableAsync(connection, options.Schema, options.Table, storageSrid, cancellationToken, transaction)
            : options.Table;
        await transaction.CommitSafelyAsync(cancellationToken);
        return new ImportTargetPreparation
        {
            LoadTableName = loadTable,
            HadExistingTarget = existed,
            AppendUsesStaging = stagedAppend,
            LiveIdentity = identity,
            StorageSrid = storageSrid
        };
    }

    private async Task<int> ResolveAppendStorageSridAsync(
        ImportedDatasetSpatialMetadata.Facts? facts, ImportTargetOptions options, CancellationToken cancellationToken)
    {
        var storageSrid = ResolveAppendStorageSrid(facts, options);
        if (storageSrid == (options.Request.TargetSrid ?? options.SourceSrid))
        {
            // The request pair was validated before preparation; skip extra pooled connections under the lock.
            return storageSrid;
        }
        var issues = await ValidateImportSridsAsync(options.SourceSrid, storageSrid, cancellationToken);
        if (issues.Count > 0)
        {
            throw new ImportSridValidationException(issues);
        }
        return storageSrid;
    }

    private static int ResolveAppendStorageSrid(
        ImportedDatasetSpatialMetadata.Facts? facts, ImportTargetOptions options)
    {
        var storageSrid = options.Request.TargetSrid ?? facts?.StorageSrid ?? options.SourceSrid;
        ImportedDatasetSpatialMetadata.ValidateAppend(facts, storageSrid, options.CoordinateEpoch);
        if (options.CoordinateEpoch.HasValue && options.SourceSrid != storageSrid)
        {
            throw new InvalidDataException(
                "Coordinate epoch cannot be preserved through a CRS transformation; use the source storage CRS.");
        }
        return storageSrid;
    }

    private async Task ValidateDirectImportAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string table,
        int storageSrid, DirectImportGuard guard, CancellationToken cancellationToken)
    {
        var identity = await ImportedDatasetSpatialMetadata.LockAndResolveAsync(
            connection, transaction, schema, table, "geometry", cancellationToken);
        if (identity != guard.Identity)
        {
            throw new InvalidDataException("Import target identity changed while reading the source.");
        }
        var facts = await ImportedDatasetSpatialMetadata.ReadAsync(
            connection, transaction, _schemaConfiguration.MetadataSchema,
            schema, table, "geometry", identity, cancellationToken);
        ImportedDatasetSpatialMetadata.ValidateAppend(facts, storageSrid, guard.CoordinateEpoch);
    }
    private static async Task ValidatePromotionIdentityAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string table,
        ImportedDatasetSpatialMetadata.Identity? expectedIdentity, CancellationToken cancellationToken)
    {
        if (expectedIdentity is null)
        {
            if (await ImportTableExistsAsync(connection, schema, table, cancellationToken, transaction))
            {
                throw new InvalidDataException("Import target identity changed while reading the source.");
            }
            return;
        }
        var current = await ImportedDatasetSpatialMetadata.LockAndResolveAsync(
            connection, transaction, schema, table, "geometry", cancellationToken);
        if (current != expectedIdentity.Value)
        {
            throw new InvalidDataException("Import target identity changed while reading the source.");
        }
    }

}
