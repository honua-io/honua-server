// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Apache.Arrow;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.FeatureServer.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using GeometryType = Honua.Core.Features.Catalog.Domain.GeometryType;

namespace Honua.Server.Tests.Features.FeatureServer.Services;

public sealed class GeoParquetQueryFormatterTests
{
    [Fact]
    public async Task FormatAsGeoParquet_WithFeatures_WritesReadableParquetWithGeoMetadata()
    {
        var layer = CreateLayer(
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("name", FieldType.String, 255),
            new FieldDefinition("population", FieldType.Integer));
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
            .Should().Equal("objectid", "geometry", "name", "population");

        reader.Schema.Metadata.Should().ContainKey("geo");
        using var geoDoc = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
        geoDoc.RootElement.GetProperty("version").GetString().Should().Be("1.1.0");
        geoDoc.RootElement.GetProperty("primary_column").GetString().Should().Be("geometry");
    }

    [Fact]
    public void FormatAsGeoParquet_EmptyResult_ReturnsValidParquetWithCorrectContentType()
    {
        var layer = CreateLayer(
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("name", FieldType.String, 255));

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
    }

    [Fact]
    public async Task FormatAsGeoParquet_AppliesGeometryPrecisionAndDimensionFiltering()
    {
        var layer = CreateLayer(new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false));
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
    public async Task FormatAsGeoParquet_WithZmGeometryAndReturnMTrue_StripsMPerGeoParquetSpec()
    {
        // GeoParquet 1.1.0 only supports XY and XYZ — M values must be stripped
        // even when the caller requests returnM=true.
        var layer = CreateLayer(new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false));
        var feature = Feature.Create(
            1,
            CreatePointWkbWithZm(1.0, 2.0, 3.0, 4.0),
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
            returnM: true,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        var batch = await batchReader.ReadNextRecordBatchAsync();

        batch.Should().NotBeNull();
        var geometryColumn = (BinaryArray)batch!.Column("geometry");
        geometryColumn.IsNull(0).Should().BeFalse();

        var geometry = new WKBReader().Read(geometryColumn.GetBytes(0).ToArray());
        var point = geometry.Should().BeOfType<Point>().Subject;

        point.X.Should().Be(1.0);
        point.Y.Should().Be(2.0);
        point.Coordinate.Z.Should().Be(3.0);
        double.IsNaN(point.Coordinate.M).Should().BeTrue("GeoParquet 1.1.0 does not support M values");
    }

    [Fact]
    public async Task FormatAsGeoParquet_WithoutGeometry_OmitsGeometryColumn()
    {
        var layer = CreateLayer(
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("name", FieldType.String, 255));
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
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("name", FieldType.String, 255),
            new FieldDefinition("population", FieldType.Integer),
            new FieldDefinition("area", FieldType.Double));
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
            .Should().Equal("objectid", "geometry", "name");
    }

    [Fact]
    public void FormatAsGeoParquet_GeoMetadata_IncludesCrsAndGeometryType()
    {
        var layer = CreateLayer(
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false));
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
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("updated_at", FieldType.DateTime));
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
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("created_at", FieldType.DateTime));
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
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("updated_at", FieldType.DateTime),
            new FieldDefinition("event_date", FieldType.Date));
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
    public void FormatAsGeoParquet_EmptyResultWithNon4326Srid_WritesCrsNull()
    {
        var layer = CreateLayer(
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false));

        var (payload, _) = GeoParquetQueryFormatter.FormatAsGeoParquet(
            QueryResult<Feature>.Empty(),
            layer,
            returnGeometry: true,
            outputSrid: 3857,
            returnZ: false,
            returnM: false,
            new GeometryLimits());

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);

        reader.Schema.Metadata.Should().ContainKey("geo");
        using var geoDoc = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
        var geomCol = geoDoc.RootElement.GetProperty("columns")
            .GetProperty("geometry");
        // Non-4326 SRIDs write crs:null (full PROJJSON requires a projection library)
        geomCol.GetProperty("crs").ValueKind.Should().Be(JsonValueKind.Null);
        // bbox is omitted — layer extent is not the same as the exported result extent
        geomCol.TryGetProperty("bbox", out _).Should().BeFalse();
    }

    [Fact]
    public async Task FormatAsGeoParquet_WithRuntimeDistanceAttribute_IncludesDistanceColumn()
    {
        var layer = CreateLayer(
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("name", FieldType.String, 255));
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
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("name", FieldType.String, 255));
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
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("name", FieldType.String, 255));
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
            .Should().Equal("objectid", "geometry", "name")
            .And.NotContain("distance",
                "runtime fields not in outFields should be excluded, matching JSON/GeoJSON behavior");
    }

    [Fact]
    public async Task FormatAsGeoParquet_WithBase64ByteaAttribute_DecodesBytes()
    {
        var layer = CreateLayer(
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("data", FieldType.Binary));
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
            new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("data", FieldType.Binary));
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
    /// Note: The formatter also handles "smallint"/"int2" → Int16 and "decimal" → Double,
    /// but these SQL types are not reachable via FieldDefinition.SqlType today (the Postgres
    /// layer maps smallint → FieldType.Integer → "INTEGER"). Those branches are defensive
    /// for future FieldType additions or raw SQL type overrides.
    /// </summary>
    [Fact]
    public async Task FormatAsGeoParquet_AllSqlTypes_SchemaAndBuilderTypesAreConsistent()
    {
        // One field per FieldType (except Geometry, which is the geometry column itself).
        var layer = new LayerDefinition(
            Id: 1,
            Name: "type_sync_test",
            Description: "All SQL type mappings",
            GeometryType: GeometryType.Point,
            SpatialReference: SpatialReference.WGS84,
            Fields:
            [
                new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false),
                new FieldDefinition("text_field", FieldType.String),           // TEXT
                new FieldDefinition("varchar_field", FieldType.String, 100),   // VARCHAR(100)
                new FieldDefinition("int_field", FieldType.Integer),           // INTEGER
                new FieldDefinition("double_field", FieldType.Double),         // DOUBLE PRECISION
                new FieldDefinition("float_field", FieldType.Float),           // REAL
                new FieldDefinition("bool_field", FieldType.Boolean),          // BOOLEAN
                new FieldDefinition("datetime_field", FieldType.DateTime),     // TIMESTAMP WITH TIME ZONE
                new FieldDefinition("date_field", FieldType.Date),             // DATE
                new FieldDefinition("time_field", FieldType.Time),             // TIME
                new FieldDefinition("json_field", FieldType.Json),             // JSONB
                new FieldDefinition("binary_field", FieldType.Binary),         // BYTEA
                new FieldDefinition("uuid_field", FieldType.Uuid),             // UUID
                new FieldDefinition("shape", FieldType.Geometry, Nullable: true)
            ]);

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
    public void FormatAsGeoParquet_With2DGeometryAndReturnZTrue_DoesNotAdvertiseZ()
    {
        // A 2D-only layer/feature with returnZ=true must not claim "Point Z" in metadata
        // because the actual WKB payload is 2D.
        var layer = CreateLayer(new FieldDefinition("objectid", FieldType.BigInteger, Nullable: false));
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

    private static LayerDefinition CreateLayer(params FieldDefinition[] fields)
        => new(
            Id: 1,
            Name: "test_layer",
            Description: "Test Layer",
            GeometryType: GeometryType.Point,
            SpatialReference: SpatialReference.WGS84,
            Fields:
            [
                .. fields,
                new FieldDefinition("shape", FieldType.Geometry, Nullable: true)
            ]);

    private static byte[] CreatePointWkb(double x, double y)
        => new WKBWriter().Write(new Point(x, y) { SRID = 4326 });

    private static byte[] CreatePointWkbWithZm(double x, double y, double z, double m)
        => new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: true, emitM: true)
            .Write(new Point(new CoordinateZM(x, y, z, m)) { SRID = 4326 });
}
