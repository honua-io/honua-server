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

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class GeoParquetQueryFormatterTests
{
    [Fact]
    public async Task FormatAsGeoParquet_WithFeatures_WritesReadableParquetWithGeoMetadata()
    {
        var layer = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            Field("name", MetadataV2FieldType.String, length: 255),
            Field("population", MetadataV2FieldType.Integer));
        var feature = Feature.Create(
            1,
            CreatePointWkb(-157.8583, 21.3069),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["name"] = "Honolulu Harbor",
                ["population"] = 350000
            }.ToImmutableDictionary());

        var (payload, contentType) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        contentType.Should().Be("application/vnd.apache.parquet");
        payload.Should().NotBeEmpty();

        // Verify magic bytes: Parquet files start with "PAR1"
        payload[0].Should().Be((byte)'P');
        payload[1].Should().Be((byte)'A');
        payload[2].Should().Be((byte)'R');
        payload[3].Should().Be((byte)'1');

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        batch!.Length.Should().Be(1);
        batch.Schema.FieldsList.Select(f => f.Name)
            .Should().Equal("objectid", "geometry", "name", "population", "bbox");

        reader.Schema.Metadata.Should().ContainKey("geo");
        using var geoDoc = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
        geoDoc.RootElement.GetProperty("version").GetString().Should().Be("1.1.0");
        geoDoc.RootElement.GetProperty("primary_column").GetString().Should().Be("geometry");
    }

    [Fact]
    public async Task FormatAsGeoParquet_WithMetadataV2Resource_WritesReadableParquetWithGeoMetadata()
    {
        var resource = CreateResource(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            Field("name", MetadataV2FieldType.String, length: 255),
            Field("population", MetadataV2FieldType.Integer));
        var feature = Feature.Create(
            1,
            CreatePointWkb(-157.8583, 21.3069),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["name"] = "Honolulu Harbor",
                ["population"] = 350000
            }.ToImmutableDictionary());

        var (payload, contentType) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            resource,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        contentType.Should().Be("application/vnd.apache.parquet");
        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        batch!.Length.Should().Be(1);
        batch.Schema.FieldsList.Select(f => f.Name)
            .Should().Equal("objectid", "geometry", "name", "population", "bbox");

        reader.Schema.Metadata.Should().ContainKey("geo");
        using var geoDoc = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
        geoDoc.RootElement.GetProperty("version").GetString().Should().Be("1.1.0");
        geoDoc.RootElement.GetProperty("primary_column").GetString().Should().Be("geometry");
    }

    [Fact]
    public void FormatAsGeoParquet_EmptyResult_ReturnsValidParquetWithEmptyGeometryTypes()
    {
        var layer = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            Field("name", MetadataV2FieldType.String, length: 255));

        var (payload, contentType) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Empty(),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        contentType.Should().Be("application/vnd.apache.parquet");
        payload.Should().NotBeEmpty();

        // Verify magic bytes: Parquet files start with "PAR1"
        payload[0].Should().Be((byte)'P');
        payload[1].Should().Be((byte)'A');
        payload[2].Should().Be((byte)'R');
        payload[3].Should().Be((byte)'1');

        // GeoParquet 1.1.0 §4.1: geometry_types must be [] for an empty file
        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        reader.Schema.Metadata.Should().ContainKey("geo");
        using var geoDoc = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
        var geomCol = geoDoc.RootElement.GetProperty("columns").GetProperty("geometry");
        geomCol.GetProperty("geometry_types").EnumerateArray().Should().BeEmpty(
            "empty GeoParquet file must have geometry_types:[] per spec §4.1");
    }

    [Fact]
    public async Task FormatAsGeoParquet_AppliesGeometryPrecisionAndDimensionFiltering()
    {
        var layer = CreateLayer(Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));
        var feature = Feature.Create(
            1,
            CreatePointWkbWithZm(1.2349, 2.3451, 3.4567, 4.5678),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L
            }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits { MaxCoordinatePrecision = 2 });

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        var geometryColumn = (BinaryArray)batch!.Column("geometry");
        geometryColumn.IsNull(0).Should().BeFalse();

        var geometry = new WKBReader().Read(geometryColumn.GetBytes(0).ToArray());
        var point = geometry.Should().BeOfType<Point>().Subject;

        point.X.Should().Be(1.23);
        point.Y.Should().Be(2.35);
        double.IsNaN(point.Coordinate.Z).Should().BeTrue();
        double.IsNaN(point.Coordinate.M).Should().BeTrue();
    }

    [Fact]
    public void FormatAsGeoParquet_WithZmGeometryAndReturnMTrue_ThrowsInvalidOperation()
    {
        var layer = CreateLayer(Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));
        var feature = Feature.Create(
            1,
            CreatePointWkbWithZm(1.0, 2.0, 3.0, 4.0),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L
            }.ToImmutableDictionary());

        var act = () => GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: true,
            returnM: true,
            new GeometryLimits());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*GeoParquet*returnM=true*XY and XYZ geometries*");
    }

    [Fact]
    public async Task FormatAsGeoParquet_WithoutGeometry_OmitsGeometryColumn()
    {
        var layer = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            Field("name", MetadataV2FieldType.String, length: 255));
        var feature = Feature.Create(
            1,
            CreatePointWkb(-157.8583, 21.3069),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["name"] = "Test Feature"
            }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: false,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        batch!.Schema.FieldsList.Select(f => f.Name)
            .Should().NotContain("geometry");
        // No geometry means no geo metadata (metadata may be null or empty)
        var hasGeoKey = reader.Schema.Metadata?.ContainsKey("geo") ?? false;
        hasGeoKey.Should().BeFalse();
    }

    [Fact]
    public async Task FormatAsGeoParquet_WithOutFields_IncludesOnlyRequestedFields()
    {
        var layer = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            Field("name", MetadataV2FieldType.String, length: 255),
            Field("population", MetadataV2FieldType.Integer),
            Field("area", MetadataV2FieldType.Double));
        var feature = Feature.Create(
            1,
            CreatePointWkb(0, 0),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["name"] = "Test",
                ["population"] = 100,
                ["area"] = 1.5
            }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits(),
            outFields: ["name"]);

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        batch!.Schema.FieldsList.Select(f => f.Name)
            .Should().Equal("objectid", "geometry", "name", "bbox");
    }

    [Fact]
    public void FormatAsGeoParquet_GeoMetadata_IncludesCrsAndGeometryType()
    {
        var layer = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));
        var feature = Feature.Create(
            1,
            CreatePointWkb(0, 0),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        // Read a batch to populate schema fully
        using var batchReader = reader.GetRecordBatchReader();

        reader.Schema.Metadata.Should().ContainKey("geo");
        using var geoDoc = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
        var columns = geoDoc.RootElement.GetProperty("columns");
        var geomCol = columns.GetProperty("geometry");

        geomCol.GetProperty("encoding").GetString().Should().Be("WKB");
        geomCol.GetProperty("geometry_types").EnumerateArray().First().GetString().Should().Be("Point");

        // GeoParquet 1.1.0: omitting `crs` key implies OGC:CRS84 (WGS84 lon/lat)
        geomCol.TryGetProperty("crs", out _).Should().BeFalse("EPSG:4326 should omit crs key (implies OGC:CRS84)");
    }

    [Fact]
    public async Task FormatAsGeoParquet_WithDateTimeField_MapsToTimestampType()
    {
        var layer = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            Field("updated_at", MetadataV2FieldType.DateTime));
        var timestamp = new DateTime(2024, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var feature = Feature.Create(
            1,
            CreatePointWkb(0, 0),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["updated_at"] = timestamp
            }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        var updatedAtField = batch!.Schema.GetFieldByName("updated_at");
        updatedAtField.DataType.Should().BeOfType<Apache.Arrow.Types.TimestampType>();
    }

    [Fact]
    public async Task FormatAsGeoParquet_WithUnspecifiedDateTimeKind_TreatsAsUtc()
    {
        var layer = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            Field("created_at", MetadataV2FieldType.DateTime));
        // DateTimeKind.Unspecified — common for PostgreSQL "timestamp without time zone"
        var unspecifiedTimestamp = new DateTime(2024, 6, 15, 12, 30, 0, DateTimeKind.Unspecified);
        var feature = Feature.Create(
            1,
            CreatePointWkb(0, 0),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["created_at"] = unspecifiedTimestamp
            }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        var tsArray = (TimestampArray)batch!.Column("created_at");
        var storedValue = tsArray.GetTimestamp(0);
        // Must match the original value interpreted as UTC, not shifted by local timezone
        storedValue.Should().NotBeNull();
        storedValue!.Value.DateTime.Should().Be(unspecifiedTimestamp);
    }

    [Fact]
    public async Task FormatAsGeoParquet_WithEpochMillisecondTemporalValues_PreservesDateAndTimestampColumns()
    {
        var layer = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            Field("updated_at", MetadataV2FieldType.DateTime),
            Field("event_date", MetadataV2FieldType.Date));
        const long updatedAtEpochMs = 1718454600000;
        const long eventDateEpochMs = 1718409600000;
        var feature = Feature.Create(
            1,
            CreatePointWkb(0, 0),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["updated_at"] = updatedAtEpochMs,
                ["event_date"] = eventDateEpochMs
            }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();

        var timestampArray = (TimestampArray)batch!.Column("updated_at");
        timestampArray.GetTimestamp(0)!.Value.ToUnixTimeMilliseconds().Should().Be(updatedAtEpochMs);

        var dateArray = (Date32Array)batch.Column("event_date");
        var epoch = DateOnly.FromDateTime(DateTime.UnixEpoch);
        var expectedDate = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(eventDateEpochMs).UtcDateTime);
        dateArray.GetValue(0).Should().Be(expectedDate.DayNumber - epoch.DayNumber);
    }

    [Fact]
    public void FormatAsGeoParquet_EmptyResultWithUnresolvableSrid_ThrowsInvalidOperation()
    {
        var layer = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));

        // 987654 has no PROJJSON definition in the catalog, so it cannot carry spec-compliant
        // CRS metadata and must fail with a precise error.
        var act = () => GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Empty(),
            layer,
            returnGeometry: true,
            outputSrid: 987654,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SRID 987654*no PROJJSON*");
    }

    [Fact]
    public async Task FormatAsGeoParquet_WithNon4326Srid_EmitsProjJsonCrs()
    {
        var layer = CreateLayer(Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));
        // Web Mercator easting/northing (x, y) for central London; stored in the WKB in (x, y)
        // order per the GeoParquet spec, which overrides the CRS axis order.
        const double easting = -14000.0;
        const double northing = 6711000.0;
        var feature = Feature.Create(
            1,
            new WKBWriter().Write(new Point(easting, northing) { SRID = 3857 }),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 3857,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        // geo.crs must carry the authoritative PROJJSON for EPSG:3857.
        reader.Schema.Metadata.Should().ContainKey("geo");
        using var geoDoc = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
        var geomCol = geoDoc.RootElement.GetProperty("columns").GetProperty("geometry");
        geomCol.TryGetProperty("crs", out var crs).Should().BeTrue("non-4326 output must carry a PROJJSON crs");
        crs.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
        var id = crs.GetProperty("id");
        id.GetProperty("authority").GetString().Should().Be("EPSG");
        id.GetProperty("code").GetInt32().Should().Be(3857);

        // Coordinates are stored in (x, y) order regardless of the CRS axis order.
        batch.Should().NotBeNull();
        var geometryColumn = (BinaryArray)batch!.Column("geometry");
        var point = new WKBReader().Read(geometryColumn.GetBytes(0).ToArray()).Should().BeOfType<Point>().Subject;
        point.X.Should().BeApproximately(easting, 1e-6);
        point.Y.Should().BeApproximately(northing, 1e-6);
    }

    [Fact]
    public void FormatAsGeoParquet_WithWebMercatorAliasSrid_EmitsEpsg3857Crs()
    {
        var layer = CreateLayer(Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));
        var feature = Feature.Create(
            1,
            new WKBWriter().Write(new Point(0.0, 0.0) { SRID = 102100 }),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());

        // 102100 is the Esri Web Mercator alias; it must resolve to the canonical EPSG:3857 CRS.
        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 102100,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);

        reader.Schema.Metadata.Should().ContainKey("geo");
        using var geoDoc = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
        var geomCol = geoDoc.RootElement.GetProperty("columns").GetProperty("geometry");
        var id = geomCol.GetProperty("crs").GetProperty("id");
        id.GetProperty("authority").GetString().Should().Be("EPSG");
        id.GetProperty("code").GetInt32().Should().Be(3857);
    }

    [Fact]
    public void FormatAsGeoParquet_WithNon4326SridEmptyResult_EmitsProjJsonCrs()
    {
        var layer = CreateLayer(Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Empty(),
            layer,
            returnGeometry: true,
            outputSrid: 25832,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);

        reader.Schema.Metadata.Should().ContainKey("geo");
        using var geoDoc = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
        var geomCol = geoDoc.RootElement.GetProperty("columns").GetProperty("geometry");
        geomCol.GetProperty("geometry_types").EnumerateArray().Should().BeEmpty();
        geomCol.GetProperty("crs").GetProperty("id").GetProperty("code").GetInt32().Should().Be(25832);
    }

    [Fact]
    public void FormatAsGeoParquet_WithUnresolvableSrid_ThrowsPreciseError()
    {
        var layer = CreateLayer(Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));
        var feature = Feature.Create(
            1,
            CreatePointWkb(0, 0),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());

        var act = () => GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 987654,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SRID 987654*no PROJJSON*");
    }

    [Fact]
    public async Task FormatAsGeoParquet_WithRuntimeDistanceAttribute_IncludesDistanceColumn()
    {
        var layer = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            Field("name", MetadataV2FieldType.String, length: 255));
        // Simulate a KNN query result where the Postgres reader injects a "distance" runtime attribute.
        var feature = Feature.Create(
            1,
            CreatePointWkb(-157.8583, 21.3069),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["name"] = "Nearest Point",
                ["distance"] = 42.5 // runtime-computed by ST_Distance
            }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        batch!.Schema.FieldsList.Select(f => f.Name)
            .Should().Contain("distance");
        var distanceArray = (DoubleArray)batch.Column("distance");
        distanceArray.GetValue(0).Should().BeApproximately(42.5, 1e-9);
    }

    [Fact]
    public async Task FormatAsGeoParquet_WithRuntimeDistanceOnlyOnLaterRow_IncludesDistanceColumn()
    {
        var layer = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            Field("name", MetadataV2FieldType.String, length: 255));
        var firstFeature = Feature.Create(
            1,
            CreatePointWkb(-157.8583, 21.3069),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["name"] = "First Row"
            }.ToImmutableDictionary());
        var secondFeature = Feature.Create(
            2,
            CreatePointWkb(-157.8580, 21.3071),
            new Dictionary<string, object?>
            {
                ["objectid"] = 2L,
                ["name"] = "Second Row",
                ["distance"] = 42.5
            }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(2, [firstFeature, secondFeature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        batch!.Schema.FieldsList.Select(f => f.Name)
            .Should().Contain("distance");
        var distanceArray = (DoubleArray)batch.Column("distance");
        distanceArray.GetValue(0).Should().BeNull();
        distanceArray.GetValue(1).Should().BeApproximately(42.5, 1e-9);
    }

    [Fact]
    public async Task FormatAsGeoParquet_WithOutFieldsAndRuntimeDistance_ExcludesUnrequestedRuntimeFields()
    {
        var layer = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            Field("name", MetadataV2FieldType.String, length: 255));
        // Simulate a KNN result with a runtime "distance" attribute, but outFields only requests "name".
        var feature = Feature.Create(
            1,
            CreatePointWkb(-157.8583, 21.3069),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["name"] = "Nearest Point",
                ["distance"] = 42.5
            }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits(),
            outFields: ["name"]);

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        batch!.Schema.FieldsList.Select(f => f.Name)
            .Should().Equal("objectid", "geometry", "name", "bbox")
            .And.NotContain("distance",
                "runtime fields not in outFields should be excluded, matching JSON/GeoJSON behavior");
    }

    [Fact]
    public async Task FormatAsGeoParquet_WithBase64ByteaAttribute_DecodesBytes()
    {
        var layer = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            Field("data", MetadataV2FieldType.Binary));
        var rawBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        // Simulate the JSON round-trip: BYTEA values from the JSONB attributes column
        // arrive as base64 strings after deserialization.
        var base64Value = Convert.ToBase64String(rawBytes);
        var feature = Feature.Create(
            1,
            CreatePointWkb(0, 0),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["data"] = base64Value // string, not byte[]
            }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        var dataArray = (BinaryArray)batch!.Column("data");
        dataArray.IsNull(0).Should().BeFalse("base64 string should be decoded to bytes");
        dataArray.GetBytes(0).ToArray().Should().Equal(rawBytes);
    }

    [Fact]
    public async Task FormatAsGeoParquet_WithNativeByteArrayAttribute_PreservesBytes()
    {
        var layer = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            Field("data", MetadataV2FieldType.Binary));
        var rawBytes = new byte[] { 0x01, 0x02, 0x03 };
        var feature = Feature.Create(
            1,
            CreatePointWkb(0, 0),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["data"] = rawBytes // already byte[] (e.g. from direct column read)
            }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        var dataArray = (BinaryArray)batch!.Column("data");
        dataArray.IsNull(0).Should().BeFalse();
        dataArray.GetBytes(0).ToArray().Should().Equal(rawBytes);
    }

    /// <summary>
    /// Guards against the dual-switch sync risk: MapToArrowType and BuildAttributeArray
    /// use independent switch statements over SQL type strings. If they diverge for any type,
    /// the Parquet write or read will fail because the schema declares one Arrow type but
    /// the column data uses another.
    /// Note: The formatter also handles "smallint"/"int2" -> Int16 and "decimal" -> Double
    /// as defensive branches for raw SQL type overrides.
    /// </summary>
    [Fact]
    public async Task FormatAsGeoParquet_AllSqlTypes_SchemaAndBuilderTypesAreConsistent()
    {
        var layer = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            Field("text_field", MetadataV2FieldType.String, sqlType: "TEXT"),
            Field("varchar_field", MetadataV2FieldType.String, length: 100, sqlType: "VARCHAR(100)"),
            Field("int_field", MetadataV2FieldType.Integer, sqlType: "INTEGER"),
            Field("double_field", MetadataV2FieldType.Double, sqlType: "DOUBLE PRECISION"),
            Field("float_field", MetadataV2FieldType.Float, sqlType: "REAL"),
            Field("bool_field", MetadataV2FieldType.Boolean, sqlType: "BOOLEAN"),
            Field("datetime_field", MetadataV2FieldType.DateTime, sqlType: "TIMESTAMP WITH TIME ZONE"),
            Field("date_field", MetadataV2FieldType.Date, sqlType: "DATE"),
            Field("time_field", MetadataV2FieldType.Time, sqlType: "TIME"),
            Field("json_field", MetadataV2FieldType.Json, sqlType: "JSONB"),
            Field("binary_field", MetadataV2FieldType.Binary, sqlType: "BYTEA"),
            Field("uuid_field", MetadataV2FieldType.Uuid, sqlType: "UUID"));

        var feature = Feature.Create(
            1,
            CreatePointWkb(0, 0),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["text_field"] = "hello",
                ["varchar_field"] = "world",
                ["int_field"] = 42,
                ["double_field"] = 3.14,
                ["float_field"] = 1.5f,
                ["bool_field"] = true,
                ["datetime_field"] = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc),
                ["date_field"] = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc),
                ["time_field"] = "14:30:00",
                ["json_field"] = "{\"key\":\"value\"}",
                ["binary_field"] = new byte[] { 0x01, 0x02 },
                ["uuid_field"] = "550e8400-e29b-41d4-a716-446655440000"
            }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        // Read back. A MapToArrowType / BuildAttributeArray mismatch causes
        // InvalidCastException or corrupt column data here.
        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        batch!.Length.Should().Be(1);

        // Verify each column's Arrow type matches its expected schema mapping.
        batch.Column("objectid").Should().BeOfType<Int64Array>();
        batch.Column("geometry").Should().BeOfType<BinaryArray>();
        batch.Column("text_field").Should().BeOfType<StringArray>();
        batch.Column("varchar_field").Should().BeOfType<StringArray>();
        batch.Column("int_field").Should().BeOfType<Int32Array>();
        batch.Column("double_field").Should().BeOfType<DoubleArray>();
        batch.Column("float_field").Should().BeOfType<FloatArray>();
        batch.Column("bool_field").Should().BeOfType<BooleanArray>();
        batch.Column("datetime_field").Should().BeOfType<TimestampArray>();
        batch.Column("date_field").Should().BeOfType<Date32Array>();
        batch.Column("time_field").Should().BeOfType<Time32Array>();
        batch.Column("json_field").Should().BeOfType<StringArray>();
        batch.Column("binary_field").Should().BeOfType<BinaryArray>();
        batch.Column("uuid_field").Should().BeOfType<StringArray>();

        var timeArray = (Time32Array)batch.Column("time_field");
        timeArray.GetValue(0).Should().Be(14 * 60 * 60 * 1000 + 30 * 60 * 1000);
    }

    [Fact]
    public async Task FormatAsGeoParquet_WithCorruptWkb_TreatsGeometryAsNull()
    {
        var layer = CreateLayer(
            Field("objectid", MetadataV2FieldType.BigInteger, nullable: false),
            Field("name", MetadataV2FieldType.String, length: 255));
        var corruptWkb = new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x02, 0x03 };
        var validFeature = Feature.Create(
            1,
            CreatePointWkb(1.0, 2.0),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L,
                ["name"] = "Valid"
            }.ToImmutableDictionary());
        var corruptFeature = Feature.Create(
            2,
            corruptWkb,
            new Dictionary<string, object?>
            {
                ["objectid"] = 2L,
                ["name"] = "Corrupt"
            }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(2, [validFeature, corruptFeature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        batch!.Length.Should().Be(2);
        var geometryColumn = (BinaryArray)batch.Column("geometry");
        geometryColumn.IsNull(0).Should().BeFalse("valid geometry should be preserved");
        geometryColumn.IsNull(1).Should().BeTrue("corrupt WKB should produce null geometry");
    }

    [Fact]
    public void FormatAsGeoParquet_With2DGeometryAndReturnZTrue_DoesNotAdvertiseZ()
    {
        // A 2D-only layer/feature with returnZ=true must not claim "Point Z" in metadata
        // because the actual WKB payload is 2D.
        var layer = CreateLayer(Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));
        var feature = Feature.Create(
            1,
            CreatePointWkb(1.0, 2.0),
            new Dictionary<string, object?>
            {
                ["objectid"] = 1L
            }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: true,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);

        reader.Schema.Metadata.Should().ContainKey("geo");
        using var geoDoc = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
        var geomCol = geoDoc.RootElement.GetProperty("columns").GetProperty("geometry");
        geomCol.GetProperty("geometry_types").EnumerateArray().First().GetString()
            .Should().Be("Point", "2D geometry should not advertise Z even when returnZ=true");
    }

    [Fact]
    public void FormatAsGeoParquet_GeoMetadata_DeclaresCoveringBbox()
    {
        var layer = CreateLayer(Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));
        var feature = Feature.Create(
            1,
            CreatePointWkb(-157.8583, 21.3069),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);

        reader.Schema.Metadata.Should().ContainKey("geo");
        using var geoDoc = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
        var geomCol = geoDoc.RootElement.GetProperty("columns").GetProperty("geometry");

        // GeoParquet 1.1 covering.bbox maps each ordinate to the [column, field] path of the bbox struct.
        var covering = geomCol.GetProperty("covering").GetProperty("bbox");
        AssertCoveringPath(covering, "xmin");
        AssertCoveringPath(covering, "ymin");
        AssertCoveringPath(covering, "xmax");
        AssertCoveringPath(covering, "ymax");

        // The referenced bbox column exists as a struct with the four ordinate fields.
        var bboxField = reader.Schema.GetFieldByName("bbox");
        bboxField.Should().NotBeNull();
        var structType = bboxField.DataType.Should().BeOfType<Apache.Arrow.Types.StructType>().Subject;
        structType.Fields.Select(f => f.Name).Should().Equal("xmin", "ymin", "xmax", "ymax");
    }

    [Fact]
    public async Task FormatAsGeoParquet_PerRowBbox_EqualsGeometryEnvelope()
    {
        var layer = CreateLayer(Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));
        // A line string so xmin != xmax and ymin != ymax, proving a real envelope is stored.
        var coordinates = new[] { new Coordinate(-122.5, 37.1), new Coordinate(-122.1, 37.9) };
        var line = new LineString(coordinates) { SRID = 4326 };
        var feature = Feature.Create(
            1,
            new WKBWriter().Write(line),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(1, [feature]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        var bbox = (StructArray)batch!.Column("bbox");
        bbox.IsNull(0).Should().BeFalse();

        var expected = line.EnvelopeInternal;
        BboxOrdinate(bbox, "xmin", 0).Should().Be(expected.MinX);
        BboxOrdinate(bbox, "ymin", 0).Should().Be(expected.MinY);
        BboxOrdinate(bbox, "xmax", 0).Should().Be(expected.MaxX);
        BboxOrdinate(bbox, "ymax", 0).Should().Be(expected.MaxY);
    }

    [Fact]
    public async Task FormatAsGeoParquet_NullOrEmptyGeometry_ProducesNullBbox()
    {
        var layer = CreateLayer(Field("objectid", MetadataV2FieldType.BigInteger, nullable: false));
        var withGeometry = Feature.Create(
            1,
            CreatePointWkb(10.0, 20.0),
            new Dictionary<string, object?> { ["objectid"] = 1L }.ToImmutableDictionary());
        var withoutGeometry = Feature.Create(
            2,
            geometry: null,
            new Dictionary<string, object?> { ["objectid"] = 2L }.ToImmutableDictionary());

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Create(2, [withGeometry, withoutGeometry]),
            layer,
            returnGeometry: true,
            outputSrid: 4326,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        var bbox = (StructArray)batch!.Column("bbox");

        bbox.IsNull(0).Should().BeFalse("row with geometry must have a bbox");
        BboxOrdinate(bbox, "xmin", 0).Should().Be(10.0);
        BboxOrdinate(bbox, "ymax", 0).Should().Be(20.0);

        bbox.IsNull(1).Should().BeTrue("row without geometry must have a null bbox");
        BboxOrdinate(bbox, "xmin", 1).Should().BeNull();
        BboxOrdinate(bbox, "ymin", 1).Should().BeNull();
        BboxOrdinate(bbox, "xmax", 1).Should().BeNull();
        BboxOrdinate(bbox, "ymax", 1).Should().BeNull();
    }

    private static void AssertCoveringPath(JsonElement covering, string ordinate)
    {
        var path = covering.GetProperty(ordinate).EnumerateArray().Select(e => e.GetString()).ToArray();
        path.Should().Equal("bbox", ordinate);
    }

    private static double? BboxOrdinate(StructArray bbox, string ordinate, int row)
    {
        var index = ordinate switch
        {
            "xmin" => 0,
            "ymin" => 1,
            "xmax" => 2,
            "ymax" => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(ordinate))
        };
        return ((DoubleArray)bbox.Fields[index]).GetValue(row);
    }

    private static MetadataV2Resource CreateLayer(params MetadataV2Field[] fields)
        => CreateResource(fields);

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

    private static MetadataV2Resource CreateResource(params MetadataV2Field[] fields)
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

    private static byte[] CreatePointWkb(double x, double y)
        => new WKBWriter().Write(new Point(x, y) { SRID = 4326 });

    private static byte[] CreatePointWkbWithZm(double x, double y, double z, double m)
        => new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: true)
            .Write(new Point(new CoordinateZM(x, y, z, m)) { SRID = 4326 });
}
