// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using InfrastructureGeometryService = Honua.Server.Features.Infrastructure.Services.GeometryService;
using WkbReader = NetTopologySuite.IO.WKBReader;

namespace Honua.Server.Features.Export.Writers;

/// <summary>
/// Writes features as an OGC GeoPackage (.gpkg) file.
/// Creates spec-compliant metadata tables and inserts features in batched transactions.
/// </summary>
internal static class GeoPackageExportWriter
{
    private const int BatchSize = 1000;
    private const string TableName = "features";

    public static async Task<int> WriteAsync(
        string gpkgPath,
        IAsyncEnumerable<Feature> features,
        FieldDefinition[] fields,
        GeometryType geometryType,
        int srid,
        string? srsName,
        string? srsWkt,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = gpkgPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Set GeoPackage application ID (magic bytes)
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA application_id = 0x47504B47;"; // 'GPKG'
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version = 10200;"; // GeoPackage 1.2.0 (OGC 12-128r16)
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await CreateMetadataTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        await InsertSpatialRefAsync(connection, srid, srsName, srsWkt, cancellationToken).ConfigureAwait(false);
        await CreateFeatureTableAsync(connection, fields, cancellationToken).ConfigureAwait(false);
        await RegisterContentsAsync(connection, srid, cancellationToken).ConfigureAwait(false);

        var insertSummary = await InsertFeaturesAsync(connection, features, fields, srid, cancellationToken)
            .ConfigureAwait(false);
        await RegisterGeometryColumnAsync(connection, geometryType, srid, insertSummary.Dimensions, cancellationToken).ConfigureAwait(false);

        return insertSummary.TotalInserted;
    }

    private static async Task CreateMetadataTablesAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS gpkg_spatial_ref_sys (
                srs_name TEXT NOT NULL,
                srs_id INTEGER NOT NULL PRIMARY KEY,
                organization TEXT NOT NULL,
                organization_coordsys_id INTEGER NOT NULL,
                definition TEXT NOT NULL,
                description TEXT
            );

            CREATE TABLE IF NOT EXISTS gpkg_contents (
                table_name TEXT NOT NULL PRIMARY KEY,
                data_type TEXT NOT NULL DEFAULT 'features',
                identifier TEXT UNIQUE,
                description TEXT DEFAULT '',
                last_change DATETIME NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                min_x DOUBLE,
                min_y DOUBLE,
                max_x DOUBLE,
                max_y DOUBLE,
                srs_id INTEGER,
                CONSTRAINT fk_gc_r_srs_id FOREIGN KEY (srs_id) REFERENCES gpkg_spatial_ref_sys(srs_id)
            );

            CREATE TABLE IF NOT EXISTS gpkg_geometry_columns (
                table_name TEXT NOT NULL,
                column_name TEXT NOT NULL,
                geometry_type_name TEXT NOT NULL,
                srs_id INTEGER NOT NULL,
                z TINYINT NOT NULL,
                m TINYINT NOT NULL,
                CONSTRAINT pk_geom_cols PRIMARY KEY (table_name, column_name),
                CONSTRAINT fk_gc_tn FOREIGN KEY (table_name) REFERENCES gpkg_contents(table_name),
                CONSTRAINT fk_gc_srs FOREIGN KEY (srs_id) REFERENCES gpkg_spatial_ref_sys(srs_id)
            );

            INSERT OR IGNORE INTO gpkg_spatial_ref_sys (srs_name, srs_id, organization, organization_coordsys_id, definition)
            VALUES ('Undefined cartesian SRS', -1, 'NONE', -1, 'undefined');

            INSERT OR IGNORE INTO gpkg_spatial_ref_sys (srs_name, srs_id, organization, organization_coordsys_id, definition)
            VALUES ('Undefined geographic SRS', 0, 'NONE', 0, 'undefined');
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertSpatialRefAsync(
        SqliteConnection connection, int srid, string? srsName, string? srsWkt, CancellationToken ct)
    {
        var (organization, coordsysId) = ResolveSrsAuthority(srid, srsName);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO gpkg_spatial_ref_sys (srs_name, srs_id, organization, organization_coordsys_id, definition)
            VALUES ($name, $srid, $org, $orgCoordsysId, $definition);
            """;
        cmd.Parameters.AddWithValue("$name", srsName ?? $"EPSG:{srid}");
        cmd.Parameters.AddWithValue("$srid", srid);
        cmd.Parameters.AddWithValue("$org", organization);
        cmd.Parameters.AddWithValue("$orgCoordsysId", coordsysId);
        cmd.Parameters.AddWithValue("$definition", srsWkt ?? "undefined");
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // Parses "AUTHORITY:CODE" forms (e.g. "EPSG:4326", "ESRI:102100") to populate
    // gpkg_spatial_ref_sys.organization and organization_coordsys_id per OGC 12-128r16 §1.1.2.
    // Falls back to EPSG with srs_id when the name is absent or unparseable.
    private static (string Organization, int CoordsysId) ResolveSrsAuthority(int srid, string? srsName)
    {
        if (!string.IsNullOrWhiteSpace(srsName))
        {
            var colonIndex = srsName.IndexOf(':');
            if (colonIndex > 0 && colonIndex < srsName.Length - 1)
            {
                var authority = srsName[..colonIndex].Trim();
                var codeText = srsName[(colonIndex + 1)..].Trim();
                if (authority.Length > 0
                    && int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                {
                    return (authority.ToUpperInvariant(), code);
                }
            }
        }

        return ("EPSG", srid);
    }

    private static async Task CreateFeatureTableAsync(
        SqliteConnection connection, FieldDefinition[] fields, CancellationToken ct)
    {
        var columns = new List<string> { "fid INTEGER PRIMARY KEY AUTOINCREMENT", "geom BLOB" };

        foreach (var field in fields)
        {
            var sqliteType = MapSqliteType(field.Type);
            var nullable = field.Nullable ? "" : " NOT NULL";
            columns.Add($"\"{field.Name}\" {sqliteType}{nullable}");
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE TABLE \"{TableName}\" ({string.Join(", ", columns)});";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task RegisterContentsAsync(SqliteConnection connection, int srid, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO gpkg_contents (table_name, data_type, identifier, srs_id)
            VALUES ($table, 'features', $table, $srid);
            """;
        cmd.Parameters.AddWithValue("$table", TableName);
        cmd.Parameters.AddWithValue("$srid", srid);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task RegisterGeometryColumnAsync(
        SqliteConnection connection, GeometryType geometryType, int srid, GeometryDimensionMetadata dimensions, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO gpkg_geometry_columns (table_name, column_name, geometry_type_name, srs_id, z, m)
            VALUES ($table, 'geom', $geomType, $srid, $z, $m);
            """;
        cmd.Parameters.AddWithValue("$table", TableName);
        cmd.Parameters.AddWithValue("$geomType", MapGpkgGeometryType(geometryType));
        cmd.Parameters.AddWithValue("$srid", srid);
        cmd.Parameters.AddWithValue("$z", dimensions.Z);
        cmd.Parameters.AddWithValue("$m", dimensions.M);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<FeatureInsertSummary> InsertFeaturesAsync(
        SqliteConnection connection,
        IAsyncEnumerable<Feature> features,
        FieldDefinition[] fields,
        int srid,
        CancellationToken ct)
    {
        var totalInserted = 0;
        var batchCount = 0;
        var anyGeometry = false;
        var anyGeometryHasZ = false;
        var anyGeometryHasM = false;
        var anyGeometryWithoutZ = false;
        var anyGeometryWithoutM = false;
        var wkbReader = new WkbReader();

        // Build parameterized INSERT statement
        var fieldNames = new List<string> { "geom" };
        var paramNames = new List<string> { "$geom" };
        for (var i = 0; i < fields.Length; i++)
        {
            fieldNames.Add($"\"{fields[i].Name}\"");
            paramNames.Add($"$f{i}");
        }

        var insertSql = $"INSERT INTO \"{TableName}\" ({string.Join(", ", fieldNames)}) VALUES ({string.Join(", ", paramNames)});";

        SqliteTransaction? transaction = null;

        // Create command once and reuse across all rows
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = insertSql;
        var geomParam = cmd.Parameters.Add("$geom", SqliteType.Blob);
        var fieldParams = new SqliteParameter[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            fieldParams[i] = cmd.Parameters.Add($"$f{i}", MapSqliteParamType(fields[i].Type));
        }

        try
        {
            transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            cmd.Transaction = transaction;

            await foreach (var feature in features.WithCancellation(ct).ConfigureAwait(false))
            {
                // Geometry as GeoPackage binary
                geomParam.Value = feature.Geometry is { Length: > 0 }
                    ? ToGpkgBinary(feature.Geometry, srid)
                    : DBNull.Value;

                if (feature.Geometry is { Length: > 0 } geometryWkb &&
                    TryDetectGeometryDimensions(wkbReader, geometryWkb, out var hasZ, out var hasM))
                {
                    anyGeometry = true;
                    anyGeometryHasZ |= hasZ;
                    anyGeometryHasM |= hasM;
                    anyGeometryWithoutZ |= !hasZ;
                    anyGeometryWithoutM |= !hasM;
                }

                for (var i = 0; i < fields.Length; i++)
                {
                    feature.Attributes.TryGetValue(fields[i].Name, out var value);
                    fieldParams[i].Value = NormalizeSqliteValue(value, fields[i].Type);
                }

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                totalInserted++;
                batchCount++;

                if (batchCount >= BatchSize)
                {
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    transaction.Dispose();
                    transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                    cmd.Transaction = transaction;
                    batchCount = 0;
                }
            }

            if (batchCount > 0)
            {
                // Surface cancellation *before* attempting the final commit so we do
                // not persist a partial batch whose in-flight features may have been
                // truncated by the async source.
                ct.ThrowIfCancellationRequested();
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            transaction?.Dispose();
        }

        return new FeatureInsertSummary(
            totalInserted,
            new GeometryDimensionMetadata(
                DetermineDimensionRequirement(anyGeometry, anyGeometryHasZ, anyGeometryWithoutZ),
                DetermineDimensionRequirement(anyGeometry, anyGeometryHasM, anyGeometryWithoutM)));
    }

    private static SqliteType MapSqliteParamType(FieldType fieldType) => fieldType switch
    {
        FieldType.Integer or FieldType.BigInteger or FieldType.Boolean => SqliteType.Integer,
        FieldType.Double or FieldType.Float => SqliteType.Real,
        FieldType.Binary => SqliteType.Blob,
        _ => SqliteType.Text
    };

    /// <summary>
    /// Encodes WKB geometry as GeoPackage standard binary (header + WKB payload).
    /// See OGC 12-128r16 §6.2.3.
    /// </summary>
    private static byte[] ToGpkgBinary(byte[] wkb, int srid)
    {
        // GeoPackage binary header: GP (2 bytes) + version (1) + flags (1) + srs_id (4) + optional envelope + WKB
        // Minimal header with no envelope: 8 bytes
        var result = new byte[8 + wkb.Length];
        result[0] = 0x47; // 'G'
        result[1] = 0x50; // 'P'
        result[2] = 0;    // version
        result[3] = 0x01; // flags: little-endian, no envelope

        // SRS ID as little-endian int32
        result[4] = (byte)(srid & 0xFF);
        result[5] = (byte)((srid >> 8) & 0xFF);
        result[6] = (byte)((srid >> 16) & 0xFF);
        result[7] = (byte)((srid >> 24) & 0xFF);

        Buffer.BlockCopy(wkb, 0, result, 8, wkb.Length);
        return result;
    }

    private static string MapGpkgGeometryType(GeometryType geometryType) => geometryType switch
    {
        GeometryType.Point => "POINT",
        GeometryType.MultiPoint => "MULTIPOINT",
        GeometryType.LineString => "LINESTRING",
        GeometryType.MultiLineString => "MULTILINESTRING",
        GeometryType.Polygon => "POLYGON",
        GeometryType.MultiPolygon => "MULTIPOLYGON",
        GeometryType.GeometryCollection => "GEOMETRY",
        GeometryType.None => "GEOMETRY",
        _ => "GEOMETRY"
    };

    private static string MapSqliteType(FieldType fieldType) => fieldType switch
    {
        FieldType.Integer => "INTEGER",
        FieldType.BigInteger => "INTEGER",
        FieldType.Double or FieldType.Float => "REAL",
        FieldType.Boolean => "INTEGER",
        FieldType.DateTime or FieldType.Date => "TEXT",
        FieldType.Time => "TEXT",
        FieldType.Binary => "BLOB",
        _ => "TEXT"
    };

    private static object NormalizeSqliteValue(object? value, FieldType fieldType)
    {
        if (value is null or DBNull)
            return DBNull.Value;

        return fieldType switch
        {
            FieldType.Boolean when value is bool b => b ? 1 : 0,
            FieldType.DateTime when value is DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            FieldType.Date when value is DateTimeOffset dto => dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FieldType.Time when value is TimeSpan ts => ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
            _ => value
        };
    }

    private static bool TryDetectGeometryDimensions(WkbReader reader, byte[] wkb, out bool hasZ, out bool hasM)
    {
        try
        {
            var geometry = reader.Read(wkb);
            (hasZ, hasM) = InfrastructureGeometryService.DetectZMFromGeometry(geometry);
            return true;
        }
        catch
        {
            hasZ = false;
            hasM = false;
            return false;
        }
    }

    private static byte DetermineDimensionRequirement(bool anyGeometry, bool anyHasDimension, bool anyWithoutDimension)
    {
        if (!anyGeometry || !anyHasDimension)
        {
            return 0;
        }

        return anyWithoutDimension ? (byte)2 : (byte)1;
    }

    private readonly record struct FeatureInsertSummary(int TotalInserted, GeometryDimensionMetadata Dimensions);

    private readonly record struct GeometryDimensionMetadata(byte Z, byte M);
}
