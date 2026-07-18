// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Apache.Arrow;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Server↔SDK GeoParquet interop conformance lane (honua-server#2845, part of #2842).
///
/// <para>
/// This suite locks the GeoParquet wire contract that <c>honua-sdk-js</c> (and any standard
/// GeoParquet reader) depends on. It proves the server's emitted output actually interoperates
/// with a real reader rather than only round-tripping through the server's own encoder:
/// </para>
/// <list type="bullet">
///   <item>the emitted <c>geo</c> metadata validates against the authoritative
///     <see href="https://geoparquet.org/releases/v1.1.0/schema.json">GeoParquet 1.1.0 JSON Schema</see>,
///     whose <c>crs</c> field is validated in turn against the bundled
///     <see href="https://proj.org/schemas/v0.7/projjson.schema.json">PROJJSON v0.7 schema</see>;</item>
///   <item>the SDK-relied fields (version, primary_column, encoding, geometry_types, crs,
///     covering.bbox) are present and pinned so silent drift fails loudly;</item>
///   <item>the documented constraints (encoding=WKB, XY/XYZ only, M stripped) are locked;</item>
///   <item>geometry (WKB), CRS (PROJJSON), and attributes round-trip with correct (x, y) axis
///     order through an independent Arrow/Parquet reader.</item>
/// </list>
///
/// <para>
/// The reference reader used here is ParquetSharp's native Arrow reader plus NetTopologySuite's
/// WKB reader. The complementary live-server round-trip through the real <c>geopandas</c> /
/// <c>pyarrow</c> / <c>pyproj</c> stack lives in
/// <c>tests/python/feature_server/test_geoparquet_interop.py</c>, cross-linked to the SDK reader
/// tracking issue (honua-sdk-js#630).
/// </para>
/// </summary>
public sealed class GeoParquetSdkInteropConformanceTests
{
    private static readonly JSchema GeoParquetSchema = LoadGeoParquetSchema();

    // ---------------------------------------------------------------------
    // Schema conformance (AC: validate output against the GeoParquet 1.1 schema)
    // ---------------------------------------------------------------------

    [Fact]
    public void GeoParquet_Wgs84Output_GeoMetadataValidatesAgainstGeoParquet11Schema()
    {
        var geo = ExtractGeoMetadata(WritePoint(-157.8583, 21.3069, srid: 4326, outputSrid: 4326));

        AssertValidGeoParquetMetadata(geo);
    }

    [Fact]
    public void GeoParquet_Non4326Output_GeoMetadataAndProjJsonCrsValidateAgainstSchema()
    {
        // Web Mercator easting/northing; the emitted geo.crs carries authoritative PROJJSON that
        // must validate against the PROJJSON v0.7 schema referenced by the GeoParquet crs field.
        var geo = ExtractGeoMetadata(WritePoint(-14000.0, 6711000.0, srid: 3857, outputSrid: 3857));

        AssertValidGeoParquetMetadata(geo);

        using var doc = JsonDocument.Parse(geo);
        var crs = doc.RootElement.GetProperty("columns").GetProperty("geometry").GetProperty("crs");
        crs.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
        crs.GetProperty("id").GetProperty("authority").GetString().Should().Be("EPSG");
        crs.GetProperty("id").GetProperty("code").GetInt32().Should().Be(3857);
    }

    [Fact]
    public void GeoParquet_EmptyResult_GeoMetadataValidatesAgainstSchema()
    {
        var layer = CreateLayer(Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));
        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Empty(), layer,
            returnGeometry: true, outputSrid: 4326, returnZ: false, returnM: false, new GeometryLimits());

        var geo = ExtractGeoMetadata(payload);
        AssertValidGeoParquetMetadata(geo);

        // Spec §4.1: an empty file must still be schema-valid with geometry_types: [].
        using var doc = JsonDocument.Parse(geo);
        doc.RootElement.GetProperty("columns").GetProperty("geometry")
            .GetProperty("geometry_types").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public void GeoParquet_XyzOutput_GeoMetadataValidatesAndAdvertisesZ()
    {
        var layer = CreateLayer(Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));
        var feature = Feature.Create(
            1,
            new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: false)
                .Write(new Point(new CoordinateZ(1.0, 2.0, 3.0)) { SRID = 4326 }),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]), layer,
            returnGeometry: true, outputSrid: 4326, returnZ: true, returnM: false, new GeometryLimits());

        var geo = ExtractGeoMetadata(payload);
        AssertValidGeoParquetMetadata(geo);

        using var doc = JsonDocument.Parse(geo);
        doc.RootElement.GetProperty("columns").GetProperty("geometry")
            .GetProperty("geometry_types").EnumerateArray().First().GetString()
            .Should().Be("Point Z", "XYZ geometry must advertise the ' Z' suffix per the SDK contract");
    }

    // ---------------------------------------------------------------------
    // SDK-relied field contract (version, primary_column, encoding, geometry_types, covering.bbox)
    // ---------------------------------------------------------------------

    [Fact]
    public void GeoParquet_SdkReliedFields_ArePresentAndPinned()
    {
        var geo = ExtractGeoMetadata(WritePoint(-157.8583, 21.3069, srid: 4326, outputSrid: 4326));

        using var doc = JsonDocument.Parse(geo);
        var root = doc.RootElement;

        // Top-level fields the SDK reads to locate the geometry column.
        root.GetProperty("version").GetString().Should().Be("1.1.0");
        root.GetProperty("primary_column").GetString().Should().Be("geometry");

        var geomCol = root.GetProperty("columns").GetProperty("geometry");
        geomCol.GetProperty("encoding").GetString().Should().Be("WKB");
        geomCol.GetProperty("geometry_types").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("Point");

        // covering.bbox maps each ordinate onto the [column, field] path of the bbox struct so the
        // SDK can prune row groups without decoding geometry (GeoParquet 1.1 spatial pruning).
        var coveringBbox = geomCol.GetProperty("covering").GetProperty("bbox");
        AssertCoveringPath(coveringBbox, "xmin");
        AssertCoveringPath(coveringBbox, "ymin");
        AssertCoveringPath(coveringBbox, "xmax");
        AssertCoveringPath(coveringBbox, "ymax");

        // EPSG:4326 output omits crs (implies OGC:CRS84 / lon-lat) per the SDK contract.
        geomCol.TryGetProperty("crs", out _).Should().BeFalse();
    }

    // ---------------------------------------------------------------------
    // Documented constraints (encoding=WKB, XY/XYZ, M stripped)
    // ---------------------------------------------------------------------

    [Fact]
    public void GeoParquet_ReturnM_IsRejectedSoTheXyXyzConstraintFailsLoudly()
    {
        var layer = CreateLayer(Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));
        var feature = Feature.Create(
            1,
            new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: true)
                .Write(new Point(new CoordinateZM(1.0, 2.0, 3.0, 4.0)) { SRID = 4326 }),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());

        var act = () => GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]), layer,
            returnGeometry: true, outputSrid: 4326, returnZ: true, returnM: true, new GeometryLimits());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*GeoParquet*returnM=true*XY and XYZ*");
    }

    [Fact]
    public async Task GeoParquet_MOrdinate_IsStrippedFromEmittedWkb()
    {
        var layer = CreateLayer(Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));
        var feature = Feature.Create(
            1,
            new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: true)
                .Write(new Point(new CoordinateZM(1.0, 2.0, 3.0, 4.0)) { SRID = 4326 }),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());

        // returnZ=true, returnM=false: Z retained, M dropped.
        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]), layer,
            returnGeometry: true, outputSrid: 4326, returnZ: true, returnM: false, new GeometryLimits());

        var point = (Point)await ReadFirstGeometryAsync(payload);
        point.Coordinate.Z.Should().Be(3.0);
        double.IsNaN(point.Coordinate.M).Should().BeTrue("M must be stripped for GeoParquet 1.1 XY/XYZ output");
    }

    // ---------------------------------------------------------------------
    // Reference-reader round-trip fidelity (geometry, axis order, attributes)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GeoParquet_Non4326_RoundTripsCoordinatesInXyAxisOrder()
    {
        const double easting = -14000.0;
        const double northing = 6711000.0;

        var payload = WritePoint(easting, northing, srid: 3857, outputSrid: 3857);
        var point = (Point)await ReadFirstGeometryAsync(payload);

        // The GeoParquet spec stores coordinates in (x, y) order, overriding the CRS axis order,
        // so easting maps to X and northing to Y regardless of EPSG:3857's declared axes.
        point.X.Should().BeApproximately(easting, 1e-6);
        point.Y.Should().BeApproximately(northing, 1e-6);
    }

    [Fact]
    public async Task GeoParquet_PerRowBbox_MatchesGeometryEnvelopeForReferenceReader()
    {
        var layer = CreateLayer(Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));
        var line = new LineString([new Coordinate(-122.5, 37.1), new Coordinate(-122.1, 37.9)]) { SRID = 4326 };
        var feature = Feature.Create(
            1, new WKBWriter().Write(line),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]), layer,
            returnGeometry: true, outputSrid: 4326, returnZ: false, returnM: false, new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        var bbox = (StructArray)batch!.Column("bbox");
        var envelope = line.EnvelopeInternal;
        ((DoubleArray)bbox.Fields[0]).GetValue(0).Should().Be(envelope.MinX);
        ((DoubleArray)bbox.Fields[1]).GetValue(0).Should().Be(envelope.MinY);
        ((DoubleArray)bbox.Fields[2]).GetValue(0).Should().Be(envelope.MaxX);
        ((DoubleArray)bbox.Fields[3]).GetValue(0).Should().Be(envelope.MaxY);
    }

    [Fact]
    public async Task GeoParquet_Attributes_RoundTripThroughReferenceReader()
    {
        var layer = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            Field("name", MetadataV2FieldType.String, length: 255),
            Field("population", MetadataV2FieldType.Integer),
            Field("area", MetadataV2FieldType.Double));
        var feature = Feature.Create(
            1,
            new WKBWriter().Write(new Point(-157.8583, 21.3069) { SRID = 4326 }),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["name"] = "Honolulu Harbor",
                ["population"] = 350000,
                ["area"] = 12.5
            }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]), layer,
            returnGeometry: true, outputSrid: 4326, returnZ: false, returnM: false, new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        ((Int64Array)batch!.Column("objectid")).GetValue(0).Should().Be(1L);
        ((StringArray)batch.Column("name")).GetString(0).Should().Be("Honolulu Harbor");
        ((Int32Array)batch.Column("population")).GetValue(0).Should().Be(350000);
        ((DoubleArray)batch.Column("area")).GetValue(0).Should().Be(12.5);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static byte[] WritePoint(double x, double y, int srid, int outputSrid)
    {
        var layer = CreateLayer(Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));
        var feature = Feature.Create(
            1,
            new WKBWriter().Write(new Point(x, y) { SRID = srid }),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]), layer,
            returnGeometry: true, outputSrid: outputSrid, returnZ: false, returnM: false, new GeometryLimits());
        return payload;
    }

    private static string ExtractGeoMetadata(byte[] payload)
    {
        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        reader.Schema.Metadata.Should().ContainKey("geo");
        return reader.Schema.Metadata["geo"];
    }

    private static async Task<Geometry> ReadFirstGeometryAsync(byte[] payload)
    {
        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();
        var geometryColumn = (BinaryArray)batch!.Column("geometry");
        return new WKBReader().Read(geometryColumn.GetBytes(0).ToArray());
    }

    private static void AssertValidGeoParquetMetadata(string geoJson)
    {
        var token = JToken.Parse(geoJson);
        var valid = token.IsValid(GeoParquetSchema, out IList<string> errors);
        valid.Should().BeTrue(
            "emitted geo metadata must conform to the GeoParquet 1.1.0 schema; errors: {0}",
            string.Join("; ", errors));
    }

    private static void AssertCoveringPath(JsonElement coveringBbox, string ordinate)
    {
        coveringBbox.GetProperty(ordinate).EnumerateArray().Select(e => e.GetString())
            .Should().Equal("bbox", ordinate);
    }

    private static JSchema LoadGeoParquetSchema()
    {
        var geoParquetSchemaText = ReadSchemaAsset("geoparquet-v1.1.0.schema.json");
        var projJsonSchemaText = ReadSchemaAsset("projjson-v0.7.schema.json");

        // Resolve the external PROJJSON $ref offline so crs validation never touches the network.
        var resolver = new JSchemaPreloadedResolver();
        resolver.Add(new Uri("https://proj.org/schemas/v0.7/projjson.schema.json"), projJsonSchemaText);
        return JSchema.Parse(geoParquetSchemaText, resolver);
    }

    private static string ReadSchemaAsset(string fileName)
    {
        var matches = Directory.GetFiles(AppContext.BaseDirectory, fileName, SearchOption.AllDirectories);
        matches.Should().NotBeEmpty("conformance schema asset '{0}' must be copied to the test output", fileName);
        return File.ReadAllText(matches[0]);
    }

    private static MetadataV2Resource CreateLayer(params MetadataV2Field[] fields)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "test-layer",
                Name = "test_layer",
                Description = "Test Layer"
            },
            SchemaFields =
            [
                .. fields,
                new MetadataV2Field
                {
                    Name = "shape",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = true,
                    SemanticRoles = ["geometry.primary"]
                }
            ],
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape"
            }
        };

    private static MetadataV2Field Field(
        string name,
        MetadataV2FieldType type,
        int? length = null,
        bool nullable = true,
        string? sqlType = null)
        => new()
        {
            Name = name,
            Type = type,
            Length = length,
            Nullable = nullable,
            SqlType = sqlType
        };
}
