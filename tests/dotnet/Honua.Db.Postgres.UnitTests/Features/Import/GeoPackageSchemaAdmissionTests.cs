// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Honua.Db.Postgres.Features.FileImport;
using Honua.TestKit.Infrastructure;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Features;

namespace Honua.Db.Postgres.Tests.Features.Import;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class GeoPackageSchemaAdmissionTests
{
    [Theory]
    [InlineData("sample_layer")]
    [InlineData("gpkg_contents")]
    [InlineData("gpkg_geometry_columns")]
    [InlineData("gpkg_spatial_ref_sys")]
    public Task PreviewGeoPackage_ViewBackedSchema_RejectsImport(string tableName) =>
        AssertViewRejectedAsync(tableName, PreviewAsync);

    [Theory]
    [InlineData("sample_layer")]
    [InlineData("gpkg_contents")]
    [InlineData("gpkg_geometry_columns")]
    [InlineData("gpkg_spatial_ref_sys")]
    public Task StreamGeoPackage_ViewBackedSchema_RejectsImport(string tableName) =>
        AssertViewRejectedAsync(tableName, async path => { await ReadAllFeaturesAsync(path); });

    private static async Task PreviewAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        await PreviewImportServiceFactory.Create().PreviewFileAsync(stream, "sample.gpkg");
    }

    private static async Task AssertViewRejectedAsync(string tableName, Func<string, Task> read)
    {
        var filePath = Path.Join(Path.GetTempPath(), $"honua-gpkg-{Guid.NewGuid():N}.gpkg");
        try
        {
            GeoPackageTestFiles.Create(filePath);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = filePath,
                Pooling = false
            }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    ALTER TABLE "{tableName}" RENAME TO original_table;
                    CREATE VIEW "{tableName}" AS SELECT * FROM original_table;
                    CREATE TABLE pragma_table_list (schema TEXT, name TEXT, type TEXT);
                    INSERT INTO pragma_table_list VALUES ('main', '{tableName}', 'table');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            Func<Task> action = () => read(filePath);
            await action.Should().ThrowAsync<InvalidDataException>();
        }
        finally
        {
            await GeoPackageTestFiles.DeleteAsync(filePath);
        }
    }

    [Fact]
    public async Task ReadGeoPackage_OrdinaryTables_PreservesFeatureData()
    {
        var filePath = Path.Join(Path.GetTempPath(), $"honua-gpkg-{Guid.NewGuid():N}.gpkg");
        try
        {
            GeoPackageTestFiles.Create(filePath);
            var features = await ReadAllFeaturesAsync(filePath);

            features.Should().ContainSingle();
            features[0].Attributes["name"].Should().Be("Test Feature");
            features[0].Geometry.SRID.Should().Be(4326);
        }
        finally
        {
            await GeoPackageTestFiles.DeleteAsync(filePath);
        }
    }

    [Theory]
    [InlineData("VIRTUAL")]
    [InlineData("STORED")]
    public async Task StreamGeoPackage_GeneratedColumnInFeatureTable_RejectsImport(string storageKind)
    {
        var filePath = Path.Join(Path.GetTempPath(), $"honua-gpkg-gen-{Guid.NewGuid():N}.gpkg");
        try
        {
            GeoPackageTestFiles.Create(filePath);
            await AddGeneratedColumnAsync(filePath, storageKind);

            Func<Task> action = async () => { await ReadAllFeaturesAsync(filePath); };
            await action.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*generated columns*");
        }
        finally
        {
            await GeoPackageTestFiles.DeleteAsync(filePath);
        }
    }

    private static async Task AddGeneratedColumnAsync(string filePath, string storageKind)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = storageKind == "VIRTUAL"
            ? "ALTER TABLE sample_layer ADD COLUMN bomb BLOB GENERATED ALWAYS AS (zeroblob(500000000)) VIRTUAL;"
            : """
                DROP TABLE sample_layer;
                CREATE TABLE sample_layer (
                    fid INTEGER PRIMARY KEY,
                    geom BLOB,
                    name TEXT,
                    bomb TEXT GENERATED ALWAYS AS ('generated') STORED
                );
                """;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task StreamGeoPackage_ExcessiveMetadataRows_RejectsImport()
    {
        var filePath = Path.Join(Path.GetTempPath(), $"honua-gpkg-meta-{Guid.NewGuid():N}.gpkg");
        try
        {
            GeoPackageTestFiles.Create(filePath);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = filePath,
                Pooling = false
            }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    DROP TABLE gpkg_contents;
                    CREATE TABLE gpkg_contents (table_name TEXT, data_type TEXT);
                    WITH RECURSIVE cnt(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM cnt WHERE x < 1005)
                    INSERT INTO gpkg_contents SELECT 'sample_layer', 'features' FROM cnt;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            Func<Task> action = async () => { await ReadAllFeaturesAsync(filePath); };
            await action.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*exceeds the maximum*");
        }
        finally
        {
            await GeoPackageTestFiles.DeleteAsync(filePath);
        }
    }

    [Fact]
    public async Task PreviewKmz_SpoofedZeroEntryLengthZipBomb_RejectsWithInvalidDataException()
    {
        var bombPayload = new byte[2 * 1024 * 1024];
        var zipBytes = CreateSpoofedLengthZip("doc.kml", bombPayload, spoofedLength: 0);

        await using var stream = new MemoryStream(zipBytes);
        Func<Task> action = () => PreviewImportServiceFactory.Create().PreviewFileAsync(stream, "bomb.kmz");

        await action.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task CopyEntryBounded_ActualBytesExceedCompressionRatio_ThrowsInvalidDataException()
    {
        var destination = Path.Join(Path.GetTempPath(), $"honua-ratio-{Guid.NewGuid():N}.kml");
        try
        {
            await using var decompressed = new MemoryStream(new byte[32 * 1024]);
            var request = new StreamingFileImportService.ScratchEntryCopyRequest
            {
                EntryStream = decompressed,
                EntryName = "doc.kml",
                CompressedLength = 100L,
                DestinationPath = destination,
                Budget = new StreamingFileImportService.ArchiveExtractionBudget(
                    10L * 1024 * 1024,
                    10L * 1024 * 1024,
                    200d)
            };

            Func<Task> action = () => StreamingFileImportService.CopyEntryBoundedAsync(request, CancellationToken.None);

            await action.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*compression ratio*");
        }
        finally
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }

    private static byte[] CreateSpoofedLengthZip(string entryName, byte[] payload, uint spoofedLength)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
            using var entryStream = entry.Open();
            entryStream.Write(payload, 0, payload.Length);
        }

        var bytes = memory.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(22, 4), spoofedLength);
        for (var i = 0; i <= bytes.Length - 46; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24, 4), spoofedLength);
            }
        }

        return bytes;
    }

    private static async Task<List<IFeature>> ReadAllFeaturesAsync(string filePath)
    {
        var features = new List<IFeature>();
        await foreach (var feature in ReadFeatures(filePath))
        {
            features.Add(feature);
        }

        return features;
    }

    private static IAsyncEnumerable<IFeature> ReadFeatures(string filePath)
    {
        var method = typeof(StreamingFileImportService).GetMethod("ReadGeoPackageStreamingAsync",
            BindingFlags.NonPublic | BindingFlags.Static, null,
            [typeof(string), typeof(string), typeof(CancellationToken)], null)!;
        return (IAsyncEnumerable<IFeature>)method.Invoke(null, [filePath, null, CancellationToken.None])!;
    }
}
