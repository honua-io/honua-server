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

    // A missing constraint identifies a legacy or mixed import. Never infer a new
    // storage contract for such an existing table: published layers may depend on it.
    private static async Task<string?> ReadImportedGeometryTypeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string table,
        CancellationToken cancellationToken)
    {
        if (!await TargetIsImporterOwnedAsync(connection, schema, table, cancellationToken, transaction))
        {
            return null;
        }

        await using var command = new NpgsqlCommand("""
            SELECT gc.type FROM geometry_columns gc
            WHERE gc.f_table_schema = @schema AND gc.f_table_name = @table AND gc.f_geometry_column = 'geometry'
              AND EXISTS (SELECT 1 FROM pg_constraint c
                  WHERE c.conrelid = to_regclass(format('%I.%I', @schema, @table))
                    AND c.conname = 'honua_import_geometry_type')
            """, connection, transaction);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);
        var type = (string?)await command.ExecuteScalarAsync(cancellationToken);
        return type is "POINT" or "MULTIPOINT" or "LINESTRING" or "MULTILINESTRING" or "POLYGON" or "MULTIPOLYGON" ? type : null;
    }

    // Infer from final stored geometries, after repair/transformation and row rejection.
    // A check constraint is visible through PostGIS geometry_columns without forcing
    // all source coordinates to the same XY/XYZ/XYM/XYZM typmod. The existing SRID
    // constraint remains authoritative for the transformed storage CRS.
    private static async Task RefreshImportedGeometryTypeAsync(
        NpgsqlConnection connection, string schema, string table, CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null, string? preserveType = null)
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
        var hasMultiPoint = false;
        await using (var command = new NpgsqlCommand($"""
            SELECT CASE ST_GeometryType(geometry)
                WHEN 'ST_Point' THEN 'POINT' WHEN 'ST_MultiPoint' THEN 'POINT'
                WHEN 'ST_LineString' THEN 'LINESTRING' WHEN 'ST_MultiLineString' THEN 'LINESTRING'
                WHEN 'ST_Polygon' THEN 'POLYGON' WHEN 'ST_MultiPolygon' THEN 'POLYGON'
                ELSE 'OTHER' END AS family,
                bool_or(ST_GeometryType(geometry) = 'ST_MultiPoint')
            FROM {qualified}
            WHERE geometry IS NOT NULL
            GROUP BY family
            LIMIT 2
            """, connection, transaction))
        {
            command.CommandTimeout = 0;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                geometryType = reader.GetString(0);
                hasMultiPoint = reader.GetBoolean(1);
                if (await reader.ReadAsync(cancellationToken))
                {
                    geometryType = null;
                }
            }
        }

        // Preserve Point's Esri representation for all-single-point inputs. Its
        // family constraint accepts MultiPoint edits without rejecting valid writes.
        // Existing typed tables retain their original catalog/storage contract.
        var type = preserveType ?? geometryType switch
        {
            "POINT" => hasMultiPoint ? "MULTIPOINT" : "POINT",
            "LINESTRING" => "MULTILINESTRING",
            "POLYGON" => "MULTIPOLYGON",
            _ => null
        };
        var singleType = type switch
        {
            "MULTIPOINT" => "ST_Point",
            "MULTILINESTRING" => "ST_LineString",
            "MULTIPOLYGON" => "ST_Polygon",
            _ => null
        };
        if (type is not null)
        {
            if (singleType is not null)
            {
                await using var promote = new NpgsqlCommand($"""
                    UPDATE {qualified} SET geometry = ST_Multi(geometry)
                    WHERE ST_GeometryType(geometry) = '{singleType}'
                    """, connection, transaction);
                promote.CommandTimeout = 0;
                await promote.ExecuteNonQueryAsync(cancellationToken);
            }

            // PostGIS reads the first type literal for geometry_columns. XYM has
            // an M suffix; Point layers accept either point form on future edits.
            var additionalFamilyTypes = type switch
            {
                "POINT" => " OR GeometryType(geometry) = 'MULTIPOINT' OR GeometryType(geometry) = 'MULTIPOINTM'",
                "LINESTRING" => " OR GeometryType(geometry) = 'MULTILINESTRING' OR GeometryType(geometry) = 'MULTILINESTRINGM'",
                "POLYGON" => " OR GeometryType(geometry) = 'MULTIPOLYGON' OR GeometryType(geometry) = 'MULTIPOLYGONM'",
                _ => string.Empty
            };
            await using var command = new NpgsqlCommand($"""
                ALTER TABLE {qualified} ADD CONSTRAINT {ImportedGeometryTypeConstraint}
                CHECK (GeometryType(geometry) = '{type}' OR GeometryType(geometry) = '{type}M'{additionalFamilyTypes})
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
