// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;
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
        var writer = new WKBWriter { Strict = false, HandleSRID = true };
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

    // -------------------------------------------------------------------
    // CsvImportOptions (explicit columns + address geocoding) — the MCP
    // honua_ingest_dataset options ride through these on the shared pipeline.
    // -------------------------------------------------------------------

    [Fact]
    public async Task ReadStreamingAsync_ExplicitCoordinateColumns_OverrideHeuristics()
    {
        // "easting"/"northing" are not auto-detected names; explicit options must map them.
        var csv = """
            name,easting,northing,geom
            Capitol,-97.7404,30.2747,POINT(1 1)
            """;

        var (features, _) = await ReadAllAsync(csv, new CsvImportOptions
        {
            LongitudeColumn = "easting",
            LatitudeColumn = "northing"
        });

        features.Should().ContainSingle();
        var point = features[0].Geometry.Should().BeOfType<Point>().Subject;
        // Explicit mapping replaces the heuristics entirely: the "geom" WKT column
        // is ignored as a geometry source and preserved as a plain attribute.
        point.X.Should().BeApproximately(-97.7404, 1e-6);
        point.Y.Should().BeApproximately(30.2747, 1e-6);
        features[0].Attributes["geom"].Should().Be("POINT(1 1)");
        features[0].Attributes.Exists("easting").Should().BeFalse();
        features[0].Attributes.Exists("northing").Should().BeFalse();
    }

    [Fact]
    public async Task ReadStreamingAsync_ExplicitCoordinateColumns_UnparseableValues_AreRecorded()
    {
        var csv = """
            name,easting,northing
            Bad,not-a-number,30.2747
            """;

        var (features, diagnostics) = await ReadAllAsync(csv, new CsvImportOptions
        {
            LongitudeColumn = "easting",
            LatitudeColumn = "northing"
        });

        features.Should().ContainSingle();
        features[0].Geometry.Should().BeNull();
        diagnostics.UnparseableGeometryRows.Should().Be(1);
    }

    [Fact]
    public async Task ReadStreamingAsync_ExplicitCoordinateColumn_Missing_ThrowsCsvImportOptionsException()
    {
        var csv = """
            name,lon,lat
            Capitol,-97.7404,30.2747
            """;

        var act = () => ReadAllAsync(csv, new CsvImportOptions
        {
            LongitudeColumn = "no_such_column",
            LatitudeColumn = "lat"
        });

        (await act.Should().ThrowAsync<CsvImportOptionsException>())
            .Which.Message.Should().Contain("no_such_column").And.Contain("Available columns");
    }

    [Fact]
    public async Task ReadStreamingAsync_AddressColumn_GeocodesRowsAndKeepsAddressAttribute()
    {
        var csv = """
            name,address
            Capitol,"1100 Congress Ave, Austin, TX"
            Unknown,"nowhere at all"
            """;

        var geocoded = new List<string>();
        var (features, diagnostics) = await ReadAllAsync(csv, new CsvImportOptions
        {
            AddressColumn = "address",
            AddressGeocoder = (address, _) =>
            {
                geocoded.Add(address);
                return Task.FromResult(address.Contains("Congress")
                    ? new CsvGeocodedAddress(-97.7404, 30.2747)
                    : null);
            }
        });

        features.Should().HaveCount(2);
        var point = features[0].Geometry.Should().BeOfType<Point>().Subject;
        point.X.Should().BeApproximately(-97.7404, 1e-6);
        // The address stays a row attribute (unlike coordinate source columns).
        features[0].Attributes["address"].Should().Be("1100 Congress Ave, Austin, TX");
        // The failed row imports without geometry and is recorded per-row (1-based).
        features[1].Geometry.Should().BeNull();
        diagnostics.GeocodeFailureCount.Should().Be(1);
        diagnostics.GeocodeFailures.Should().ContainSingle()
            .Which.Should().Be(new CsvGeocodeFailure(2, "nowhere at all"));
        geocoded.Should().Equal("1100 Congress Ave, Austin, TX", "nowhere at all");
    }

    [Fact]
    public async Task ReadStreamingAsync_AddressColumn_RowCapExceeded_ThrowsCsvImportOptionsException()
    {
        var builder = new StringBuilder("name,address\n");
        for (var i = 0; i < 3; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"row{i},addr{i}\n");
        }

        var act = () => ReadAllAsync(builder.ToString(), new CsvImportOptions
        {
            AddressColumn = "address",
            MaxGeocodedRows = 2,
            AddressGeocoder = (_, _) => Task.FromResult<CsvGeocodedAddress?>(new CsvGeocodedAddress(0, 0))
        });

        (await act.Should().ThrowAsync<CsvImportOptionsException>())
            .Which.Message.Should().Contain("capped at 2");
    }

    [Fact]
    public async Task ReadStreamingAsync_AddressAndCoordinateColumns_ThrowsCsvImportOptionsException()
    {
        var csv = """
            name,address,lon,lat
            Capitol,somewhere,-97.7,30.2
            """;

        var act = () => ReadAllAsync(csv, new CsvImportOptions
        {
            AddressColumn = "address",
            LongitudeColumn = "lon",
            LatitudeColumn = "lat"
        });

        (await act.Should().ThrowAsync<CsvImportOptionsException>())
            .Which.Message.Should().Contain("mutually exclusive");
    }

    private static Task<(List<IFeature> Features, CsvGeometryDiagnostics Diagnostics)> ReadAllAsync(string csv)
        => ReadAllAsync(csv, options: null);

    private static async Task<(List<IFeature> Features, CsvGeometryDiagnostics Diagnostics)> ReadAllAsync(
        string csv,
        CsvImportOptions? options)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var diagnostics = new CsvGeometryDiagnostics();
        var features = new List<IFeature>();
        await foreach (var feature in CsvFormatReader.ReadStreamingAsync(
            stream, delimiterOverride: null, diagnostics, options, CancellationToken.None))
        {
            features.Add(feature);
        }

        return (features, diagnostics);
    }
}
