// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Export.Writers;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using WkbWriter = NetTopologySuite.IO.WKBWriter;

namespace Honua.Server.Tests.Features.Export.Writers;

public sealed class GeoPackageExportWriterTests
{
    [Fact]
    public async Task WriteAsync_WithZmGeometry_RegistersMandatoryZAndMMetadata()
    {
        var tempPath = CreateTempGeoPackagePath();

        try
        {
            await GeoPackageExportWriter.WriteAsync(
                tempPath,
                AsAsyncEnumerable(CreateFeature(1, CreatePointWkbWithZm(10, 20, 30, 40))),
                [],
                Honua.Core.Features.Catalog.Domain.GeometryType.Point,
                4326,
                "EPSG:4326",
                null,
                CancellationToken.None);

            var dimensions = await ReadGeometryDimensionsAsync(tempPath);
            dimensions.Z.Should().Be(1);
            dimensions.M.Should().Be(1);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task WriteAsync_WithMixedZGeometry_RegistersOptionalZMetadata()
    {
        var tempPath = CreateTempGeoPackagePath();

        try
        {
            await GeoPackageExportWriter.WriteAsync(
                tempPath,
                AsAsyncEnumerable(
                    CreateFeature(1, CreatePointWkb(10, 20)),
                    CreateFeature(2, CreatePointWkbWithZ(30, 40, 50))),
                [],
                Honua.Core.Features.Catalog.Domain.GeometryType.Point,
                4326,
                "EPSG:4326",
                null,
                CancellationToken.None);

            var dimensions = await ReadGeometryDimensionsAsync(tempPath);
            dimensions.Z.Should().Be(2);
            dimensions.M.Should().Be(0);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static string CreateTempGeoPackagePath()
        => Path.Combine(Path.GetTempPath(), $"gpkg-export-{Guid.NewGuid():N}.gpkg");

    private static Feature CreateFeature(long id, byte[] geometry)
        => Feature.Create(id, geometry, ImmutableDictionary<string, object?>.Empty);

    private static async IAsyncEnumerable<Feature> AsAsyncEnumerable(params Feature[] features)
    {
        foreach (var feature in features)
        {
            yield return feature;
            await Task.Yield();
        }
    }

    private static async Task<(long Z, long M)> ReadGeometryDimensionsAsync(string tempPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = tempPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT z, m FROM gpkg_geometry_columns WHERE table_name = 'features' AND column_name = 'geom'";

        await using var reader = await cmd.ExecuteReaderAsync();
        var hasRow = await reader.ReadAsync();
        hasRow.Should().BeTrue();

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static byte[] CreatePointWkb(double x, double y)
        => new WkbWriter(NetTopologySuite.IO.ByteOrder.LittleEndian, handleSRID: false, emitZ: false, emitM: false)
            .Write(new Point(x, y) { SRID = 4326 });

    private static byte[] CreatePointWkbWithZ(double x, double y, double z)
        => new WkbWriter(NetTopologySuite.IO.ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: false)
            .Write(new Point(new CoordinateZ(x, y, z)) { SRID = 4326 });

    private static byte[] CreatePointWkbWithZm(double x, double y, double z, double m)
        => new WkbWriter(NetTopologySuite.IO.ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: true)
            .Write(new Point(new CoordinateZM(x, y, z, m)) { SRID = 4326 });
}
