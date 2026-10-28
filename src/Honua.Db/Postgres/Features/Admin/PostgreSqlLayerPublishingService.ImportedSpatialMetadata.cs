// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Db.Postgres.Features.FileImport;
using Npgsql;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    private async Task<double?> ResolveImportedStorageEpochAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, LayerPublishRequest request,
        string schema, string table, string? geometryColumn, int sourceStorageSrid, int publishedStorageSrid,
        CancellationToken cancellationToken)
    {
        var hint = request.StorageCrsCoordinateEpoch;
        if (hint.HasValue && !double.IsFinite(hint.Value))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation, "Coordinate epoch must be finite.");
        }
        if (string.IsNullOrWhiteSpace(geometryColumn))
        {
            if (hint.HasValue)
            {
                throw new LayerPublishingException(LayerPublishingErrorKind.Validation, "Coordinate epoch requires a geometry column.");
            }
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
        var facts = await ImportedDatasetSpatialMetadata.ReadAsync(
            connection, transaction, _schemaConfiguration.MetadataSchema, schema, table, geometryColumn,
            identity, cancellationToken);
        if (facts is null)
        {
            if (hint.HasValue)
            {
                throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                    "Coordinate epoch cannot be verified for the current source relation and geometry column.");
            }
            return null;
        }
        if (facts.Value.StorageSrid != sourceStorageSrid ||
            (hint.HasValue && hint != facts.Value.CoordinateEpoch))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "Source storage CRS or coordinate epoch conflicts with imported metadata.");
        }
        if (facts.Value.CoordinateEpoch.HasValue && publishedStorageSrid != facts.Value.StorageSrid)
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "Coordinate epoch cannot be preserved through this managed-copy CRS transformation.");
        }
        return facts.Value.CoordinateEpoch;
    }
}
