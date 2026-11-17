// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
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
