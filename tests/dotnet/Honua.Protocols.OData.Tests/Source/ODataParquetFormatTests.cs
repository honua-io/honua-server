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
            $"/odata/Features({TestLayerId})?$format=parquet&$top=100");

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

        decoded.Rows.Select(row => row.ObjectId).Should().BeEquivalentTo(
            Enumerable.Range(1, 15).Select(id => (long)id));
        var nullGeometryRow = decoded.Rows.Single(row => row.ObjectId == 13);
        nullGeometryRow.Name.Should().Be("Virtual City");
        nullGeometryRow.State.Should().BeNull();
        nullGeometryRow.Geometry.Should().BeNull();

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

        // The window includes Las Vegas as well as all five California cities.
        decoded.Rows.Select(row => row.ObjectId).Should().BeEquivalentTo([1L, 2L, 3L, 4L, 5L, 11L]);
        AssertPoint(decoded.Rows.Single(row => row.ObjectId == 11).Geometry, -115.1398, 36.1699);

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
    /// resolves to a CRS an independent consumer can use. A GeoParquet file without this is a
    /// plain Parquet file: a consumer would not know the binary column holds geometry.
    /// </summary>
    private static void AssertGeoMetadata(string? geoMetadata)
    {
        geoMetadata.Should().NotBeNullOrWhiteSpace(
            "a GeoParquet response must carry the 'geo' schema metadata key");

        using var document = JsonDocument.Parse(geoMetadata!);
        var root = document.RootElement;

        root.GetProperty("version").GetString().Should().Be("1.1.0");
        var primaryColumn = root.GetProperty("primary_column").GetString();
        primaryColumn.Should().Be("geometry");

        var column = root.GetProperty("columns").GetProperty(primaryColumn!);
        column.GetProperty("encoding").GetString().Should().Be("WKB");

        // GeoParquet 1.1 makes the column `crs` member optional and defines its *absence* as
        // the default CRS, OGC:CRS84 (longitude, latitude). GeoParquetFeatureWriter relies on
        // that: ResolveGeoParquetCrsProjJson returns null for EPSG:4326 output, so the writer
        // deliberately omits `crs` — the OData layer here is SRID 4326, so a missing member is
        // the CRS declaration, not a gap. Only an explicitly emitted (non-default) CRS carries
        // PROJJSON, and that is what has to be readable.
        if (column.TryGetProperty("crs", out var crs))
        {
            crs.ValueKind.Should().Be(JsonValueKind.Object,
                "an explicit GeoParquet 'crs' must be a PROJJSON object, not a bare string or null");
            crs.TryGetProperty("type", out var crsType).Should().BeTrue(
                "PROJJSON requires a 'type' member for a reader to reconstruct the CRS");
            crsType.GetString().Should().Be("GeographicCRS");
            crs.GetProperty("id").GetProperty("authority").GetString().Should().Be("EPSG");
            crs.GetProperty("id").GetProperty("code").GetInt32().Should().Be(4326);
        }
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

        var wkbReader = new WKBReader();
        var rows = new List<DecodedRow>();

        using var batchReader = reader.GetRecordBatchReader();
        while (await batchReader.ReadNextRecordBatchAsync() is { } batch)
        {
            using (batch)
            {
                var objectIds = RequireColumn<Int64Array>(batch, "objectid");
                var names = RequireColumn<StringArray>(batch, "name");
                var states = RequireColumn<StringArray>(batch, "state");
                var geometries = RequireColumn<BinaryArray>(batch, "geometry");

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

        // The Arrow reader only surfaces the file's key-value metadata on its schema once a batch
        // has been read (the same "materialize schema" order the FeatureServer f=parquet tests
        // use); read before that, Schema.Metadata is null. Stay null-safe so a file that truly
        // carries no 'geo' key fails AssertGeoMetadata with that message rather than an NRE.
        string? geoMetadata = null;
        reader.Schema.Metadata?.TryGetValue("geo", out geoMetadata);

        return new DecodedParquet(rows, geoMetadata);
    }

    /// <summary>
    /// Resolves a named column, failing with the columns the file actually carries rather than
    /// Arrow's bare index-out-of-range when the served schema does not name it.
    /// </summary>
    private static TArray RequireColumn<TArray>(RecordBatch batch, string name)
        where TArray : IArrowArray
    {
        var index = batch.Schema.GetFieldIndex(name);
        index.Should().BeGreaterThanOrEqualTo(0,
            "the served GeoParquet must carry column '{0}' (it carries: {1})",
            name,
            string.Join(", ", batch.Schema.FieldsList.Select(field => field.Name)));

        var column = batch.Column(index);
        column.Should().BeOfType<TArray>("column '{0}' must decode as {1}", name, typeof(TArray).Name);
        return (TArray)column;
    }

    private sealed record DecodedParquet(IReadOnlyList<DecodedRow> Rows, string? GeoMetadata);

    private sealed record DecodedRow(long ObjectId, string? Name, string? State, Geometry? Geometry);
}
