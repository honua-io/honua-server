// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using Honua.Core.Features.FileImport.Services;
using Honua.Io.Export;
using Honua.Io.Export.Writers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.IO.Esri;
using Xunit;
using Feature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace GavDataRoundtrip;

public sealed class RoundtripProbes
{
    [Fact]
    public async Task GeoJson_Int64BeyondDoublePrecision_PreservesValueAndType()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{"key":9007199254740993}}]}
            """));
        await foreach (var feature in new StreamingGeoJsonReader().ReadFeaturesAsync(stream))
        {
            Assert.Equal(9007199254740993L, Assert.IsType<long>(feature.Attributes["key"]));
        }
    }

    [Fact]
    public async Task Csv_ZmGeometry_PreservesBothOrdinates()
    {
        var point = new GeometryFactory().CreatePoint(new CoordinateZM(1, 2, 30, 40));
        using var output = new MemoryStream();
        await CsvExportWriter.WriteAsync(output, Rows(MakeFeature(point)), [], CancellationToken.None);
        output.Position = 0;
        await foreach (var feature in CsvFormatReader.ReadStreamingAsync(output, CancellationToken.None))
        {
            Assert.Equal(30, feature.Geometry.Coordinate.Z);
            Assert.Equal(40, feature.Geometry.Coordinate.M);
        }
    }

    [Theory]
    [InlineData("fid")]
    [InlineData("geom")]
    public async Task GeoPackage_ReservedAttribute_PreservesAttribute(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".gpkg");
        try
        {
            var point = new GeometryFactory().CreatePoint(new Coordinate(1, 2));
            var feature = MakeFeature(point) with { Attributes = ImmutableDictionary<string, object?>.Empty.Add(name, "source-value") };
            var count = await GeoPackageExportWriter.WriteAsync(path, Rows(feature),
                [new ExportField(name, ExportFieldType.String, true)], ExportGeometryType.Point,
                4326, "EPSG:4326", null, CancellationToken.None);
            Assert.Equal(1, count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Shapefile_FirstFeature2D_SecondFeatureZ_PreservesElevation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gav-shp-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var factory = new GeometryFactory();
            using var output = new MemoryStream();
            await ShapefileExportWriter.WriteAsync(output,
                Rows(MakeFeature(factory.CreatePoint(new Coordinate(1, 2))),
                     MakeFeature(factory.CreatePoint(new CoordinateZ(3, 4, 50)))),
                [new ExportField("name", ExportFieldType.String, true)], ExportGeometryType.Point,
                null, NullLogger.Instance, CancellationToken.None);
            output.Position = 0;
            using (var zip = new ZipArchive(output, ZipArchiveMode.Read, true)) zip.ExtractToDirectory(folder);
            using var reader = Shapefile.OpenRead(Path.Combine(folder, "export.shp"));
            Assert.True(reader.Read(out _, out _));
            Assert.True(reader.Read(out _, out var second));
            Assert.Equal(50, second.Geometry.Coordinate.Z);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task Csv_WhitespaceAndEmptyString_PreservesAttributes()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("name,empty,WKT\n\"  \",\"\",POINT (1 2)\n"));
        await foreach (var feature in CsvFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
        {
            Assert.True(feature.Attributes.Exists("name"));
            Assert.Equal("  ", feature.Attributes["name"]);
            Assert.True(feature.Attributes.Exists("empty"));
            Assert.Equal("", feature.Attributes["empty"]);
        }
    }

    [Fact]
    public async Task GeoPackage_Boolean_DeclaresBooleanType()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".gpkg");
        try
        {
            var point = new GeometryFactory().CreatePoint(new Coordinate(1, 2));
            var feature = MakeFeature(point) with { Attributes = ImmutableDictionary<string, object?>.Empty.Add("active", true) };
            await GeoPackageExportWriter.WriteAsync(path, Rows(feature),
                [new ExportField("active", ExportFieldType.Boolean, true)], ExportGeometryType.Point,
                4326, "EPSG:4326", null, CancellationToken.None);
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT type FROM pragma_table_info('features') WHERE name='active'";
            Assert.Equal("BOOLEAN", await command.ExecuteScalarAsync());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Gpx_DisconnectedSegments_PreserveSeparateLines()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            <gpx version="1.1" creator="roundtrip-probe" xmlns="http://www.topografix.com/GPX/1/1">
              <trk><name>two segments</name>
                <trkseg><trkpt lat="0" lon="0"/><trkpt lat="0" lon="1"/></trkseg>
                <trkseg><trkpt lat="0" lon="10"/><trkpt lat="0" lon="11"/></trkseg>
              </trk>
            </gpx>
            """));
        var totalLength = 0.0;
        var count = 0;
        await foreach (var feature in GpxFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
        {
            totalLength += feature.Geometry.Length;
            count++;
        }
        Assert.True(count > 0);
        Assert.Equal(2, totalLength);
    }

    [Fact]
    public async Task Gpx_TrackElevations_PreserveZ()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            <gpx version="1.1" creator="roundtrip-probe" xmlns="http://www.topografix.com/GPX/1/1">
              <trk><trkseg>
                <trkpt lat="0" lon="0"><ele>30</ele></trkpt>
                <trkpt lat="0" lon="1"><ele>40</ele></trkpt>
              </trkseg></trk>
            </gpx>
            """));
        await foreach (var feature in GpxFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
            Assert.Equal(30, feature.Geometry.Coordinate.Z);
    }

    private static Feature MakeFeature(Geometry geometry) => Feature.Create(1,
        new WKBWriter(ByteOrder.LittleEndian, false, true, true).Write(geometry),
        ImmutableDictionary<string, object?>.Empty.Add("name", "test"));

    private static async IAsyncEnumerable<Feature> Rows(params Feature[] features)
    {
        foreach (var feature in features) yield return feature;
        await Task.CompletedTask;
    }
}
