// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Db.Postgres.Features.FileImport;
using Npgsql;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    private sealed record StorageEpochContext(NpgsqlConnection Connection, NpgsqlTransaction Transaction,
        LayerPublishRequest Request, string Schema, string Table, string? GeometryColumn,
        int SourceStorageSrid, int PublishedStorageSrid);

    private Task<double?> ResolveImportedStorageEpochAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, LayerPublishRequest request,
        string schema, string table, string? geometryColumn, int sourceStorageSrid, int publishedStorageSrid,
        CancellationToken cancellationToken) =>
        ResolveStorageEpochCoreAsync(new StorageEpochContext(connection, transaction, request, schema, table,
            geometryColumn, sourceStorageSrid, publishedStorageSrid), ReadLockedSpatialFactsAsync, cancellationToken);

    private Task<double?> ReadImportedStorageEpochAsync(StorageEpochContext context, CancellationToken cancellationToken) =>
        ResolveStorageEpochCoreAsync(context, ImportedDatasetSpatialMetadata.ReadSnapshotAsync, cancellationToken);

    private static Task<ImportedDatasetSpatialMetadata.Facts?> ReadLockedSpatialFactsAsync(
        ImportedDatasetSpatialMetadata.ReadTarget target, CancellationToken cancellationToken) =>
        ImportedDatasetSpatialMetadata.ReadAsync(target.Connection, target.Transaction, target.MetadataSchema,
            target.Schema, target.Table, target.GeometryColumn, target.Identity, cancellationToken);

    private async Task<double?> ResolveStorageEpochCoreAsync(StorageEpochContext context,
        Func<ImportedDatasetSpatialMetadata.ReadTarget, CancellationToken, Task<ImportedDatasetSpatialMetadata.Facts?>> readFacts,
        CancellationToken cancellationToken)
    {
        var (connection, transaction, _, schema, table, geometryColumn, _, _) = context;
        var hint = ValidateEpochHint(context);
        if (string.IsNullOrWhiteSpace(geometryColumn))
        {
            return null;
        }

        // Publication can target external databases with no Honua import migrations. Discover
        // the registry on this actual source connection before referencing it; never use a
        // different database's relation OID or a mutable ownership comment as epoch authority.
        if (!await ImportedDatasetSpatialMetadata.IsAvailableAsync(
                connection, transaction, _schemaConfiguration.MetadataSchema, cancellationToken))
        {
            if (hint.HasValue)
            {
                throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                    "Coordinate epoch cannot be verified against imported source metadata.");
            }
            return null;
        }
        var identity = await ImportedDatasetSpatialMetadata.LockAndResolveAsync(
            connection, transaction, schema, table, geometryColumn, cancellationToken);
        var facts = await readFacts(new ImportedDatasetSpatialMetadata.ReadTarget(
            connection, transaction, _schemaConfiguration.MetadataSchema, schema, table, geometryColumn, identity),
            cancellationToken);
        return ValidateResolvedEpoch(context, facts);
    }

    private static double? ValidateEpochHint(StorageEpochContext context)
    {
        var hint = context.Request.StorageCrsCoordinateEpoch;
        if (hint.HasValue && !double.IsFinite(hint.Value))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation, "Coordinate epoch must be finite.");
        }
        if (string.IsNullOrWhiteSpace(context.GeometryColumn) && hint.HasValue)
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation, "Coordinate epoch requires a geometry column.");
        }
        return hint;
    }

    private static double? ValidateResolvedEpoch(StorageEpochContext context, ImportedDatasetSpatialMetadata.Facts? facts)
    {
        var hint = context.Request.StorageCrsCoordinateEpoch;
        if (facts is null)
        {
            if (hint.HasValue)
            {
                throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                    "Coordinate epoch cannot be verified for the current source relation and geometry column.");
            }
            return null;
        }
        if (facts.Value.StorageSrid != context.SourceStorageSrid ||
            (hint.HasValue && hint != facts.Value.CoordinateEpoch))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "Source storage CRS or coordinate epoch conflicts with imported metadata.");
        }
        if (facts.Value.CoordinateEpoch.HasValue && context.PublishedStorageSrid != facts.Value.StorageSrid)
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "Coordinate epoch cannot be preserved through this managed-copy CRS transformation.");
        }
        return facts.Value.CoordinateEpoch;
    }
}
