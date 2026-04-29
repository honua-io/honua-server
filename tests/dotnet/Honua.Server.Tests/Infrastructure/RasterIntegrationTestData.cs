// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;

namespace Honua.Server.Tests.Infrastructure;

internal sealed record RasterSeed(
    string Name,
    int Width,
    int Height,
    double UpperLeftX,
    double UpperLeftY,
    double ScaleX,
    double ScaleY,
    double Value,
    DateTimeOffset AcquisitionDate,
    DateTimeOffset CreatedAt,
    int Srid = 4326,
    string? Description = null);

internal static class RasterIntegrationTestData
{
    internal static readonly DateTimeOffset WestAcquisition = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset EastAcquisition = new(2024, 1, 15, 0, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset OverlapAcquisition = new(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);

    public static Task SeedIssue522MosaicAsync(WebAppFixture fixture, int layerId = WebAppFixture.TestLayerId)
    {
        return ReplaceLayerRastersAsync(
            fixture,
            layerId,
            new RasterSeed(
                Name: "west",
                Width: 2,
                Height: 2,
                UpperLeftX: 0,
                UpperLeftY: 2,
                ScaleX: 1,
                ScaleY: -1,
                Value: 20,
                AcquisitionDate: WestAcquisition,
                CreatedAt: WestAcquisition),
            new RasterSeed(
                Name: "overlap-newest",
                Width: 2,
                Height: 2,
                UpperLeftX: 1,
                UpperLeftY: 2,
                ScaleX: 1,
                ScaleY: -1,
                Value: 5,
                AcquisitionDate: OverlapAcquisition,
                CreatedAt: OverlapAcquisition),
            new RasterSeed(
                Name: "east",
                Width: 2,
                Height: 2,
                UpperLeftX: 2,
                UpperLeftY: 2,
                ScaleX: 1,
                ScaleY: -1,
                Value: 40,
                AcquisitionDate: EastAcquisition,
                CreatedAt: EastAcquisition));
    }

    public static Task SeedOverlappingRasterStackAsync(
        WebAppFixture fixture,
        int count,
        int layerId = WebAppFixture.TestLayerId)
    {
        var baseTime = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var rasters = Enumerable.Range(0, count)
            .Select(index => new RasterSeed(
                Name: $"overlap-{index + 1}",
                Width: 2,
                Height: 2,
                UpperLeftX: 0,
                UpperLeftY: 2,
                ScaleX: 1,
                ScaleY: -1,
                Value: index + 1,
                AcquisitionDate: baseTime.AddMinutes(index),
                CreatedAt: baseTime.AddMinutes(index)))
            .ToArray();

        return ReplaceLayerRastersAsync(fixture, layerId, rasters);
    }

    public static byte[] CreatePointSelectionGeometry(double x, double y)
    {
        var factory = new GeometryFactory();
        return new WKBWriter().Write(factory.CreatePoint(new Coordinate(x, y)));
    }

    public static async Task ReplaceLayerRastersAsync(
        WebAppFixture fixture,
        int layerId,
        params RasterSeed[] rasters)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var schemaName = fixture.CurrentSchema
            ?? throw new InvalidOperationException("Fixture schema is not initialized.");

        await using var connection = await fixture.Postgres.GetConnectionAsync(schemaName).ConfigureAwait(false);

        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM honua.raster_data WHERE layer_id = @layerId;";
            delete.Parameters.AddWithValue("layerId", layerId);
            await delete.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        foreach (var raster in rasters)
        {
            await InsertConstantRasterAsync(connection, layerId, raster).ConfigureAwait(false);
        }
    }

    private static async Task InsertConstantRasterAsync(
        NpgsqlConnection connection,
        int layerId,
        RasterSeed raster)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO honua.raster_data (layer_id, name, description, raster, acquisition_date, created_at)
            SELECT @layerId,
                   @name,
                   @description,
                   ST_AddBand(
                       ST_MakeEmptyRaster(@width, @height, @upperLeftX, @upperLeftY, @scaleX, @scaleY, 0, 0, @srid),
                       '32BF'::text,
                       @value,
                       NULL
                   ),
                   @acquisitionDate,
                   @createdAt;
            """;
        command.Parameters.AddWithValue("layerId", layerId);
        command.Parameters.AddWithValue("name", raster.Name);
        command.Parameters.AddWithValue("description", (object?)raster.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("width", raster.Width);
        command.Parameters.AddWithValue("height", raster.Height);
        command.Parameters.AddWithValue("upperLeftX", raster.UpperLeftX);
        command.Parameters.AddWithValue("upperLeftY", raster.UpperLeftY);
        command.Parameters.AddWithValue("scaleX", raster.ScaleX);
        command.Parameters.AddWithValue("scaleY", raster.ScaleY);
        command.Parameters.AddWithValue("srid", raster.Srid);
        command.Parameters.AddWithValue("value", raster.Value);
        command.Parameters.AddWithValue("acquisitionDate", raster.AcquisitionDate.UtcDateTime);
        command.Parameters.AddWithValue("createdAt", raster.CreatedAt.UtcDateTime);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
