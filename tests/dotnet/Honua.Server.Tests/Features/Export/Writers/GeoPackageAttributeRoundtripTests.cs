// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Reflection;
using Honua.Db.Postgres.Features.FileImport;
using Honua.Io.Export;
using Honua.Io.Export.Writers;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using StoredFeature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Server.Tests.Features.Export.Writers;

public sealed class GeoPackageAttributeRoundtripTests
{
    [Theory]
    [InlineData("fid")]
    [InlineData("FID")]
    [InlineData("geom")]
    [InlineData("Geom")]
    public async Task ImportExport_ReservedAttributeNames_PreserveNamesTypesAndValues(string name)
    {
        var fields = new[] { new ExportField(name, ExportFieldType.String, true),
            new ExportField("fid_1", ExportFieldType.String, true), new ExportField("geom_1", ExportFieldType.String, true) };
        await RoundtripAsync(fields, ["source value", "retained fid suffix", "retained geom suffix"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(9)]
    public async Task ImportExport_BooleanDateAndTimestamp_PreserveDeclaredTypesAndValues(int offsetHours)
    {
        await RoundtripAsync([
            new("active", ExportFieldType.Boolean, true), new("observed", ExportFieldType.Date, true),
            new("created", ExportFieldType.DateTime, true), new("sequence", ExportFieldType.BigInteger, true),
            new("ratio", ExportFieldType.Double, true), new("title", ExportFieldType.String, true)],
            [true, new DateOnly(2026, 9, 4), new DateTimeOffset(2026, 9, 4, 1, 2, 3, TimeSpan.FromHours(offsetHours)).AddTicks(1234567),
                9007199254740993L, 1.23456789012345, "Hawaiʻi"]);
    }

    [Theory]
    [InlineData(false, "not-a-date")]
    [InlineData(false, "2026-02-30")]
    [InlineData(true, "not-a-timestamp")]
    [InlineData(true, "2026-13-01T00:00:00Z")]
    public async Task Import_MalformedTemporalCell_ReportsInvalidData(bool timestamp, string value)
    {
        var path = Path.Join(Path.GetTempPath(), $"honua-invalid-temporal-{Guid.NewGuid():N}.gpkg");
        try
        {
            var type = timestamp ? ExportFieldType.DateTime : ExportFieldType.Date;
            var field = new ExportField("value", type, true);
            var source = new NetTopologySuite.Features.Feature(new WKTReader().Read("POINT (1 2)"),
                new AttributesTable { { "value", type == ExportFieldType.Date ? (object)new DateOnly(2026, 9, 4) : DateTimeOffset.UnixEpoch } });
            await GeoPackageExportWriter.WriteAsync(path, Rows([source], [field]), [field],
                ExportGeometryType.Point, 4326, "EPSG:4326", null, CancellationToken.None);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var update = connection.CreateCommand();
                update.CommandText = "UPDATE features SET value = $value";
                update.Parameters.AddWithValue("$value", value);
                await update.ExecuteNonQueryAsync();
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static async Task RoundtripAsync(ExportField[] fields, object[] values)
    {
        var scratch = Path.Join(Path.GetTempPath(), $"honua-gpkg-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        var input = Path.Join(scratch, "input.gpkg");
        var output = Path.Join(scratch, "output.gpkg");
        try
        {
            var geometry = new WKTReader().Read("POINT ZM (-157.1234567890123 21.1234567890123 30.125 40.25)");
            var wkb = new WKBWriter(ByteOrder.LittleEndian, false, true, true).Write(geometry);
            var blob = new byte[8 + wkb.Length];
            blob[0] = (byte)'G'; blob[1] = (byte)'P'; blob[3] = 1;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(4), 4326);
            wkb.CopyTo(blob, 8);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = input, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                var columns = string.Join(", ", fields.Select(f => $"{Quote(f.Name)} {SqlType(f.Type)}"));
                command.CommandText = $"""
                    CREATE TABLE gpkg_contents (table_name TEXT, data_type TEXT);
                    INSERT INTO gpkg_contents VALUES ('source', 'features');
                    CREATE TABLE gpkg_geometry_columns (table_name TEXT, column_name TEXT, srs_id INTEGER);
                    INSERT INTO gpkg_geometry_columns VALUES ('source', 'shape', 4326);
                    CREATE TABLE gpkg_spatial_ref_sys (srs_id INTEGER, organization TEXT, organization_coordsys_id INTEGER);
                    INSERT INTO gpkg_spatial_ref_sys VALUES (4326, 'EPSG', 4326);
                    CREATE TABLE source (source_pk INTEGER PRIMARY KEY, shape BLOB, {columns});
                    """;
                await command.ExecuteNonQueryAsync();
                command.CommandText = $"INSERT INTO source (shape, {string.Join(", ", fields.Select(f => Quote(f.Name)))}) VALUES (@geometry, {string.Join(", ", fields.Select((_, i) => $"@p{i}"))})";
                command.Parameters.AddWithValue("@geometry", blob);
                for (var i = 0; i < values.Length; i++)
                {
                    command.Parameters.AddWithValue($"@p{i}", values[i] switch
                    {
                        DateOnly date => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                        DateTimeOffset instant => instant.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                        _ => values[i]
                    });
                }

                await command.ExecuteNonQueryAsync();
                command.CommandText = "INSERT INTO source DEFAULT VALUES";
                command.Parameters.Clear();
                await command.ExecuteNonQueryAsync();
            }

            var imported = await ReadAsync(input);
            Assert.Equal(2, imported.Count);
            AssertAttributes(imported[0], fields, values);
            Assert.Equal(2, await GeoPackageExportWriter.WriteAsync(output, Rows(imported, fields), fields,
                ExportGeometryType.Point, 4326, "EPSG:4326", null, CancellationToken.None));
            var roundtrip = await ReadAsync(output);
            Assert.Equal(2, roundtrip.Count);
            AssertAttributes(roundtrip[0], fields, values);
            Assert.True(geometry.EqualsExact(roundtrip[0].Geometry));
            Assert.Equal(30.125, roundtrip[0].Geometry.Coordinate.Z);
            Assert.Equal(40.25, roundtrip[0].Geometry.Coordinate.M);
            Assert.Null(roundtrip[1].Geometry);
            Assert.All(fields, field => Assert.Null(roundtrip[1].Attributes[field.Name]));
            await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = output, Pooling = false }.ToString());
            await verify.OpenAsync();
            await using var schema = verify.CreateCommand();
            schema.CommandText = "PRAGMA table_info(features)";
            await using var reader = await schema.ExecuteReaderAsync();
            var declarations = new Dictionary<string, string>();
            while (await reader.ReadAsync())
            {
                declarations.Add(reader.GetString(1), reader.GetString(2));
            }

            foreach (var field in fields)
            {
                Assert.Equal(SqlType(field.Type), declarations[field.Name]);
            }

            await reader.CloseAsync();
            for (var i = 0; i < fields.Length; i++)
            {
                if (fields[i].Type != ExportFieldType.DateTime)
                {
                    continue;
                }

                await using var timestamp = verify.CreateCommand();
                timestamp.CommandText = $"SELECT {Quote(fields[i].Name)} FROM features WHERE {Quote(fields[i].Name)} IS NOT NULL";
                var stored = Assert.IsType<string>(await timestamp.ExecuteScalarAsync());
                Assert.Equal(((DateTimeOffset)values[i]).UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture), stored);
                Assert.EndsWith("Z", stored, StringComparison.Ordinal);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static void AssertAttributes(IFeature feature, ExportField[] fields, object[] values)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            var actual = feature.Attributes[fields[i].Name];
            Assert.IsType(values[i].GetType(), actual);
            Assert.Equal(values[i], actual);
        }
    }

    private static async Task<List<IFeature>> ReadAsync(string path)
    {
        var method = typeof(StreamingFileImportService).GetMethod("ReadGeoPackageStreamingAsync",
            BindingFlags.NonPublic | BindingFlags.Static, null, [typeof(string), typeof(CancellationToken)], null)!;
        var stream = (IAsyncEnumerable<IFeature>)method.Invoke(null, [path, CancellationToken.None])!;
        var features = new List<IFeature>();
        await foreach (var feature in stream)
        {
            features.Add(feature);
        }

        return features;
    }

    private static async IAsyncEnumerable<StoredFeature> Rows(IEnumerable<IFeature> source, ExportField[] fields)
    {
        long id = 0;
        foreach (var feature in source)
        {
            yield return StoredFeature.Create(++id,
                feature.Geometry is null ? null : new WKBWriter(ByteOrder.LittleEndian, false, true, true).Write(feature.Geometry),
                fields.ToImmutableDictionary(field => field.Name, field => (object?)feature.Attributes[field.Name]));
        }

        await Task.CompletedTask;
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string SqlType(ExportFieldType type) => type switch
    {
        ExportFieldType.Boolean => "BOOLEAN",
        ExportFieldType.Date => "DATE",
        ExportFieldType.DateTime => "DATETIME",
        ExportFieldType.BigInteger => "INTEGER",
        ExportFieldType.Double => "REAL",
        _ => "TEXT"
    };
}
