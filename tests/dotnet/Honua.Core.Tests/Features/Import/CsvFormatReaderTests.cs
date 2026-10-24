// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadStreamingAsync_EwktProjectedSrid_KeepsEmbeddedSrid()
    {
        // #6171: the per-value SRID is part of the geometry. Callers must not replace it
        // with the 4326 factory default.
        var csv = """
            id,geom
            1,SRID=3857;POINT(-13627361 4544761)
            """;

        var (features, _) = await ReadAllAsync(csv);

        var point = features.Should().ContainSingle().Which.Geometry.Should().BeOfType<Point>().Subject;
        point.SRID.Should().Be(3857);
        point.X.Should().BeApproximately(-13627361, 0.5);
        point.Y.Should().BeApproximately(4544761, 0.5);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task TryDetectGeometrySridAsync_EwkbHex_ReturnsEmbeddedSrid()
    {
        var writer = new WKBWriter { Strict = false, HandleSRID = true };
        var hex = Convert.ToHexString(writer.Write(new Point(-13627361, 4544761) { SRID = 3857 }));
        var csv = $"""
            id,geom
            1,{hex}
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var srid = await CsvFormatReader.TryDetectGeometrySridAsync(stream, CancellationToken.None);

        srid.Should().Be(3857);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task TryDetectGeometrySridAsync_PlainWkt_ReturnsNull()
    {
        var csv = """
            id,geom
            1,POINT(-122.4194 37.7749)
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var srid = await CsvFormatReader.TryDetectGeometrySridAsync(stream, CancellationToken.None);

        srid.Should().BeNull();
    }

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadStreamingAsync_StrayQuoteInUnquotedField_DoesNotMergeFollowingRows()
    {
        // #6173: an inch mark is not a quoted-field opener. Three physical rows stay three features.
        var csv = """
            id,part,code
            1,12" pipe,a
            2,coupling,b
            3,12" valve,c
            """;

        var (features, _) = await ReadAllAsync(csv);

        features.Should().HaveCount(3);
        features[0].Attributes["part"].Should().Be("12\" pipe");
        features[0].Attributes["code"].Should().Be("a");
        features[1].Attributes["id"].Should().Be("2");
        features[1].Attributes["part"].Should().Be("coupling");
        features[2].Attributes["part"].Should().Be("12\" valve");
        features[2].Attributes["code"].Should().Be("c");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadStreamingAsync_QuotedFieldWithCommaAndEscapedQuote_StaysOneField()
    {
        var csv = """
            name,address
            Capitol,"1100 Congress Ave, ""Austin"", TX"
            """;

        var (features, _) = await ReadAllAsync(csv);

        features.Should().ContainSingle();
        features[0].Attributes["address"].Should().Be("1100 Congress Ave, \"Austin\", TX");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadStreamingAsync_QuotedFieldSpanningLines_JoinsUntilClosingQuote()
    {
        var csv = "id,note\n1,\"line1\nline2\",tail\n";

        var (features, _) = await ReadAllAsync(csv);

        features.Should().ContainSingle();
        features[0].Attributes["note"].Should().Be("line1\nline2");
        features[0].Attributes["id"].Should().Be("1");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadStreamingAsync_OpenQuoteWithManyContinuationLines_FinishesWithinOneSecond()
    {
        // #6174: an unclosed quote must not rescan the whole record on every continuation line.
        const int continuationLines = 200_000;
        var csv = new StringBuilder(continuationLines * 2 + 8);
        csv.Append('"');
        csv.Append('\n');
        for (var i = 0; i < continuationLines; i++)
        {
            csv.Append('a');
            csv.Append('\n');
        }

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv.ToString()));
        var started = System.Diagnostics.Stopwatch.StartNew();

        var act = async () =>
        {
            await foreach (var _ in CsvFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
            {
                // Drain the reader. The assertion is that the unclosed quote throws, not that a row is yielded.
            }
        };

        await act.Should().ThrowAsync<InvalidDataException>();
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadStreamingAsync_SingleCoordinateHeader_KeepsColumnAsAttribute()
    {
        // #6172: one coordinate-like header is not half of a point, so its values must
        // survive as an attribute beside a real geometry column.
        var csv = """
            id,x,wkt
            1,42,POINT(1 2)
            """;

        var (features, diagnostics) = await ReadAllAsync(csv);

        features.Should().ContainSingle();
        features[0].Attributes["x"].Should().Be("42");
        features[0].Attributes["id"].Should().Be("1");
        var point = features[0].Geometry.Should().BeOfType<Point>().Subject;
        point.X.Should().BeApproximately(1, 1e-6);
        point.Y.Should().BeApproximately(2, 1e-6);
        diagnostics.UnparseableGeometryRows.Should().Be(0);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadStreamingAsync_UnpairedLongitudeHeader_KeepsColumnAsAttribute()
    {
        var csv = """
            site,long,notes
            pier,very,north
            """;

        var (features, _) = await ReadAllAsync(csv);

        features.Should().ContainSingle();
        features[0].Geometry.Should().BeNull();
        features[0].Attributes["long"].Should().Be("very");
        features[0].Attributes["site"].Should().Be("pier");
        features[0].Attributes["notes"].Should().Be("north");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ReadStreamingAsync_HeuristicCoordinates_UnparseableValues_AreKeptAndCounted()
    {
        var csv = """
            name,lon,lat
            Broken,N/A,37.7
            """;

        var (features, diagnostics) = await ReadAllAsync(csv);

        diagnostics.UnparseableGeometryRows.Should().Be(1);
        features.Should().ContainSingle();
        features[0].Geometry.Should().BeNull();
        features[0].Attributes["lon"].Should().Be("N/A");
        features[0].Attributes["lat"].Should().Be("37.7");
        features[0].Attributes["name"].Should().Be("Broken");
    }

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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
