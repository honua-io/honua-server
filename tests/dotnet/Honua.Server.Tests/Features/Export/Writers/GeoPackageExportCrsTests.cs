// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Io.Export;
using Honua.Io.Export.Writers;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Feature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Server.Tests.Features.Export.Writers;

/// <summary>
/// Reads the CRS metadata out of an exported GeoPackage (honua-server#4419). The repository's
/// strongest export round trip — <see cref="GeoPackageAttributeRoundtripTests"/> — passes an SRID
/// in and never reads it back, and <c>Geometry.EqualsExact</c> does not compare SRID, so
/// <c>gpkg_spatial_ref_sys</c>, <c>gpkg_contents.srs_id</c>, <c>gpkg_geometry_columns.srs_id</c>
/// and the per-blob SRID header were all uncovered: a GeoPackage exported with the wrong (or no)
/// CRS passed everything.
/// </summary>
public sealed class GeoPackageExportCrsTests
{
    [Theory]
    [InlineData(4326, "EPSG:4326", "EPSG", 4326)]
    [InlineData(3857, "EPSG:3857", "EPSG", 3857)]
    [InlineData(102100, "ESRI:102100", "ESRI", 102100)]
    public async Task WriteAsync_RecordsTheSrsInEveryPlaceTheGeoPackageSpecRequires(
        int srid, string srsName, string expectedOrganization, int expectedCoordsysId)
    {
        const string wkt = "GEOGCS[\"test\",DATUM[\"test\",SPHEROID[\"test\",6378137,298.257223563]]]";
        var path = await ExportAsync(srid, srsName, wkt);
        try
        {
            await using var connection = Open(path);
            await connection.OpenAsync();

            (await ScalarAsync(connection, "SELECT srs_id FROM gpkg_contents WHERE table_name = 'features'"))
                .Should().Be((long)srid, "gpkg_contents.srs_id identifies the layer's CRS");
            (await ScalarAsync(connection, "SELECT srs_id FROM gpkg_geometry_columns WHERE table_name = 'features'"))
                .Should().Be((long)srid, "gpkg_geometry_columns.srs_id identifies the geometry column's CRS");

            (await ScalarAsync(connection, $"SELECT organization FROM gpkg_spatial_ref_sys WHERE srs_id = {srid}"))
                .Should().Be(expectedOrganization, "the authority is parsed from the supplied srsName");
            (await ScalarAsync(connection, $"SELECT organization_coordsys_id FROM gpkg_spatial_ref_sys WHERE srs_id = {srid}"))
                .Should().Be((long)expectedCoordsysId);
            (await ScalarAsync(connection, $"SELECT definition FROM gpkg_spatial_ref_sys WHERE srs_id = {srid}"))
                .Should().Be(wkt, "the CRS definition must be the WKT the caller resolved, not a placeholder");
            (await ScalarAsync(connection, $"SELECT srs_name FROM gpkg_spatial_ref_sys WHERE srs_id = {srid}"))
                .Should().Be(srsName);

            // The GeoPackage binary header carries the SRID per feature (OGC 12-128r16 §2.1.3);
            // a header that disagreed with the tables would be a file no consumer can trust.
            var blob = (byte[])(await ScalarAsync(connection, "SELECT geom FROM features LIMIT 1"))!;
            blob[0].Should().Be((byte)'G');
            blob[1].Should().Be((byte)'P');
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(4))
                .Should().Be(srid, "every geometry blob's header must declare the same SRID");
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// The mandatory reserved rows: OGC 12-128r16 requires <c>gpkg_spatial_ref_sys</c> to contain
    /// srs_id -1 and 0 in every GeoPackage regardless of the layer's own CRS.
    /// </summary>
    [Fact]
    public async Task WriteAsync_AlwaysSeedsTheReservedUndefinedSrsRows()
    {
        var path = await ExportAsync(4326, "EPSG:4326", srsWkt: null);
        try
        {
            await using var connection = Open(path);
            await connection.OpenAsync();

            (await ScalarAsync(connection, "SELECT organization FROM gpkg_spatial_ref_sys WHERE srs_id = -1"))
                .Should().Be("NONE");
            (await ScalarAsync(connection, "SELECT organization FROM gpkg_spatial_ref_sys WHERE srs_id = 0"))
                .Should().Be("NONE");
            (await ScalarAsync(connection, $"SELECT definition FROM gpkg_spatial_ref_sys WHERE srs_id = 4326"))
                .Should().Be("undefined", "an unresolvable CRS definition is recorded as undefined, not as an empty string");
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static async Task<string> ExportAsync(int srid, string? srsName, string? srsWkt)
    {
        // Both segments after GetTempPath() are fixed literals / a GUID, never rooted.
        var path = Path.Join(Path.GetTempPath(), $"honua-gpkg-crs-{Guid.NewGuid():N}.gpkg");
        var factory = new GeometryFactory(new PrecisionModel(), srid);
        var feature = Feature.Create(
            1,
            new WKBWriter().Write(factory.CreatePoint(new Coordinate(-122.4194, 37.7749))),
            ImmutableDictionary<string, object?>.Empty.Add("name", "crs-probe"));

        (await GeoPackageExportWriter.WriteAsync(
            path,
            ToAsyncEnumerable(feature),
            [new ExportField("name", ExportFieldType.String, true)],
            ExportGeometryType.Point,
            srid,
            srsName,
            srsWkt,
            CancellationToken.None)).Should().Be(1);

        return path;
    }

    private static SqliteConnection Open(string path)
        => new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static void Cleanup(string path)
    {
        SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    private static async IAsyncEnumerable<Feature> ToAsyncEnumerable(params Feature[] features)
    {
        foreach (var feature in features)
        {
            yield return feature;
        }

        await Task.CompletedTask;
    }
}
