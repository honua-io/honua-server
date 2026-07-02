// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.FileImport.Services;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Tests.Features.Import;

public sealed class CsvFormatReaderTests
{
    [Fact]
    public async Task ReadStreamingAsync_PostgisEwktExport_ImportsWithGeometry()
    {
        // #2361: a standard PostGIS CSV export encodes the geometry column as EWKT
        // ("SRID=<n>;WKT"). Previously a bare catch{} silently dropped it to NULL.
        var csv = """
            id,name,geom
            1,San Francisco,SRID=4326;POINT(-122.4194 37.7749)
            """;

        var (features, _) = await ReadAllAsync(csv);

        features.Should().ContainSingle();
        var point = features[0].Geometry.Should().BeOfType<Point>().Subject;
        point.X.Should().BeApproximately(-122.4194, 1e-6);
        point.SRID.Should().Be(4326);
        features[0].Attributes["name"].Should().Be("San Francisco");
        // The geometry column must be excluded from attributes on success.
        features[0].Attributes.Exists("geom").Should().BeFalse();
    }

    [Fact]
    public async Task ReadStreamingAsync_PostgisEwkbHexExport_ImportsWithGeometry()
    {
        // #2361: PostGIS also exports geometry as WKB/EWKB hex. This must parse too.
        var writer = new WKBWriter { HandleSRID = true };
        var hex = Convert.ToHexString(writer.Write(new Point(-122.4194, 37.7749) { SRID = 4326 }));
        var csv = $"""
            id,name,geom
            1,San Francisco,{hex}
            """;

        var (features, _) = await ReadAllAsync(csv);

        features.Should().ContainSingle();
        var point = features[0].Geometry.Should().BeOfType<Point>().Subject;
        point.X.Should().BeApproximately(-122.4194, 1e-6);
    }

    [Fact]
    public async Task ReadStreamingAsync_UnparseableGeometry_WarnsAndPreservesRawValueInsteadOfSilentNull()
    {
        // #2361: a claimed geometry value that cannot be parsed must NOT silently import as a
        // successful null-geometry row with the raw value discarded. It is recorded on the
        // diagnostics sink (surfaced as an import warning) and the raw value is preserved.
        var csv = """
            id,name,wkt
            1,Broken,not-a-geometry
            """;

        var (features, diagnostics) = await ReadAllAsync(csv);

        diagnostics.UnparseableGeometryRows.Should().Be(1);
        features.Should().ContainSingle();
        features[0].Geometry.Should().BeNull();
        features[0].Attributes["wkt"].Should().Be("not-a-geometry");
        features[0].Attributes["name"].Should().Be("Broken");
    }

    [Fact]
    public async Task ReadStreamingAsync_ValidWkt_DoesNotReportUnparseableGeometry()
    {
        var csv = """
            id,name,wkt
            1,Test,POINT(1 2)
            """;

        var (features, diagnostics) = await ReadAllAsync(csv);

        features.Should().ContainSingle();
        features[0].Geometry.Should().BeOfType<Point>();
        diagnostics.UnparseableGeometryRows.Should().Be(0);
    }

    private static async Task<(List<IFeature> Features, CsvGeometryDiagnostics Diagnostics)> ReadAllAsync(string csv)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var diagnostics = new CsvGeometryDiagnostics();
        var features = new List<IFeature>();
        await foreach (var feature in CsvFormatReader.ReadStreamingAsync(
            stream, delimiterOverride: null, diagnostics, CancellationToken.None))
        {
            features.Add(feature);
        }

        return (features, diagnostics);
    }
}
