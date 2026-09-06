// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using Apache.Arrow;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// Verifies OData reaches parity with the GeoServices f=parquet surface by emitting
/// GeoParquet through the shared cloud-native writer for <c>$format=parquet</c> (issue #1621).
/// </summary>
/// <remarks>
/// honua-server#4396: these tests already pay for a real Testcontainers PostGIS and a real HTTP
/// request, and used to assert only that the payload begins and ends with <c>PAR1</c>. A response
/// that was structurally a Parquet file but carried the wrong rows, the wrong geometry, or no
/// <c>geo</c> metadata passed. They now decode the response with an independent reader —
/// ParquetSharp's Arrow reader plus NetTopologySuite's WKB reader — and assert the exact object
/// ids and geometry seeded by <c>tests/seed/odata.yaml</c>.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataParquetFormatTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;
    private const string ParquetContentType = "application/vnd.apache.parquet";

    /// <summary>
    /// Object ids and point geometries as seeded by <c>tests/seed/odata.yaml</c>. These are the
    /// oracle: they are read from the seed, not from a previous run's output.
    /// </summary>
    private static readonly (long ObjectId, string Name, string State, double X, double Y)[] SeededCities =
    [
        (1, "San Francisco", "California", -122.4194, 37.7749),
        (2, "Los Angeles", "California", -118.2437, 34.0522),
        (3, "Sacramento", "California", -121.4944, 38.5816),
        (4, "San Diego", "California", -117.1611, 32.7157),
        (5, "San Jose", "California", -121.8863, 37.3382),
        (6, "Seattle", "Washington", -122.3321, 47.6062),
    ];

    public async Task InitializeAsync()
    {
        // All segments are relative literal path fragments (not user input), so none can be
        // rooted and silently drop earlier arguments.
        _fixture.UseSeed(Path.Join("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$format=parquet")]
    public async Task Features_FormatParquet_ReturnsGeoParquetPayload()
    {
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$format=parquet");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(ParquetContentType);

        var payload = await response.Content.ReadAsByteArrayAsync();
        AssertParquetFraming(payload);

        var decoded = await DecodeAsync(payload);

        // The response must carry the seeded features, not merely be a well-formed Parquet file.
        foreach (var city in SeededCities)
        {
            var row = decoded.Rows.Should().ContainSingle(r => r.ObjectId == city.ObjectId,
                "the seeded feature {0} must be present in the served GeoParquet", city.Name).Subject;
            row.Name.Should().Be(city.Name);
            row.State.Should().Be(city.State);
            AssertPoint(row.Geometry, city.X, city.Y);
        }

        // Feature 13 ("Virtual City") is seeded with no geometry; a reader must see a null
        // geometry rather than an empty or default one.
        var nullGeometryRow = decoded.Rows.Should().ContainSingle(r => r.ObjectId == 13).Subject;
        nullGeometryRow.Geometry.Should().BeNull("the seeded geometry-less feature must decode as null");

        AssertGeoMetadata(decoded.GeoMetadata);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$format=parquet&$filter=...")]
    public async Task Features_FormatParquetWithFilter_ReturnsFilteredGeoParquetPayload()
    {
        var filter = "state in ('California','Washington')";
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$format=parquet&$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(ParquetContentType);

        var payload = await response.Content.ReadAsByteArrayAsync();
        AssertParquetFraming(payload);

        var decoded = await DecodeAsync(payload);

        // The six California/Washington cities and nothing else. A filter that was ignored would
        // return all 15+ seeded rows and a filter that over-matched would drop one of these.
        decoded.Rows.Select(row => row.ObjectId).Should().BeEquivalentTo(
            SeededCities.Select(city => city.ObjectId),
            "the OData $filter must be applied to the GeoParquet projection");
        decoded.Rows.Should().OnlyContain(row => row.State == "California" || row.State == "Washington");

        var seattle = decoded.Rows.Single(row => row.ObjectId == 6);
        AssertPoint(seattle.Geometry, -122.3321, 47.6062);

        AssertGeoMetadata(decoded.GeoMetadata);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})?$format=parquet&bbox=...")]
    public async Task Features_FormatParquetWithBbox_ReturnsGeoParquetPayload()
    {
        // A California-only window: it excludes Seattle (47.6N) and Portland (45.5N) to the
        // north and everything east of -114.
        var response = await _fixture.Client.GetAsync(
            $"/odata/Features({TestLayerId})?$format=parquet&bbox=-124,32,-114,42");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(ParquetContentType);

        var payload = await response.Content.ReadAsByteArrayAsync();
        AssertParquetFraming(payload);

        var decoded = await DecodeAsync(payload);

        decoded.Rows.Should().NotBeEmpty();
        decoded.Rows.Select(row => row.ObjectId).Should().Contain([1L, 2L, 3L, 4L, 5L],
            "every seeded California city falls inside the requested window");
        decoded.Rows.Select(row => row.ObjectId).Should().NotContain(6L,
            "Seattle is north of the requested window and must be filtered out");

        // Every returned geometry must genuinely be inside the requested envelope.
        foreach (var row in decoded.Rows.Where(row => row.Geometry is not null))
        {
            var point = (Point)row.Geometry!;
            point.X.Should().BeInRange(-124, -114);
            point.Y.Should().BeInRange(32, 42);
        }

        AssertGeoMetadata(decoded.GeoMetadata);
    }

    private static void AssertParquetFraming(byte[] payload)
    {
        payload.Should().NotBeEmpty();
        Encoding.ASCII.GetString(payload, 0, 4).Should().Be("PAR1");
        Encoding.ASCII.GetString(payload, payload.Length - 4, 4).Should().Be("PAR1");
    }

    /// <summary>
    /// Asserts the emitted <c>geo</c> key names a geometry column, declares WKB encoding and
    /// carries a CRS. A GeoParquet file without this is a plain Parquet file: an independent
    /// consumer would not know the binary column holds geometry.
    /// </summary>
    private static void AssertGeoMetadata(string? geoMetadata)
    {
        geoMetadata.Should().NotBeNullOrWhiteSpace(
            "a GeoParquet response must carry the 'geo' schema metadata key");

        using var document = JsonDocument.Parse(geoMetadata!);
        var root = document.RootElement;

        var primaryColumn = root.GetProperty("primary_column").GetString();
        primaryColumn.Should().NotBeNullOrWhiteSpace();

        var column = root.GetProperty("columns").GetProperty(primaryColumn!);
        column.GetProperty("encoding").GetString().Should().Be("WKB");
        column.TryGetProperty("crs", out _).Should().BeTrue(
            "an independent consumer needs the CRS to place the coordinates");
    }

    private static void AssertPoint(Geometry? geometry, double expectedX, double expectedY)
    {
        geometry.Should().BeOfType<Point>();
        var point = (Point)geometry!;
        point.X.Should().BeApproximately(expectedX, 1e-9);
        point.Y.Should().BeApproximately(expectedY, 1e-9);
    }

    /// <summary>
    /// Reads the served payload with ParquetSharp's Arrow reader and NetTopologySuite's WKB
    /// reader — neither of which shares code with the writer under test.
    /// </summary>
    private static async Task<DecodedParquet> DecodeAsync(byte[] payload)
    {
        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);

        reader.Schema.Metadata.TryGetValue("geo", out var geoMetadata);

        var wkbReader = new WKBReader();
        var rows = new List<DecodedRow>();

        using var batchReader = reader.GetRecordBatchReader();
        while (await batchReader.ReadNextRecordBatchAsync() is { } batch)
        {
            using (batch)
            {
                var objectIds = (Int64Array)batch.Column("objectid");
                var names = (StringArray)batch.Column("name");
                var states = (StringArray)batch.Column("state");
                var geometries = (BinaryArray)batch.Column("geometry");

                for (var index = 0; index < batch.Length; index++)
                {
                    var wkb = geometries.IsNull(index) ? null : geometries.GetBytes(index).ToArray();
                    rows.Add(new DecodedRow(
                        objectIds.GetValue(index)!.Value,
                        names.IsNull(index) ? null : names.GetString(index),
                        states.IsNull(index) ? null : states.GetString(index),
                        wkb is null || wkb.Length == 0 ? null : wkbReader.Read(wkb)));
                }
            }
        }

        return new DecodedParquet(rows, geoMetadata);
    }

    private sealed record DecodedParquet(IReadOnlyList<DecodedRow> Rows, string? GeoMetadata);

    private sealed record DecodedRow(long ObjectId, string? Name, string? State, Geometry? Geometry);
}
