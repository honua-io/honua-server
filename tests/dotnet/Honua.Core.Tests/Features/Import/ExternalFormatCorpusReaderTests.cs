// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;
using Honua.TestKit;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Reads the format fixtures in <c>tests/fixtures/external-format-corpus/v1</c> — every one of
/// them written by GDAL/OGR, not by Honua — and asserts exact ordinates and attribute values
/// against the checked-in source GeoJSON (honua-server#4419).
/// </summary>
/// <remarks>
/// <para>
/// Almost every format fixture in this repository is serialized in-test by the same library the
/// reader under test consumes, which cannot detect a shared misinterpretation: if the writer and
/// the reader both transpose longitude and latitude, the round trip still passes. These fixtures
/// are produced by an independent implementation from a plain-text source that a human can read,
/// so the expected values below are derived from the source document, not from a snapshot of
/// Honua's own output.
/// </para>
/// <para>
/// The three points are deliberately awkward: |longitude| differs from |latitude| in every case,
/// the signs differ, one is in the southern hemisphere, and one name is non-ASCII
/// (<c>Hawaiʻi 東京</c> — an ʻokina plus CJK). Transposing the pair, dropping a sign, truncating
/// a precision digit or mangling UTF-8 all change an assertion here.
/// </para>
/// </remarks>
public sealed class ExternalFormatCorpusReaderTests
{
    private static readonly (double X, double Y, string Name, string Elevation)[] ExpectedSites =
    [
        (-122.4194, 37.7749, "San Francisco", "16.5"),
        (-157.8583, 21.3069, "Hawaiʻi 東京", "-3.25"),
        (-68.3029, -54.8019, "Ushuaia", "23")
    ];

    private static CuratedCorpus Corpus => CuratedCorpus.LoadExternalFormats();

    [Fact]
    public void Corpus_EveryDeclaredAsset_MatchesItsRecordedDigest()
    {
        // The digests are the corpus's integrity contract: a fixture edited by hand (or corrupted
        // by a text-mode checkout) must fail loudly rather than silently changing what the format
        // tests below assert.
        Corpus.VerifyAll();
        Corpus.Assets.Should().HaveCountGreaterThanOrEqualTo(14);
    }

    [Fact]
    public async Task KmlFormatReader_GdalAuthoredPoints_PreservesEveryOrdinateAndAttribute()
    {
        var features = await ReadKmlAsync("survey-sites-kml");

        features.Should().HaveCount(3, "the source document has three placemarks");
        for (var i = 0; i < ExpectedSites.Length; i++)
        {
            var expected = ExpectedSites[i];
            var point = features[i].Geometry.Should().BeOfType<Point>().Subject;
            point.X.Should().Be(expected.X);
            point.Y.Should().Be(expected.Y);
            features[i].Attributes["site_name"].Should().Be(expected.Name);
            features[i].Attributes["elev_m"].Should().Be(expected.Elevation);
            features[i].Attributes["site_id"].Should().Be((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public async Task KmlFormatReader_GdalAuthoredPolygonWithHole_PreservesBothRings()
    {
        var features = await ReadKmlAsync("polygon-with-hole-kml");

        var polygon = features.Should().ContainSingle().Subject.Geometry.Should().BeOfType<Polygon>().Subject;
        polygon.NumInteriorRings.Should().Be(1);
        // Independently computed from polygon-with-hole.source.geojson: a 0.2 x 0.2 outer square
        // minus a 0.1 x 0.1 hole.
        polygon.Area.Should().BeApproximately(0.03, 1e-12);
        polygon.ExteriorRing.EnvelopeInternal.MinX.Should().Be(-122.5);
        polygon.ExteriorRing.EnvelopeInternal.MaxX.Should().Be(-122.3);
        polygon.ExteriorRing.EnvelopeInternal.MinY.Should().Be(37.7);
        polygon.ExteriorRing.EnvelopeInternal.MaxY.Should().Be(37.9);
        polygon.GetInteriorRingN(0).EnvelopeInternal.MinX.Should().Be(-122.45);
        polygon.GetInteriorRingN(0).EnvelopeInternal.MaxY.Should().Be(37.85);
    }

    [Fact]
    public async Task KmlFormatReader_GdalAuthoredLineString_PreservesVertexOrder()
    {
        var features = await ReadKmlAsync("routes-kml");

        var line = features.Should().ContainSingle().Subject.Geometry.Should().BeOfType<LineString>().Subject;
        line.Coordinates.Should().HaveCount(3);
        line.Coordinates[0].X.Should().Be(-122.4194);
        line.Coordinates[0].Y.Should().Be(37.7749);
        line.Coordinates[1].X.Should().Be(-122.3);
        line.Coordinates[2].X.Should().Be(-122.2711);
        line.Coordinates[2].Y.Should().Be(37.8044);
    }

    [Fact]
    public async Task GpxFormatReader_GdalAuthoredWaypoints_PreservesEveryOrdinate()
    {
        await using var stream = File.OpenRead(Corpus.ResolveVerifiedPath("survey-sites-gpx"));
        var features = new List<IFeature>();
        await foreach (var feature in GpxFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
        {
            features.Add(feature);
        }

        features.Should().HaveCount(3);
        for (var i = 0; i < ExpectedSites.Length; i++)
        {
            var point = features[i].Geometry.Should().BeOfType<Point>().Subject;
            point.X.Should().Be(ExpectedSites[i].X, "GPX lon is the X ordinate");
            point.Y.Should().Be(ExpectedSites[i].Y, "GPX lat is the Y ordinate");
        }
    }

    [Fact]
    public async Task CsvFormatReader_GdalAuthoredWktColumn_PreservesEveryOrdinateAndUtf8Attribute()
    {
        await using var stream = File.OpenRead(Corpus.ResolveVerifiedPath("survey-sites-csv"));
        var features = new List<IFeature>();
        await foreach (var feature in CsvFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
        {
            features.Add(feature);
        }

        features.Should().HaveCount(3);
        for (var i = 0; i < ExpectedSites.Length; i++)
        {
            var point = features[i].Geometry.Should().BeOfType<Point>().Subject;
            point.X.Should().Be(ExpectedSites[i].X);
            point.Y.Should().Be(ExpectedSites[i].Y);
            features[i].Attributes["site_name"].Should().Be(ExpectedSites[i].Name);
        }
    }

    /// <summary>
    /// The first successful FileGDB read in the repository's history: both committed
    /// <c>.gdb</c> fixtures made the happy path structurally untestable (one is multi-layer, which
    /// the reader rejects by design; the other has no feature classes), so every FileGDB assertion
    /// asserted a failure. This fixture is a single feature class written by GDAL's OpenFileGDB
    /// driver (honua-server#4419).
    /// </summary>
    [Fact]
    public async Task FileGdbReader_SingleFeatureClass_StreamsEveryFeatureWithExactOrdinates()
    {
        using var extracted = ExtractCorpusZip("survey-sites-gdb");
        var gdbPath = Path.Join(extracted.Path, "survey_sites.gdb");
        Directory.Exists(gdbPath).Should().BeTrue();

        var features = new List<IFeature>();
        await foreach (var feature in FileGdbReader.ReadStreamingAsync(gdbPath, CancellationToken.None))
        {
            features.Add(feature);
        }

        features.Should().HaveCount(3, "the feature class holds the three source points");
        for (var i = 0; i < ExpectedSites.Length; i++)
        {
            var point = features[i].Geometry.Should().BeOfType<Point>().Subject;
            // FileGDB stores coordinates as scaled integers on a fixed grid, so the round trip is
            // exact only to the grid resolution (~1e-9 degrees here). The tolerance is still eight
            // orders of magnitude tighter than any transposition, sign error or truncated digit.
            point.X.Should().BeApproximately(ExpectedSites[i].X, 1e-7);
            point.Y.Should().BeApproximately(ExpectedSites[i].Y, 1e-7);
            features[i].Attributes["site_name"].Should().Be(ExpectedSites[i].Name);
        }
    }

    [Fact]
    public void FileGdbReader_SingleFeatureClass_DetectsTheDeclaredSrid()
    {
        using var extracted = ExtractCorpusZip("survey-sites-gdb");

        FileGdbReader.DetectSrid(Path.Join(extracted.Path, "survey_sites.gdb"))
            .Should().Be(4326, "the feature class was written with -a_srs EPSG:4326");
    }

    private static async Task<List<IFeature>> ReadKmlAsync(string assetId)
    {
        await using var stream = File.OpenRead(Corpus.ResolveVerifiedPath(assetId));
        var features = new List<IFeature>();
        await foreach (var feature in KmlFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
        {
            features.Add(feature);
        }

        return features;
    }

    private static TempDirectory ExtractCorpusZip(string assetId)
    {
        var directory = new TempDirectory();
        ZipFile.ExtractToDirectory(Corpus.ResolveVerifiedPath(assetId), directory.Path);
        return directory;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            // Both segments after GetTempPath() are fixed literals / a GUID, never rooted.
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), "honua-external-corpus", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a leaked temp directory must never fail a passing test.
            }
        }
    }
}
