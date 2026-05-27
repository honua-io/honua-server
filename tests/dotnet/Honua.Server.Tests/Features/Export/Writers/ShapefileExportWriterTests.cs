// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using FluentAssertions;
using Honua.Server.Features.Export;
using Honua.Server.Features.Export.Writers;
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
        var extractedDir = Path.Combine(Path.GetTempPath(), "honua-shp-test", Guid.NewGuid().ToString("N"));
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
