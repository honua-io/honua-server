// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private sealed record DirectImportGuard(ImportedDatasetSpatialMetadata.Identity Identity, double? CoordinateEpoch);
    private sealed record ImportTargetPreparation(
        string LoadTableName, bool HadExistingTarget, bool AppendUsesStaging,
        ImportedDatasetSpatialMetadata.Identity? LiveIdentity, int StorageSrid);

    private async Task<ImportTargetPreparation> PrepareImportTargetAsync(
        NpgsqlConnection connection, ImportRequest request, string schema, string table,
        int sourceSrid, double? coordinateEpoch, CancellationToken cancellationToken)
    {
        var storageSrid = request.TargetSrid ?? sourceSrid;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await ImportedDatasetSpatialMetadata.IsAvailableAsync(
                connection, transaction, _schemaConfiguration.MetadataSchema, cancellationToken))
        {
            throw new NotSupportedException("Imported dataset spatial metadata migration is required before importing.");
        }

        var existed = await ImportTableExistsAsync(connection, schema, table, cancellationToken, transaction);
        var mode = request.EffectiveLoadMode;
        // A replace drops the live table. Prove ownership before resolving geometry:
        // an unowned table may have no geometry column, and that lookup would report
        // a missing identity instead of IMPORT_TARGET_NOT_OWNED. The swap rechecks
        // under its own lock.
        if (existed && mode == ImportLoadMode.Replace &&
            !await TargetIsImporterOwnedAsync(connection, schema, table, cancellationToken, transaction))
        {
            throw new ImportTableOwnershipException(schema, table);
        }
        ImportedDatasetSpatialMetadata.Identity? identity = null;
        if (existed)
        {
            identity = await ImportedDatasetSpatialMetadata.LockAndResolveAsync(
                connection, transaction, schema, table, "geometry", cancellationToken);
        }
        if (mode != ImportLoadMode.Replace)
        {
            if (!existed)
            {
                await EnsureTableAsync(connection, schema, table, storageSrid, cancellationToken, transaction);
                identity = await ImportedDatasetSpatialMetadata.LockAndResolveAsync(
                    connection, transaction, schema, table, "geometry", cancellationToken);
                await ImportedDatasetSpatialMetadata.WriteAsync(
                    connection, transaction, _schemaConfiguration.MetadataSchema, schema, table, "geometry",
                    identity.Value, storageSrid, coordinateEpoch, cancellationToken);
            }
            else
            {
                var facts = await ImportedDatasetSpatialMetadata.ReadAsync(
                    connection, transaction, _schemaConfiguration.MetadataSchema, schema, table, "geometry",
                    identity!.Value, cancellationToken);
                storageSrid = request.TargetSrid ?? facts?.StorageSrid ?? sourceSrid;
                ImportedDatasetSpatialMetadata.ValidateAppend(facts, storageSrid, coordinateEpoch);
                if (coordinateEpoch.HasValue && sourceSrid != storageSrid)
                {
                    throw new InvalidDataException(
                        "Coordinate epoch cannot be preserved through a CRS transformation; use the source storage CRS.");
                }
            }
            if (mode == ImportLoadMode.Upsert)
            {
                await EnsureUpsertKeyAsync(connection, schema, table, request.KeyColumns, cancellationToken, transaction);
            }
        }

        var stagedAppend = mode == ImportLoadMode.Append &&
            await TargetIsImporterOwnedAsync(connection, schema, table, cancellationToken, transaction);
        if (request.CommitParticipant is not null && mode != ImportLoadMode.Replace && !stagedAppend)
        {
            // Keep the direct-mode refusal inside preparation so new schema/facts roll back too.
            throw new InvalidDataException(
                "This import must commit its receipt with the rows. The target is not an importer-owned table.");
        }
        var loadTable = mode == ImportLoadMode.Replace || stagedAppend
            ? await CreateStagingTableAsync(connection, schema, table, storageSrid, cancellationToken, transaction)
            : table;
        await transaction.CommitSafelyAsync(cancellationToken);
        return new ImportTargetPreparation(loadTable, existed, stagedAppend, identity, storageSrid);
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
