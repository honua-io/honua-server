// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.Db.Postgres.Features.FileImport;

internal sealed partial class StreamingFileImportService
{
    private const string ImportedGeometryTypeConstraint = "honua_import_geometry_type";

    // Only remove our inferred constraint, and only on import-owned tables. Source
    // and customer constraints must keep governing direct append/upsert writes.
    private static async Task<bool> ClearImportedGeometryTypeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string table,
        CancellationToken cancellationToken)
    {
        if (!await TargetIsImporterOwnedAsync(connection, schema, table, cancellationToken, transaction))
        {
            return false;
        }

        var qualified = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        await using var command = new NpgsqlCommand(
            $"ALTER TABLE {qualified} DROP CONSTRAINT IF EXISTS {ImportedGeometryTypeConstraint}",
            connection, transaction);
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    // Infer from final stored geometries, after repair/transformation and row rejection.
    // A check constraint is visible through PostGIS geometry_columns without forcing
    // all source coordinates to the same XY/XYZ/XYM/XYZM typmod. The existing SRID
    // constraint remains authoritative for the transformed storage CRS.
    private static async Task RefreshImportedGeometryTypeAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null)
    {
        await using var ownedTransaction = transaction is null
            ? await connection.BeginTransactionAsync(cancellationToken)
            : null;
        transaction ??= ownedTransaction!;
        if (!await ClearImportedGeometryTypeAsync(connection, transaction, schema, table, cancellationToken))
        {
            return;
        }

        var qualified = $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        // DROP CONSTRAINT holds the table lock until commit, so concurrent writes
        // cannot change the inferred set between this scan and constraint validation.
        // Both passes can read the whole import. Match typed-column promotion's
        // bulk timeout policy; cancellation still follows the import commit path.
        string? geometryType = null;
        await using (var command = new NpgsqlCommand($"""
            SELECT DISTINCT CASE ST_GeometryType(geometry)
                WHEN 'ST_Point' THEN 'POINT' WHEN 'ST_MultiPoint' THEN 'POINT'
                WHEN 'ST_LineString' THEN 'LINESTRING' WHEN 'ST_MultiLineString' THEN 'LINESTRING'
                WHEN 'ST_Polygon' THEN 'POLYGON' WHEN 'ST_MultiPolygon' THEN 'POLYGON'
                ELSE 'OTHER' END
            FROM {qualified}
            WHERE geometry IS NOT NULL
            LIMIT 2
            """, connection, transaction))
        {
            command.CommandTimeout = 0;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                geometryType = reader.GetString(0);
                if (await reader.ReadAsync(cancellationToken))
                {
                    geometryType = null;
                }
            }
        }

        // All parts in a supported family share its multi type. This permits later
        // single- or multi-part edits while keeping the discovered type accurate.
        // These SQL literals come from the allowlist, never directly from input.
        var (type, singleType) = geometryType switch
        {
            "POINT" => ("MULTIPOINT", "ST_Point"),
            "LINESTRING" => ("MULTILINESTRING", "ST_LineString"),
            "POLYGON" => ("MULTIPOLYGON", "ST_Polygon"),
            _ => ((string?)null, (string?)null)
        };
        if (type is not null)
        {
            await using (var promote = new NpgsqlCommand($"""
                UPDATE {qualified} SET geometry = ST_Multi(geometry)
                WHERE ST_GeometryType(geometry) = '{singleType}'
                """, connection, transaction))
            {
                promote.CommandTimeout = 0;
                await promote.ExecuteNonQueryAsync(cancellationToken);
            }

            // GeometryType appends M for XYM. Both spellings describe the same
            // geometry family; PostGIS reads the first literal for geometry_columns.
            await using var command = new NpgsqlCommand($"""
                ALTER TABLE {qualified} ADD CONSTRAINT {ImportedGeometryTypeConstraint}
                CHECK (GeometryType(geometry) = '{type}' OR GeometryType(geometry) = '{type}M')
                """, connection, transaction);
            command.CommandTimeout = 0;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitSafelyAsync(cancellationToken);
        }
    }
}
