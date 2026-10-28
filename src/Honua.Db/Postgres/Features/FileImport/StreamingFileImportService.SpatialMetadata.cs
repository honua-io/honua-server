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
        ImportedDatasetSpatialMetadata.Identity? LiveIdentity);

    private async Task<ImportTargetPreparation> PrepareImportTargetAsync(
        NpgsqlConnection connection, ImportRequest request, string schema, string table,
        double? coordinateEpoch, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await ImportedDatasetSpatialMetadata.IsAvailableAsync(
                connection, transaction, _schemaConfiguration.MetadataSchema, cancellationToken))
        {
            throw new NotSupportedException("Imported dataset spatial metadata migration is required before importing.");
        }

        var existed = await ImportTableExistsAsync(connection, schema, table, cancellationToken, transaction);
        ImportedDatasetSpatialMetadata.Identity? identity = null;
        if (existed)
        {
            identity = await ImportedDatasetSpatialMetadata.LockAndResolveAsync(
                connection, transaction, schema, table, "geometry", cancellationToken);
        }
        var mode = request.EffectiveLoadMode;
        if (mode != ImportLoadMode.Replace)
        {
            if (!existed)
            {
                await EnsureTableAsync(connection, schema, table, request.TargetSrid, cancellationToken, transaction);
                identity = await ImportedDatasetSpatialMetadata.LockAndResolveAsync(
                    connection, transaction, schema, table, "geometry", cancellationToken);
                await ImportedDatasetSpatialMetadata.WriteAsync(
                    connection, transaction, _schemaConfiguration.MetadataSchema, schema, table, "geometry",
                    identity.Value, request.TargetSrid, coordinateEpoch, cancellationToken);
            }
            else
            {
                var facts = await ImportedDatasetSpatialMetadata.ReadAsync(
                    connection, transaction, _schemaConfiguration.MetadataSchema, schema, table, "geometry",
                    identity!.Value, cancellationToken);
                ImportedDatasetSpatialMetadata.ValidateAppend(facts, request.TargetSrid, coordinateEpoch);
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
            ? await CreateStagingTableAsync(connection, schema, table, request.TargetSrid, cancellationToken, transaction)
            : table;
        await transaction.CommitSafelyAsync(cancellationToken);
        return new ImportTargetPreparation(loadTable, existed, stagedAppend, identity);
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
