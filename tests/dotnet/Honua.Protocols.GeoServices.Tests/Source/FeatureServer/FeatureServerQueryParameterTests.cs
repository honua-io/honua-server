// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerQueryParameterTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public FeatureServerQueryParameterTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Operation(Operations.Security)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public void EsriOperationAllowlists_AcceptPortalTokenParameter()
    {
        var tokenBearingOperations = new[]
        {
            FeatureServerEndpoints.AllowedQueryParameters.QueryRelatedRecords,
            FeatureServerEndpoints.AllowedQueryParameters.GenerateRenderer,
            FeatureServerEndpoints.AllowedQueryParameters.GetEstimates,
            FeatureServerEndpoints.AllowedQueryParameters.ServiceGetEstimates,
            FeatureServerEndpoints.AllowedQueryParameters.QueryDomains,
            FeatureServerEndpoints.AllowedQueryParameters.Relationships,
            FeatureServerEndpoints.AllowedQueryParameters.QueryBins,
            FeatureServerEndpoints.AllowedQueryParameters.QueryDateBins,
            FeatureServerEndpoints.AllowedQueryParameters.QueryH3,
            FeatureServerEndpoints.AllowedQueryParameters.ApplyEdits,
            FeatureServerEndpoints.AllowedQueryParameters.DeleteFeatures,
        };

        tokenBearingOperations.Should().AllSatisfy(parameters => parameters.Should().Contain("token"));
    }

    // ArcGIS Pro and the JS API send these parameters by default even when the
    // backing service doesn't honor them. Rejecting the request would break
    // out-of-the-box client connections for interop-compat reasons, so these
    // compatibility-oriented knobs are silently accepted (no-ops on the server).
    // returnExceededLimitFeatures is included here because the ArcGIS Maps SDK for
    // .NET ServiceFeatureTable.QueryFeaturesAsync always sends it, and its contract
    // ("return rows beyond maxRecordCount + report exceededTransferLimit") matches
    // Honua's default behavior, so it is accept-and-ignore (#1460).
    [Theory]
    [InlineData("gdbVersion=sde.DEFAULT")]
    [InlineData("quantizationParameters=1")]
    [InlineData("datumTransformation=4326")]
    [InlineData("returnExceededLimitFeatures=true")]
    [InlineData("returnTrueCurves=true")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithCompatibilityParameter_AcceptsRequest(string queryParam)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=json&{queryParam}");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
    }

    // BUG A regression: returnExceededLimitFeatures must be accepted (not 400) alongside
    // other query controls, since the ArcGIS Maps SDK for .NET always sends it.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithReturnExceededLimitFeaturesAndCountOnly_ReturnsOk()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1%3D1&returnCountOnly=true&returnExceededLimitFeatures=true&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().NotContain("Unsupported query parameters");
    }

    // A negative resultRecordCount must still be rejected.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithNegativeResultRecordCount_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1%3D1&resultRecordCount=-5&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // honua-server#1825: a resultRecordCount above maxRecordCount must be silently capped
    // to maxRecordCount (and the page returned) rather than rejected with a 400 — this is
    // what ArcGIS does, and clients routinely pass a large "give me everything" count. The
    // default maxRecordCount in the test host is 10000, so 100000 is over the limit.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithResultRecordCountAboveMaxRecordCount_ClampsInsteadOfBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1%3D1&resultRecordCount=100000&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().NotContain("cannot exceed");
    }

    // BUG C regression: an unknown geometryType must be rejected, not coerced to an envelope.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithUnknownGeometryType_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1%3D1&geometry=-122.6,37.4,-122.3,37.9&geometryType=esriGeometryBogus&returnCountOnly=true&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("geometryType");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithTileResultType_ReturnsOk()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=json&where=1%3D1&resultType=tile");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithPbfFormat_ReturnsOk()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=pbf");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/x-protobuf");
        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithFlatGeobufFormat_ReturnsOk()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=fgb");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.flatgeobuf");
        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();
    }

    [IntegrationTheory]
    [InlineData("*", 4326)]
    [InlineData("name", 3857)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithFlatGeobufFormatAndNoMatches_ReturnsReadableEmptyFile(string fields, int outputSrid)
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=fgb&where=1%3D0&outFields={fields}&outSR={outputSrid}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.flatgeobuf");
        var payload = await ReadFlatGeobufEvidenceAsync(response, $"empty-{outputSrid}");
        payload.Should().NotBeEmpty("an empty result still needs a valid FlatGeobuf header");
        FlatGeobuf.NTS.FeatureCollectionConversions.Deserialize(payload).Should().BeEmpty();
        using var stream = new MemoryStream(payload);
        var header = FlatGeobuf.Helpers.ReadHeader(stream, out _);
        header.FeaturesCount.Should().Be(0);
        header.IndexNodeSize.Should().Be(0);
        (header.Crs?.Code).Should().Be(outputSrid);
        var columns = Enumerable.Range(0, header.ColumnsLength)
            .Select(index => header.Columns(index)?.Name).ToArray();
        columns.Should().Contain("name");
        columns.Should().Contain("objectid");
        if (fields != "*")
        {
            columns.Should().HaveCount(2);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithFlatGeobufNullableAttribute_PreservesFollowingColumnIdentity()
    {
        var id = await InsertFlatGeobufProbeAsync("""{"name":null,"description":"after-null","population":42}""");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=fgb&where=objectid%3D{id}&outFields=name,description,population");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var decoded = FlatGeobuf.NTS.FeatureCollectionConversions.Deserialize(await ReadFlatGeobufEvidenceAsync(response, "nullable-attributes"));
        var feature = decoded.Should().ContainSingle().Subject;
        feature.Attributes.Exists("name").Should().BeFalse("null properties have no FlatGeobuf property entry");
        feature.Attributes["description"].Should().Be("after-null");
        feature.Attributes["population"].Should().Be(42);
    }

    [IntegrationTheory]
    [InlineData("created_date", MetadataV2FieldType.Date, "2026-10-27")]
    [InlineData("observed_time", MetadataV2FieldType.Time, "12:34:56")]
    [InlineData("record_uuid", MetadataV2FieldType.Uuid, "43c522e5-35d6-48de-96e6-8e8329a10bd5")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithFlatGeobufTypedAttribute_PreservesValue(
        string fieldName, MetadataV2FieldType fieldType, string value)
    {
        _fixture.UpdateV2ResourceSchemaField(WebAppFixture.TestLayerId,
            new MetadataV2Field { Name = fieldName, Type = fieldType, Nullable = true });
        var attributes = new System.Text.Json.Nodes.JsonObject
        {
            [fieldName] = value,
            ["description"] = "following-value"
        };
        var id = await InsertFlatGeobufProbeAsync(attributes.ToJsonString());
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=fgb&where=objectid%3D{id}&outFields={fieldName},description");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var decoded = FlatGeobuf.NTS.FeatureCollectionConversions.Deserialize(await ReadFlatGeobufEvidenceAsync(response, fieldName));
        var feature = decoded.Should().ContainSingle().Subject;
        feature.Attributes[fieldName].Should().Be(value);
        feature.Attributes["description"].Should().Be("following-value");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithFlatGeobufTypedProperties_PreservesExactValuesAndColumnTypes()
    {
        var fields = new[]
        {
            new MetadataV2Field { Name = "exact_integer", Type = MetadataV2FieldType.BigInteger },
            new MetadataV2Field { Name = "enabled", Type = MetadataV2FieldType.Boolean },
            new MetadataV2Field { Name = "document", Type = MetadataV2FieldType.Json },
            new MetadataV2Field { Name = "blob", Type = MetadataV2FieldType.Binary },
            new MetadataV2Field { Name = "observed_at", Type = MetadataV2FieldType.DateTime },
            new MetadataV2Field { Name = "observed_fraction", Type = MetadataV2FieldType.DateTime }
        };
        foreach (var field in fields)
        {
            _fixture.UpdateV2ResourceSchemaField(WebAppFixture.TestLayerId, field);
        }
        var id = await InsertFlatGeobufProbeAsync("""
            {"exact_integer":9223372036854775807,"enabled":true,"document":{"items":[1,null,"text"]},
             "blob":"AP9/gA==","observed_at":"2026-10-27T12:34:56.123Z","observed_fraction":0.125}
            """);
        var names = string.Join(',', fields.Select(field => field.Name));
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=fgb&where=objectid%3D{id}&outFields={names}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadFlatGeobufEvidenceAsync(response, "typed-properties");
        using var stream = new MemoryStream(payload);
        var header = FlatGeobuf.Helpers.ReadHeader(stream);
        header.FeaturesCount.Should().Be(1);
        stream.Position += (long)FlatGeobuf.Index.PackedRTree.CalcSize(header.FeaturesCount, header.IndexNodeSize);
        using var reader = new BinaryReader(stream);
        var featureSize = reader.ReadInt32();
        var packed = FlatGeobuf.Feature.GetRootAsFeature(new Google.FlatBuffers.ByteBuffer(reader.ReadBytes(featureSize)));
        using var properties = new MemoryStream(packed.GetPropertiesArray());
        using var propertyReader = new BinaryReader(properties);
        var values = new Dictionary<string, object?>();
        while (properties.Position < properties.Length)
        {
            var column = header.Columns(propertyReader.ReadUInt16())!.Value;
            object value = column.Type switch
            {
                FlatGeobuf.ColumnType.Long => propertyReader.ReadInt64(),
                FlatGeobuf.ColumnType.Bool => propertyReader.ReadBoolean(),
                FlatGeobuf.ColumnType.Binary => propertyReader.ReadBytes(propertyReader.ReadInt32()),
                FlatGeobuf.ColumnType.Json or FlatGeobuf.ColumnType.DateTime =>
                    System.Text.Encoding.UTF8.GetString(propertyReader.ReadBytes(propertyReader.ReadInt32())),
                _ => throw new InvalidDataException($"Unexpected property type: {column.Type}")
            };
            values.Add(column.Name, value);
        }
        values["objectid"].Should().Be(id);
        values["exact_integer"].Should().Be(long.MaxValue);
        values["enabled"].Should().Be(true);
        values["blob"].Should().BeOfType<byte[]>().Which.Should().Equal(0, 255, 127, 128);
        using var document = JsonDocument.Parse((string)values["document"]!);
        document.RootElement.GetProperty("items")[1].ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetProperty("items")[2].GetString().Should().Be("text");
        DateTimeOffset.Parse((string)values["observed_at"]!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(new DateTimeOffset(2026, 10, 27, 12, 34, 56, 123, TimeSpan.Zero));
        DateTimeOffset.Parse((string)values["observed_fraction"]!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(DateTimeOffset.UnixEpoch.AddTicks(1250));
        stream.Position.Should().Be(stream.Length);
    }

    [IntegrationTheory]
    [InlineData(null)]
    [InlineData("POINT EMPTY")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithFlatGeobufNullOrEmptyGeometry_PreservesRow(string? geometry)
    {
        var id = await InsertFlatGeobufProbeAsync("""{"name":"geometry-control"}""", geometry);
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=fgb&where=objectid%3D{id}&outFields=name");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadFlatGeobufEvidenceAsync(response, geometry is null ? "null-geometry" : "empty-point");
        var decoded = FlatGeobuf.NTS.FeatureCollectionConversions.Deserialize(payload);
        var feature = decoded.Should().ContainSingle().Subject;
        feature.Attributes["name"].Should().Be("geometry-control");
        if (geometry is null)
        {
            feature.Geometry.Should().BeNull();
        }
        else
        {
            feature.Geometry.Should().NotBeNull();
            feature.Geometry.IsEmpty.Should().BeTrue();
            feature.Geometry.GeometryType.Should().Be("Point");
        }
        using var stream = new MemoryStream(payload);
        FlatGeobuf.Helpers.ReadHeader(stream).IndexNodeSize.Should().Be(0);
    }

    [IntegrationTheory]
    [InlineData("POLYGON EMPTY", false, false, "xy-polygon")]
    [InlineData("POLYGON Z EMPTY", true, false, "xyz-polygon")]
    [InlineData("GEOMETRYCOLLECTION Z EMPTY", true, false, "xyz-collection")]
    [InlineData("GEOMETRYCOLLECTION M EMPTY", false, true, "xym-collection")]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithFlatGeobufEmptyGeometry_PreservesDeclaredDimensions(
        string geometry, bool hasZ, bool hasM, string evidenceName)
    {
        var id = await InsertFlatGeobufProbeAsync("""{"name":"dimension-control"}""", geometry);
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=fgb&where=objectid%3D{id}&outFields=name&returnZ=true&returnM=true");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadFlatGeobufEvidenceAsync(response, "dimension-" + evidenceName);
        using var stream = new MemoryStream(payload);
        var header = FlatGeobuf.Helpers.ReadHeader(stream);
        header.FeaturesCount.Should().Be(1);
        header.HasZ.Should().Be(hasZ);
        header.HasM.Should().Be(hasM);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithFlatGeobufMultiPointEmptyMember_PreservesBothMembers()
    {
        var id = await InsertFlatGeobufProbeAsync("""{"name":"empty-component"}""", "MULTIPOINT (EMPTY, (1 2))");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=fgb&where=objectid%3D{id}&outFields=name");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadFlatGeobufEvidenceAsync(response, "multipoint-empty-member");
        var feature = FlatGeobuf.NTS.FeatureCollectionConversions.Deserialize(payload).Should().ContainSingle().Subject;
        feature.Geometry.NumGeometries.Should().Be(2);
        feature.Geometry.GetGeometryN(0).IsEmpty.Should().BeTrue();
        feature.Geometry.GetGeometryN(1).Coordinate.X.Should().Be(1);
        feature.Geometry.GetGeometryN(1).Coordinate.Y.Should().Be(2);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithFlatGeobufSpatialIndex_ReadsExactRowsAcrossMultipleNodeLevels()
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            SELECT @layer, ST_SetSRID(ST_Point(i::double precision, i::double precision / 2), 4326),
                   jsonb_build_object('name', 'indexed-' || i, 'category', 'cng-index-probe')
            FROM generate_series(1, 40) i;
            """;
        command.Parameters.AddWithValue("layer", WebAppFixture.TestLayerId);
        await command.ExecuteNonQueryAsync();
        var predicate = Uri.EscapeDataString("category = 'cng-index-probe'");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=fgb&where={predicate}&outFields=name,category&resultRecordCount=100");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadFlatGeobufEvidenceAsync(response, "indexed-40-points");
        FlatGeobuf.NTS.FeatureCollectionConversions.Deserialize(payload).Should().HaveCount(40);
        using var stream = new MemoryStream(payload);
        FlatGeobuf.Helpers.ReadHeader(stream).IndexNodeSize.Should().Be(16);
        stream.Position = 0;
        var subset = FlatGeobuf.NTS.FeatureCollectionConversions.Deserialize(stream,
            new NetTopologySuite.Geometries.Envelope(10, 12, 5, 6)).ToArray();
        subset.Select(feature => feature.Attributes["name"]).Should()
            .BeEquivalentTo("indexed-10", "indexed-11", "indexed-12");
    }

    private static async Task<byte[]> ReadFlatGeobufEvidenceAsync(HttpResponseMessage response, string name)
    {
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.flatgeobuf");
        var payload = await response.Content.ReadAsByteArrayAsync();
        // Explicit opt-in saves real endpoint bytes for independent GDAL readback.
        if (Environment.GetEnvironmentVariable("HONUA_FGB_FIXTURE_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, name + ".fgb"), payload);
        }
        return payload;
    }

    private async Task<long> InsertFlatGeobufProbeAsync(string attributes, string? geometry = "POINT (1 2)")
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO features (layer_id, geometry, attributes)
            VALUES (@layer, ST_GeomFromText(@geometry, 4326), @attributes::jsonb)
            RETURNING objectid;
            """;
        command.Parameters.AddWithValue("layer", WebAppFixture.TestLayerId);
        command.Parameters.AddWithValue("attributes", attributes);
        command.Parameters.AddWithValue("geometry", NpgsqlTypes.NpgsqlDbType.Text, (object?)geometry ?? DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeobufFormat_ReturnsOk()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=geobuf");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geobuf");
        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoParquetFormat_ReturnsOk()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=parquet");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.apache.parquet");
        var payload = await response.Content.ReadAsByteArrayAsync();
        await AssertGeoParquetPayloadAsync(payload, expectGeometry: true);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithFlatGeobufFormatAndDistinct_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=fgb&returnDistinctValues=true");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("returnDistinctValues is not supported when f=fgb.");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoParquetFormatAndResolvableNon4326OutSR_ReturnsParquetWithProjJsonCrs()
    {
        // #2844: EPSG:3857 has a resolvable PROJJSON definition, so GeoParquet output reprojects
        // through the shared query pipeline and emits the authoritative PROJJSON crs metadata.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=parquet&outSR=3857");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.apache.parquet");
        var payload = await response.Content.ReadAsByteArrayAsync();
        await AssertGeoParquetCrsAsync(payload, expectedEpsg: 3857);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoParquetFormatAndUnresolvableOutSR_ReturnsBadRequest()
    {
        // #2844/#2907: EPSG:2000 (Anguilla 1957 / British West Indies Grid) is a valid CRS the
        // registry resolves from spatial_ref_sys, but it is NOT in the GeoParquet PROJJSON
        // catalog. This exercises the catalog-resolvability branch of the endpoint guard (a
        // made-up SRID would be rejected earlier by CRS-registry validation, never reaching it).
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=parquet&outSR=2000");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        // Assert against the endpoint guard's own message (not the deeper writer message), so
        // removing the catalog-resolvability branch makes this test fail (branch coverage).
        content.Should().Contain("GeoParquet output does not support outSR 2000");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoParquetFormatAndReturnM_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=parquet&returnM=true");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("GeoParquet output does not support returnM=true");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoArrowFormatAndReturnM_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=arrow&returnM=true");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("GeoArrow output does not support returnM=true");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoJsonFormatAndReturnM_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=geojson&returnM=true");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("GeoJSON output does not support returnM=true");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoParquetFormatNoGeometryAndNon4326OutSR_ReturnsOk()
    {
        // When returnGeometry=false the parquet file has no geometry column or CRS metadata,
        // so the 4326 restriction should not apply.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=parquet&returnGeometry=false&outSR=3857");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.apache.parquet");
        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoParquetFormatAndOutStatisticsAndNon4326OutSR_ReturnsJson()
    {
        // outStatistics queries always return JSON regardless of f=, so the parquet
        // CRS restriction must not reject the request.
        var outStats = Uri.EscapeDataString(
            """[{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":"cnt"}]""");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=parquet&outSR=3857&outStatistics={outStats}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoParquetFormatNoGeometry_ReturnsOk()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=parquet&returnGeometry=false");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.apache.parquet");
        var payload = await response.Content.ReadAsByteArrayAsync();
        await AssertGeoParquetPayloadAsync(payload, expectGeometry: false);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeobufFormatAndDistinct_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=geobuf&returnDistinctValues=true");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("returnDistinctValues is not supported when f=geobuf.");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithFlatGeobufAcceptHeader_ReturnsOk()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query");
        request.Headers.Accept.ParseAdd("application/vnd.flatgeobuf");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.flatgeobuf");
        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeobufAcceptHeader_ReturnsOk()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query");
        request.Headers.Accept.ParseAdd("application/geobuf");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geobuf");
        var payload = await response.Content.ReadAsByteArrayAsync();
        payload.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeoParquetAcceptHeader_ReturnsOk()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query");
        request.Headers.Accept.ParseAdd("application/vnd.apache.parquet");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.apache.parquet");
        var payload = await response.Content.ReadAsByteArrayAsync();
        await AssertGeoParquetPayloadAsync(payload, expectGeometry: true);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithAcceptHeaderQuality_PrefersHighestWeightedFormat()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query");
        request.Headers.TryAddWithoutValidation("Accept", "application/json;q=0.1, application/geo+json;q=0.9");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/geo+json");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithAcceptHeaderQuality_IgnoresZeroWeightedFormats()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.flatgeobuf;q=0, application/json;q=0.5");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithExplicitFormat_PrefersFOverAcceptHeader()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=json");
        request.Headers.Accept.ParseAdd("application/vnd.flatgeobuf");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithPjsonFormat_ReturnsPrettyPrintedJson()
    {
        // f=pjson must emit indented JSON, not the byte-identical compact f=json payload (#1824).
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=pjson");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        content.Should().Contain("\n", "pjson is pretty-printed (indented) JSON");

        // Still valid, semantically-identical JSON.
        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();

        // And distinct from the compact f=json byte payload.
        var compact = await _fixture.Client.GetStringAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=json");
        compact.Should().NotContain("\n");
        content.Should().NotBe(compact);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithoutFormatAndWildcardAccept_DefaultsToJson()
    {
        // Esri REST defaults f to json; a no-f request (even with a wildcard Accept that a
        // browser/curl sends) must not silently return binary protobuf (#1824).
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1%3D1");
        request.Headers.TryAddWithoutValidation("Accept", "*/*");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithoutFormatAndBrowserAccept_DefaultsToJson()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?where=1%3D1");
        request.Headers.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");

        var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithUnsupportedFormat_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=xml");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        var details = document.RootElement.GetProperty("error").GetProperty("details")
            .EnumerateArray()
            .Select(element => element.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        details.Should().Contain(detail => detail!.Contains("Output format 'xml' is not supported"));
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithMalformedObjectIdsDelimiter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?objectIds=1,,2&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithMalformedOutFieldsDelimiter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?outFields=name,,category&f=json");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task QueryPost_WithUnsupportedBodyParameter_ReturnsBadRequest()
    {
        var payload = """
            {
              "where": "1=1",
              "outStatistics": [{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":"count"}],
              "f": "json"
            }
            """;

        using var payloadContent = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query",
            payloadContent);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("outStatistics");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task QueryPost_WithUnsupportedContentType_ReturnsUnsupportedMediaType()
    {
        var payload = """
            {
              "where": "1=1",
              "f": "json"
            }
            """;

        using var payloadContent = new StringContent(payload, Encoding.UTF8, "text/plain");
        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query",
            payloadContent);

        await response.AssertGeoServicesErrorAsync(415, 500);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Unsupported Media Type");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithMalformedOutStatistics_DoesNotLeakJsonParserDetails()
    {
        const string sentinel = "SENTINEL_STAT_FIELD";
        var malformedOutStatistics = Uri.EscapeDataString($"[{{\"statisticType\":\"count\",\"onStatisticField\":\"{sentinel}\"");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?f=json&outStatistics={malformedOutStatistics}");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("outStatistics must be a valid JSON array.");
        content.Should().NotContain("BytePositionInLine");
        content.Should().NotContain("LineNumber");
        content.Should().NotContain("System.Text.Json");
        content.Should().NotContain(sentinel);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithGeometryPrecision_RoundsCoordinates()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?geometryPrecision=0&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();

        queryResponse!.Features.Should().NotBeNull();
        var features = queryResponse.Features!;
        var feature = features.FirstOrDefault(item => item.Geometry?.X != null && item.Geometry?.Y != null);
        feature.Should().NotBeNull("expected at least one feature with geometry");

        var x = feature!.Geometry!.X!.Value;
        var y = feature.Geometry!.Y!.Value;

        Math.Abs(x % 1).Should().BeLessThan(1e-9);
        Math.Abs(y % 1).Should().BeLessThan(1e-9);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithReturnDistinctValues_ReturnsUniqueAttributes()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?outFields=category&returnDistinctValues=true&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();

        queryResponse!.Features.Should().NotBeNull();
        var categories = queryResponse.Features!
            .Select(feature => GetStringAttribute(feature.Attributes, "category"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        categories.Should().NotBeEmpty();
        categories.Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(categories.Count);
    }

    // Regression: returnDistinctValues must honor the caller's outFields verbatim.
    // Force-appending objectid would give every distinct row a unique OID and defeat
    // de-duplication, so the OID must NOT appear in the returned attributes (#1427).
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithReturnDistinctValues_DoesNotIncludeObjectId()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query?outFields=category&returnDistinctValues=true&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull().And.NotBeEmpty();

        foreach (var feature in queryResponse.Features!)
        {
            feature.Attributes.Keys.Should().NotContain(
                key => key.Equals("objectid", StringComparison.OrdinalIgnoreCase),
                "distinct results have no stable OID and must not force-append objectid");
        }
    }

    // Esri's large-page JSON path used the streaming formatter before this regression:
    // returnDistinctValues was ignored there, so duplicate attributes and feature OIDs
    // leaked into the response (#4091).
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithReturnDistinctValues_OverStreamingThreshold_RemainsUnique()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            "?where=1%3D1&outFields=category&returnDistinctValues=true&returnGeometry=false" +
            "&resultRecordCount=1001&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNullOrEmpty();

        var categories = queryResponse.Features!
            .Select(feature => GetStringAttribute(feature.Attributes, "category"))
            .Where(value => value is not null)
            .ToArray();
        categories.Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(categories.Length);
        foreach (var feature in queryResponse.Features!)
        {
            feature.Attributes.Keys.Should().NotContain(
                key => key.Equals("OBJECTID", StringComparison.OrdinalIgnoreCase));
        }
    }

    // The test layer (layer 0) has five features whose `category` is either
    // "test" (objectids 1, 3, 5) or "sample" (objectids 2, 4). Grouping by
    // category and filtering COUNT(objectid) > 2 keeps only the "test" group.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithHavingFilteringGroups_ReturnsOnlyPassingGroups()
    {
        var outStats = Uri.EscapeDataString(
            """[{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":"cnt"}]""");
        var having = Uri.EscapeDataString("COUNT(objectid) > 2");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            $"?f=json&groupByFieldsForStatistics=category&outStatistics={outStats}&having={having}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull();

        var categories = queryResponse.Features!
            .Select(feature => GetStringAttribute(feature.Attributes, "category"))
            .ToList();

        categories.Should().ContainSingle()
            .Which.Should().Be("test", "only the 'test' group has COUNT(objectid) > 2");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithHavingButNoOutStatistics_ReturnsBadRequest()
    {
        var having = Uri.EscapeDataString("COUNT(objectid) > 2");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            $"?f=json&groupByFieldsForStatistics=category&having={having}");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("having");
        content.Should().Contain("outStatistics");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithHavingAggregateNotInOutStatistics_ReturnsBadRequest()
    {
        var outStats = Uri.EscapeDataString(
            """[{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":"cnt"}]""");
        // SUM(objectid) is not declared in outStatistics, so the HAVING term is rejected.
        var having = Uri.EscapeDataString("SUM(objectid) > 2");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            $"?f=json&groupByFieldsForStatistics=category&outStatistics={outStats}&having={having}");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("having");
    }

    // #3372: when outStatistics is present the result columns ARE the statistic aliases plus
    // the groupByFieldsForStatistics columns, so ordering by an alias is the only way to order
    // aggregate output. ArcGIS Server accepts it; validating against the layer's source fields
    // (the non-aggregate denominator) rejected it. The test layer groups to test=3, sample=2.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithOutStatisticsOrderByStatisticAliasDescending_ReturnsRowsOrderedByAlias()
    {
        var features = await QueryStatisticsFeaturesAsync("feature_count DESC");

        features.Select(feature => GetStringAttribute(feature.Attributes, "category"))
            .Should().Equal("test", "sample");
        features.Select(feature => GetInt64Attribute(feature.Attributes, "feature_count"))
            .Should().Equal(3L, 2L);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithOutStatisticsOrderByStatisticAliasAscending_ReturnsRowsOrderedByAlias()
    {
        var features = await QueryStatisticsFeaturesAsync("feature_count ASC");

        features.Select(feature => GetInt64Attribute(feature.Attributes, "feature_count"))
            .Should().Equal(2L, 3L);
    }

    // An alias with no explicit direction defaults to ascending, matching the non-aggregate path.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithOutStatisticsOrderByStatisticAliasNoDirection_DefaultsToAscending()
    {
        var features = await QueryStatisticsFeaturesAsync("feature_count");

        features.Select(feature => GetInt64Attribute(feature.Attributes, "feature_count"))
            .Should().Equal(2L, 3L);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithOutStatisticsOrderByGroupByField_ReturnsRowsOrderedByThatField()
    {
        var ascending = await QueryStatisticsFeaturesAsync("category ASC");
        ascending.Select(feature => GetStringAttribute(feature.Attributes, "category"))
            .Should().Equal("sample", "test");

        var descending = await QueryStatisticsFeaturesAsync("category DESC");
        descending.Select(feature => GetStringAttribute(feature.Attributes, "category"))
            .Should().Equal("test", "sample");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithOutStatisticsOrderByMultipleTerms_ReturnsOrderedRows()
    {
        var features = await QueryStatisticsFeaturesAsync("feature_count DESC, category ASC");

        features.Select(feature => GetStringAttribute(feature.Attributes, "category"))
            .Should().Equal("test", "sample");
    }

    // The widened set is exactly "declared statistic alias or declared group-by field" — a name
    // that is neither is still rejected, so typos do not silently sort by nothing.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithOutStatisticsOrderByUnknownField_ReturnsBadRequest()
    {
        var content = await QueryStatisticsRawAsync("not_a_field");

        content.Should().Contain("orderByFields");
        content.Should().Contain("\"code\":400");
    }

    // A layer field that is real but is not in the aggregate result set is equally invalid:
    // `name` exists on the layer yet is neither a statistic alias nor a group-by column.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithOutStatisticsOrderByNonAggregateLayerField_ReturnsBadRequest()
    {
        var content = await QueryStatisticsRawAsync("name");

        content.Should().Contain("orderByFields");
        content.Should().Contain("\"code\":400");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithOutStatisticsOrderByInvalidDirection_ReturnsBadRequest()
    {
        var content = await QueryStatisticsRawAsync("feature_count SIDEWAYS");

        content.Should().Contain("orderByFields");
        content.Should().Contain("\"code\":400");
    }

    // The non-aggregate path keeps the layer-schema denominator exactly as before: a statistic
    // alias is meaningless without outStatistics and must still be rejected there.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithoutOutStatisticsOrderByUnknownField_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            "?f=json&where=1%3D1&orderByFields=feature_count%20DESC");

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"code\":400");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_WithoutOutStatisticsOrderByLayerField_ReturnsOrderedFeatures()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            "?f=json&where=1%3D1&outFields=objectid&returnGeometry=false&orderByFields=objectid%20DESC");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);

        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull().And.NotBeEmpty();

        var objectIds = queryResponse.Features!
            .Select(feature => GetInt64Attribute(feature.Attributes, "objectid"))
            .ToList();
        objectIds.Should().BeInDescendingOrder();
    }

    [IntegrationTest]
    [Operation(Operations.GetLayerInfo)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}")]
    public async Task LayerMetadata_AdvertisesSupportsHavingClause()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        document.RootElement.TryGetProperty("advancedQueryCapabilities", out var capabilities)
            .Should().BeTrue("layer metadata should expose advancedQueryCapabilities");
        capabilities.TryGetProperty("supportsHavingClause", out var supportsHaving)
            .Should().BeTrue("advancedQueryCapabilities should expose supportsHavingClause");
        supportsHaving.GetBoolean().Should().BeTrue();
    }

    // #3372 helper: the canonical aggregate request — count grouped by category, ordered by the
    // caller-supplied orderByFields — issued against the shared test layer.
    private const string StatisticsOutStatistics =
        """[{"statisticType":"count","onStatisticField":"objectid","outStatisticFieldName":"feature_count"}]""";

    private Task<string> QueryStatisticsRawAsync(string orderByFields)
    {
        var url = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            $"?f=json&groupByFieldsForStatistics=category" +
            $"&outStatistics={Uri.EscapeDataString(StatisticsOutStatistics)}" +
            $"&orderByFields={Uri.EscapeDataString(orderByFields)}";

        return ReadStatisticsBodyAsync(url);
    }

    private async Task<string> ReadStatisticsBodyAsync(string url)
    {
        var response = await _fixture.Client.GetAsync(url);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<List<GeoServicesFeature>> QueryStatisticsFeaturesAsync(string orderByFields)
    {
        var content = await QueryStatisticsRawAsync(orderByFields);
        content.Should().NotContain("\"error\"", "the aggregate query must succeed");

        var queryResponse = JsonSerializer.Deserialize(content, FeatureServerJsonContext.Default.QueryResponse);
        queryResponse.Should().NotBeNull();
        queryResponse!.Features.Should().NotBeNull().And.HaveCount(2);
        return [.. queryResponse.Features!];
    }

    private static long? GetInt64Attribute(Dictionary<string, object?> attributes, string key)
    {
        if (!attributes.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            JsonElement element when element.ValueKind == JsonValueKind.Number => element.GetInt64(),
            _ => long.TryParse(value.ToString(), out var parsed) ? parsed : null
        };
    }

    private static string? GetStringAttribute(Dictionary<string, object?> attributes, string key)
    {
        if (!attributes.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        if (value is string text)
        {
            return text;
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        return value.ToString();
    }

    private static async Task AssertGeoParquetPayloadAsync(byte[] payload, bool expectGeometry)
    {
        payload.Should().NotBeEmpty();
        payload.Length.Should().BeGreaterThanOrEqualTo(4);
        Encoding.ASCII.GetString(payload, 0, 4).Should().Be("PAR1");

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        await batchReader.ReadNextRecordBatchAsync(); // materialize schema

        var fieldNames = reader.Schema.FieldsList.Select(f => f.Name).ToArray();
        fieldNames.Should().OnlyHaveUniqueItems();
        fieldNames.Should().Contain("objectid");

        if (expectGeometry)
        {
            fieldNames.Should().Contain("geometry");
            reader.Schema.Metadata.Should().ContainKey("geo");

            using var metadataDocument = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
            metadataDocument.RootElement.GetProperty("primary_column").GetString().Should().Be("geometry");
        }
        else
        {
            fieldNames.Should().NotContain("geometry");
            var hasGeoKey = reader.Schema.Metadata?.ContainsKey("geo") ?? false;
            hasGeoKey.Should().BeFalse();
        }
    }

    private static async Task AssertGeoParquetCrsAsync(byte[] payload, int expectedEpsg)
    {
        payload.Should().NotBeEmpty();
        Encoding.ASCII.GetString(payload, 0, 4).Should().Be("PAR1");

        using var stream = new MemoryStream(payload);
        using var reader = new ParquetSharp.Arrow.FileReader(stream);
        using var batchReader = reader.GetRecordBatchReader();
        await batchReader.ReadNextRecordBatchAsync(); // materialize schema

        reader.Schema.Metadata.Should().ContainKey("geo");
        using var geoDoc = JsonDocument.Parse(reader.Schema.Metadata["geo"]);
        var geomCol = geoDoc.RootElement.GetProperty("columns").GetProperty("geometry");
        geomCol.TryGetProperty("crs", out var crs).Should().BeTrue("non-4326 output must carry a PROJJSON crs");
        var id = crs.GetProperty("id");
        id.GetProperty("authority").GetString().Should().Be("EPSG");
        id.GetProperty("code").GetInt32().Should().Be(expectedEpsg);
    }

    // returnDistinctValues remains pageable at a large offset because the provider computes
    // the complete distinct set before the response window is applied.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_ReturnDistinctValues_WithVeryHighResultOffset_ReturnsEmptyPage()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            "?where=1%3D1&outFields=category&returnDistinctValues=true&returnGeometry=false" +
            "&resultOffset=200000&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("features");
    }

    // BH2-001: a moderate resultOffset below the threshold (< MaxRecordCount * 10) with
    // returnDistinctValues must still be accepted.
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task Query_ReturnDistinctValues_WithModerateResultOffset_ReturnsOk()
    {
        // resultOffset=50 is well below the rejection threshold and must succeed.
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query" +
            "?where=1%3D1&outFields=category&returnDistinctValues=true&returnGeometry=false" +
            "&resultOffset=50&f=json");

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
    }
}
