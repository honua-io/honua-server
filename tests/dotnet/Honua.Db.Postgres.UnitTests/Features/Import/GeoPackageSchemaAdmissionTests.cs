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
    [InlineData("sample_layer", true)]
    [InlineData("gpkg_contents", true)]
    [InlineData("gpkg_geometry_columns", true)]
    [InlineData("gpkg_spatial_ref_sys", true)]
    [InlineData("sample_layer", false)]
    [InlineData("gpkg_contents", false)]
    [InlineData("gpkg_geometry_columns", false)]
    [InlineData("gpkg_spatial_ref_sys", false)]
    public async Task ReadGeoPackage_ViewBackedSchema_RejectsImport(string tableName, bool preview)
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
                command.CommandText = $"ALTER TABLE \"{tableName}\" RENAME TO original_table; CREATE VIEW \"{tableName}\" AS SELECT * FROM original_table;";
                await command.ExecuteNonQueryAsync();
            }

            await using var stream = File.OpenRead(filePath);
            Func<Task> action = async () =>
            {
                if (preview)
                {
                    await PreviewImportServiceFactory.Create().PreviewFileAsync(stream, "sample.gpkg");
                    return;
                }

                await ReadAllFeaturesAsync(filePath);
            };

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
