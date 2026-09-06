// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using FluentAssertions;
using Honua.Io.Export;
using Honua.Io.Export.Writers;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.IO.Esri;
using Feature = Honua.Core.Features.FeatureStore.Domain.Feature;
using WkbWriter = NetTopologySuite.IO.WKBWriter;

namespace Honua.Server.Tests.Features.Export.Writers;

public sealed class ShapefileExportWriterTests
{
    [Fact]
    public async Task WriteAsync_PreservesMultiPolygonParts_WhenLayerDeclaresPolygon()
    {
        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
        var multiPolygon = geometryFactory.CreateMultiPolygon(
        [
            geometryFactory.CreatePolygon(CreateRing(geometryFactory, 0, 0, 1, 1)),
            geometryFactory.CreatePolygon(CreateRing(geometryFactory, 2, 2, 3, 3))
        ]);

        var feature = Feature.Create(
            1,
            new WkbWriter().Write(multiPolygon),
            ImmutableDictionary<string, object?>.Empty.Add("name", "multipart"));

        await using var output = new MemoryStream();
        var result = await ShapefileExportWriter.WriteAsync(
            output,
            ToAsyncEnumerable(feature),
            [new ExportField("name", ExportFieldType.String, true)],
            ExportGeometryType.Polygon,
            prjWkt: null,
            NullLogger.Instance,
            CancellationToken.None);

        result.WrittenCount.Should().Be(1);
        output.Position = 0;
        // Path.Combine args after the first are fixed literals / a GUID (never rooted), so GetTempPath() is never dropped.
        var extractedDir = Path.Join(Path.GetTempPath(), "honua-shp-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractedDir);

        try
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true))
            {
                zip.ExtractToDirectory(extractedDir);
            }

            var shpPath = Directory.GetFiles(extractedDir, "*.shp").Single();
            using var reader = Shapefile.OpenRead(shpPath);

            reader.Read(out var deleted, out var exportedFeature).Should().BeTrue();
            deleted.Should().BeFalse();
            exportedFeature.Should().NotBeNull();
            exportedFeature!.Geometry.NumGeometries.Should().Be(2);
        }
        finally
        {
            Directory.Delete(extractedDir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_3DPointInput_WritesPointZShapeTypeAndPreservesZ()
    {
        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
        var point3d = geometryFactory.CreatePoint(new CoordinateZ(10, 20, 123.5));

        var feature = Feature.Create(
            1,
            new WkbWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: false).Write(point3d),
            ImmutableDictionary<string, object?>.Empty.Add("name", "summit"));

        await using var output = new MemoryStream();
        var result = await ShapefileExportWriter.WriteAsync(
            output,
            ToAsyncEnumerable(feature),
            [new ExportField("name", ExportFieldType.String, true)],
            ExportGeometryType.Point,
            prjWkt: null,
            NullLogger.Instance,
            CancellationToken.None);

        result.WrittenCount.Should().Be(1);
        output.Position = 0;
        // Path.Combine args after the first are fixed literals / a GUID (never rooted), so GetTempPath() is never dropped.
        var extractedDir = Path.Join(Path.GetTempPath(), "honua-shp-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractedDir);

        try
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true))
            {
                zip.ExtractToDirectory(extractedDir);
            }

            var shpPath = Directory.GetFiles(extractedDir, "*.shp").Single();
            using var reader = Shapefile.OpenRead(shpPath);

            reader.ShapeType.Should().Be(ShapeType.PointZM,
                "3D input must select the Z-capable shape type instead of flattening to 2D (#2744)");
            reader.Read(out _, out var exportedFeature).Should().BeTrue();
            exportedFeature!.Geometry.Coordinate.Z.Should().BeApproximately(123.5, 1e-9);
        }
        finally
        {
            Directory.Delete(extractedDir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_2DPointInput_KeepsPlain2DShapeType()
    {
        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
        var point = geometryFactory.CreatePoint(new Coordinate(10, 20));

        var feature = Feature.Create(
            1,
            new WkbWriter().Write(point),
            ImmutableDictionary<string, object?>.Empty.Add("name", "flat"));

        await using var output = new MemoryStream();
        var result = await ShapefileExportWriter.WriteAsync(
            output,
            ToAsyncEnumerable(feature),
            [new ExportField("name", ExportFieldType.String, true)],
            ExportGeometryType.Point,
            prjWkt: null,
            NullLogger.Instance,
            CancellationToken.None);

        result.WrittenCount.Should().Be(1);
        output.Position = 0;
        // Path.Combine args after the first are fixed literals / a GUID (never rooted), so GetTempPath() is never dropped.
        var extractedDir = Path.Join(Path.GetTempPath(), "honua-shp-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractedDir);

        try
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true))
            {
                zip.ExtractToDirectory(extractedDir);
            }

            var shpPath = Directory.GetFiles(extractedDir, "*.shp").Single();
            using var reader = Shapefile.OpenRead(shpPath);
            reader.ShapeType.Should().Be(ShapeType.Point, "2D input keeps the plain 2D shape type");
        }
        finally
        {
            Directory.Delete(extractedDir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_3DLineInput_WritesPolyLineZShapeType()
    {
        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
        var line3d = geometryFactory.CreateLineString(
        [
            new CoordinateZ(0, 0, 1.5),
            new CoordinateZ(1, 1, 2.5)
        ]);

        var feature = Feature.Create(
            1,
            new WkbWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: false).Write(line3d),
            ImmutableDictionary<string, object?>.Empty.Add("name", "trail"));

        await using var output = new MemoryStream();
        var result = await ShapefileExportWriter.WriteAsync(
            output,
            ToAsyncEnumerable(feature),
            [new ExportField("name", ExportFieldType.String, true)],
            ExportGeometryType.LineString,
            prjWkt: null,
            NullLogger.Instance,
            CancellationToken.None);

        result.WrittenCount.Should().Be(1);
        output.Position = 0;
        // Path.Combine args after the first are fixed literals / a GUID (never rooted), so GetTempPath() is never dropped.
        var extractedDir = Path.Join(Path.GetTempPath(), "honua-shp-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractedDir);

        try
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true))
            {
                zip.ExtractToDirectory(extractedDir);
            }

            var shpPath = Directory.GetFiles(extractedDir, "*.shp").Single();
            using var reader = Shapefile.OpenRead(shpPath);

            reader.ShapeType.Should().Be(ShapeType.PolyLineZM);
            reader.Read(out _, out var exportedFeature).Should().BeTrue();
            exportedFeature!.Geometry.Coordinates[0].Z.Should().BeApproximately(1.5, 1e-9);
            exportedFeature.Geometry.Coordinates[1].Z.Should().BeApproximately(2.5, 1e-9);
        }
        finally
        {
            Directory.Delete(extractedDir, recursive: true);
        }
    }

    [Fact]
    public void BuildDbfFieldMap_MakesTruncatedAndCaseInsensitiveCollisionsUnique()
    {
        var method = typeof(ShapefileExportWriter).GetMethod(
            "BuildDbfFieldMap",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var fields = new[]
        {
            new ExportField("abcdefghij", ExportFieldType.String, true),
            new ExportField("ABCDEFGHIJ", ExportFieldType.String, true),
            new ExportField("very_long_field_alpha", ExportFieldType.String, true),
            new ExportField("very_long_field_beta", ExportFieldType.String, true)
        };
        var warnings = new List<string>();

        var map = (Dictionary<string, string>)method!.Invoke(null, [fields, warnings])!;

        map.Values.Should().OnlyContain(name => name.Length <= 10);
        map.Values.Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(fields.Length);
        warnings.Should().NotBeEmpty();
    }

    /// <summary>
    /// honua-server#4419: every existing call site passed <c>prjWkt: null</c>, so the branch that
    /// emits the <c>.prj</c> had no coverage at all and no test asserted the sidecar was present in
    /// the export ZIP. A shapefile shipped without its <c>.prj</c> is a CRS-less file — the classic
    /// silent-wrong-data shapefile failure, because the consumer guesses.
    /// </summary>
    [Fact]
    public async Task WriteAsync_WithPrjWkt_WritesThePrjSidecarVerbatimIntoTheArchive()
    {
        const string wkt =
            "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]]," +
            "PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433],AUTHORITY[\"EPSG\",\"4326\"]]";
        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
        var feature = Feature.Create(
            1,
            new WkbWriter().Write(geometryFactory.CreatePoint(new Coordinate(-122.4194, 37.7749))),
            ImmutableDictionary<string, object?>.Empty.Add("name", "projected"));

        await using var output = new MemoryStream();
        var result = await ShapefileExportWriter.WriteAsync(
            output,
            ToAsyncEnumerable(feature),
            [new ExportField("name", ExportFieldType.String, true)],
            ExportGeometryType.Point,
            prjWkt: wkt,
            NullLogger.Instance,
            CancellationToken.None);

        result.WrittenCount.Should().Be(1);
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);

        var entryNames = zip.Entries.Select(entry => Path.GetExtension(entry.FullName)).ToArray();
        entryNames.Should().Contain(".shp").And.Contain(".shx").And.Contain(".dbf");
        entryNames.Should().Contain(".prj", "a shapefile without a .prj carries no CRS at all");

        var prj = zip.Entries.Single(entry => entry.FullName.EndsWith(".prj", StringComparison.OrdinalIgnoreCase));
        Path.GetFileNameWithoutExtension(prj.FullName).Should().Be(
            Path.GetFileNameWithoutExtension(
                zip.Entries.Single(entry => entry.FullName.EndsWith(".shp", StringComparison.OrdinalIgnoreCase)).FullName),
            "the sidecar only applies to a shapefile whose base name it shares");

        using var reader = new StreamReader(prj.Open());
        (await reader.ReadToEndAsync()).Should().Be(wkt, "the resolved CRS WKT is written through unaltered");
    }

    /// <summary>
    /// The complementary negative: with no resolvable CRS the writer must omit the sidecar rather
    /// than emit an empty or placeholder <c>.prj</c>, which a consumer would read as a real CRS.
    /// </summary>
    [Fact]
    public async Task WriteAsync_WithoutPrjWkt_OmitsThePrjSidecar()
    {
        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
        var feature = Feature.Create(
            1,
            new WkbWriter().Write(geometryFactory.CreatePoint(new Coordinate(1, 2))),
            ImmutableDictionary<string, object?>.Empty.Add("name", "unprojected"));

        await using var output = new MemoryStream();
        await ShapefileExportWriter.WriteAsync(
            output,
            ToAsyncEnumerable(feature),
            [new ExportField("name", ExportFieldType.String, true)],
            ExportGeometryType.Point,
            prjWkt: null,
            NullLogger.Instance,
            CancellationToken.None);

        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
        zip.Entries.Should().NotContain(
            entry => entry.FullName.EndsWith(".prj", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// honua-server#4419: no test read a DBF field descriptor, so declared field types were
    /// unverified — a writer that emitted every attribute as a character column would have passed.
    /// The expected types are the DBF type codes the shapefile specification defines for each
    /// <see cref="ExportFieldType"/>.
    /// </summary>
    [Fact]
    public async Task WriteAsync_MixedFieldTypes_WritesTheDeclaredDbfFieldTypes()
    {
        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
        var feature = Feature.Create(
            1,
            new WkbWriter().Write(geometryFactory.CreatePoint(new Coordinate(-122.4194, 37.7749))),
            ImmutableDictionary<string, object?>.Empty
                .Add("name", "typed")
                .Add("count", 42)
                .Add("ratio", 1.5)
                .Add("flag", true)
                .Add("seen", new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)));

        await using var output = new MemoryStream();
        await ShapefileExportWriter.WriteAsync(
            output,
            ToAsyncEnumerable(feature),
            [
                new ExportField("name", ExportFieldType.String, true),
                new ExportField("count", ExportFieldType.Integer, true),
                new ExportField("ratio", ExportFieldType.Double, true),
                new ExportField("flag", ExportFieldType.Boolean, true),
                new ExportField("seen", ExportFieldType.DateTime, true)
            ],
            ExportGeometryType.Point,
            prjWkt: null,
            NullLogger.Instance,
            CancellationToken.None);

        output.Position = 0;
        // Path.Combine args after the first are fixed literals / a GUID (never rooted), so GetTempPath() is never dropped.
        var extractedDir = Path.Join(Path.GetTempPath(), "honua-shp-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractedDir);
        try
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true))
            {
                zip.ExtractToDirectory(extractedDir);
            }

            using var reader = Shapefile.OpenRead(Directory.GetFiles(extractedDir, "*.shp").Single());
            var byName = reader.Fields.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);

            byName["name"].Should().BeOfType<NetTopologySuite.IO.Esri.Dbf.Fields.DbfCharacterField>();
            byName["count"].Should().BeOfType<NetTopologySuite.IO.Esri.Dbf.Fields.DbfNumericInt32Field>();
            byName["ratio"].Should().BeOfType<NetTopologySuite.IO.Esri.Dbf.Fields.DbfNumericDoubleField>();
            byName["flag"].Should().BeOfType<NetTopologySuite.IO.Esri.Dbf.Fields.DbfLogicalField>();
            byName["seen"].Should().BeOfType<NetTopologySuite.IO.Esri.Dbf.Fields.DbfDateField>();

            reader.Read(out _, out var exported).Should().BeTrue();
            exported!.Attributes["count"].Should().Be(42);
            exported.Attributes["ratio"].Should().Be(1.5);
            exported.Attributes["flag"].Should().Be(true);
        }
        finally
        {
            Directory.Delete(extractedDir, recursive: true);
        }
    }

    /// <summary>
    /// honua-server#4419: every shapefile fixture in this repository used ASCII strings, so the
    /// writer's hard-coded UTF-8 DBF encoding was never verified. A non-ASCII attribute that
    /// round-trips proves the encoding claim rather than assuming it.
    /// </summary>
    [Fact]
    public async Task WriteAsync_NonAsciiAttribute_RoundTripsThroughTheUtf8Dbf()
    {
        const string name = "Hawaiʻi 東京";
        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
        var feature = Feature.Create(
            1,
            new WkbWriter().Write(geometryFactory.CreatePoint(new Coordinate(-157.8583, 21.3069))),
            ImmutableDictionary<string, object?>.Empty.Add("name", name));

        await using var output = new MemoryStream();
        await ShapefileExportWriter.WriteAsync(
            output,
            ToAsyncEnumerable(feature),
            [new ExportField("name", ExportFieldType.String, true)],
            ExportGeometryType.Point,
            prjWkt: null,
            NullLogger.Instance,
            CancellationToken.None);

        output.Position = 0;
        // Path.Combine args after the first are fixed literals / a GUID (never rooted), so GetTempPath() is never dropped.
        var extractedDir = Path.Join(Path.GetTempPath(), "honua-shp-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractedDir);
        try
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true))
            {
                zip.ExtractToDirectory(extractedDir);
            }

            var shpPath = Directory.GetFiles(extractedDir, "*.shp").Single();
            var options = new NetTopologySuite.IO.Esri.ShapefileReaderOptions
            {
                Encoding = System.Text.Encoding.UTF8
            };
            using var reader = Shapefile.OpenRead(shpPath, options);
            reader.Read(out _, out var exported).Should().BeTrue();
            exported!.Attributes["name"].Should().Be(
                name,
                "the writer hard-codes Encoding.UTF8 for the DBF, so a multi-byte name must survive");
        }
        finally
        {
            Directory.Delete(extractedDir, recursive: true);
        }
    }

    private static LinearRing CreateRing(
        GeometryFactory geometryFactory,
        double minX,
        double minY,
        double maxX,
        double maxY)
        => geometryFactory.CreateLinearRing(
        [
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY)
        ]);

    private static async IAsyncEnumerable<Feature> ToAsyncEnumerable(params Feature[] features)
    {
        foreach (var feature in features)
        {
            yield return feature;
        }

        await Task.CompletedTask;
    }
}
