// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;

namespace Honua.TestKit.Infrastructure;

public sealed record RasterSeed(
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

public static class RasterIntegrationTestData
{
    public static readonly DateTimeOffset WestAcquisition = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset EastAcquisition = new(2024, 1, 15, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset OverlapAcquisition = new(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);

    // Seeds the issue-522 mosaic and keeps the seed lock until the handle is disposed; see
    // HoldLayerRastersAsync.
    public static Task<IAsyncDisposable> HoldIssue522MosaicAsync(
        WebAppFixture fixture,
        int layerId = WebAppFixture.TestLayerId)
        => HoldLayerRastersAsync(fixture, layerId, CreateIssue522Mosaic());

    public static Task RunWithIssue522MosaicAsync(
        WebAppFixture fixture,
        Func<Task> action,
        int layerId = WebAppFixture.TestLayerId)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(action);

        return fixture.Postgres.RunUnderSchemaMutationLockAsync(async () =>
        {
            await ReplaceLayerRastersUnderLockAsync(fixture, layerId, CreateIssue522Mosaic()).ConfigureAwait(false);
            await action().ConfigureAwait(false);
        });
    }

    // Replaces the layer's rasters and runs the action while still holding the shared seed
    // advisory lock, so no parallel fixture seed (whose honua.layers cleanup cascades to
    // honua.raster_data) can delete the rows between the seed and the reads.
    public static Task RunWithLayerRastersAsync(
        WebAppFixture fixture,
        int layerId,
        IReadOnlyList<RasterSeed> rasters,
        Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(rasters);
        ArgumentNullException.ThrowIfNull(action);

        return fixture.Postgres.RunUnderSchemaMutationLockAsync(async () =>
        {
            await ReplaceLayerRastersUnderLockAsync(fixture, layerId, rasters).ConfigureAwait(false);
            await action().ConfigureAwait(false);
        });
    }

    public static RasterSeed[] CreateOverlappingRasterStack(int count)
    {
        var baseTime = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, count)
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
    }

    public static byte[] CreatePointSelectionGeometry(double x, double y)
    {
        var factory = new GeometryFactory();
        return new WKBWriter().Write(factory.CreatePoint(new Coordinate(x, y)));
    }

    // Replaces the layer's rasters under the shared seed advisory lock and keeps holding the
    // lock until the returned handle is disposed, so no parallel fixture seed (whose
    // honua.layers cleanup cascades to honua.raster_data) can delete or re-add rows before the
    // test's reads finish. Use it as
    // `await using var rasters = await HoldLayerRastersAsync(...)` at the top of the case.
    public static async Task<IAsyncDisposable> HoldLayerRastersAsync(
        WebAppFixture fixture,
        int layerId,
        params RasterSeed[] rasters)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(rasters);

        var lease = await fixture.Postgres.AcquireSchemaMutationLockAsync().ConfigureAwait(false);
        try
        {
            await ReplaceLayerRastersUnderLockAsync(fixture, layerId, rasters).ConfigureAwait(false);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return lease;
    }

    private static RasterSeed[] CreateIssue522Mosaic()
        =>
        [
            new RasterSeed("west", 2, 2, 0, 2, 1, -1, 20, WestAcquisition, WestAcquisition),
            new RasterSeed("overlap-newest", 2, 2, 1, 2, 1, -1, 5, OverlapAcquisition, OverlapAcquisition),
            new RasterSeed("east", 2, 2, 2, 2, 1, -1, 40, EastAcquisition, EastAcquisition),
        ];

    private static async Task ReplaceLayerRastersUnderLockAsync(
        WebAppFixture fixture,
        int layerId,
        IReadOnlyList<RasterSeed> rasters)
    {
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
