// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// Exercises the advertised geometry property with attribute-only schema metadata.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wfs10)]
[Protocol(TestProtocols.Wfs20)]
public sealed class WfsFallbackGeometryTransactionTests : IAsyncLifetime
{
    private const string TypeName = "native_rich_edits_wfs";
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
        .ReplaceService<IMetadataV2GraphProvider>(BuildMetadataProvider());

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE features SET attributes = attributes ||
              '{"rank":1,"score":1.5,"active":false,"cert_owner":"fallback-geometry-regression"}'::jsonb
            WHERE layer_id = 0
            """;
        await command.ExecuteNonQueryAsync();
    }
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Create, Operations.Update)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs10, "Transaction")]
    [InterfaceOperation(TestProtocols.Wfs20, "Transaction")]
    public async Task Transaction_AdvertisedGeometry_PersistsInsertUpdateReplaceAndNil(bool explicitGeometry)
    {
        var resource = _fixture.GetCurrentV2GraphSnapshot().Graph.Resources.Single();
        resource.FindPrimaryGeometryField().Should().BeNull();
        resource.SchemaFields.Should().NotContain(field => field.Type == MetadataV2FieldType.Geometry);
        if (explicitGeometry)
        {
            _fixture.UpdateV2ResourceSchemaField(0,
                new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = true });
        }

        using var schemaResponse = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=DescribeFeatureType&VERSION=1.0.0&TYPENAME=honua:{TypeName}");
        var schemaBody = await schemaResponse.Content.ReadAsStringAsync();
        schemaResponse.StatusCode.Should().Be(HttpStatusCode.OK, schemaBody);
        XNamespace xsd = "http://www.w3.org/2001/XMLSchema";
        XDocument.Parse(schemaBody).Descendants(xsd + "element").Should().ContainSingle(
            element => (string?)element.Attribute("name") == (explicitGeometry ? "shape" : "geometry"));

        // Stock QGIS 3.44 sends WFS 1.0 / GML 2, including an EPSG URN with XY coordinates.
        var inserted = await SendAsync(LegacyTransaction($$"""
            <Insert><native_rich_edits_wfs xmlns="http://honua.io/wfs">
              <name>Lāhainā ' &amp; &lt; &gt; – 東京</name><rank>40</rank><score>42.125</score>
              <active>false</active><observed_at>2025-01-02T03:04:05.678Z</observed_at>
              <cert_owner>fallback-geometry-regression</cert_owner>
              <geometry>{{Point("-157.83330000000000837,21.35549999999999926")}}</geometry>
            </native_rich_edits_wfs></Insert>
            """), HttpStatusCode.OK);
        XNamespace ogc = "http://www.opengis.net/ogc";
        var fid = XDocument.Parse(inserted).Descendants(ogc + "FeatureId").Single().Attribute("fid")!.Value;
        var objectId = long.Parse(fid[(fid.LastIndexOf('.') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        await AssertStoredAsync(objectId, -157.8333, 21.3555, "Lāhainā ' & < > – 東京", 40);
        using (var created = await ReadStoredAsync(objectId))
        {
            var attributes = created.RootElement.GetProperty("attributes");
            attributes.GetProperty("score").GetDouble().Should().Be(42.125);
            attributes.GetProperty("active").GetBoolean().Should().BeFalse();
            attributes.GetProperty("observed_at").GetString().Should().Be("2025-01-02T03:04:05.678Z");
            attributes.GetProperty("cert_owner").GetString().Should().Be("fallback-geometry-regression");
        }

        await SendAsync(LegacyTransaction(Update(fid,
            Property("geometry", Point("-157.81230000000000757,21.37650000000000006")))), HttpStatusCode.OK);
        await AssertStoredAsync(objectId, -157.8123, 21.3765, "Lāhainā ' & < > – 東京", 40);

        // The native full-feature case emits another geometry-only Update, then attribute edits.
        await SendAsync(LegacyTransaction(Update(fid,
            Property("geometry", Point("-157.79990000000000805,21.39989999999999881")) +
            Property("name", "updated-rich-feature") + Property("rank", "41"))), HttpStatusCode.OK);
        await AssertStoredAsync(objectId, -157.7999, 21.3999, "updated-rich-feature", 41);

        // Replace shares the insert payload parser; retain the existing WFS 2.0 axis contract.
        await SendAsync($$"""
            <wfs:Transaction service="WFS" version="2.0.0" xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:fes="http://www.opengis.net/fes/2.0" xmlns:gml="http://www.opengis.net/gml/3.2"
                xmlns:honua="http://honua.io/wfs">
              <wfs:Replace><honua:native_rich_edits_wfs>
                <honua:name>replaced-rich-feature</honua:name><honua:rank>42</honua:rank>
                <honua:score>100.5</honua:score><honua:active>true</honua:active>
                <honua:cert_owner>fallback-geometry-regression</honua:cert_owner>
                <honua:geometry><gml:Point srsName="urn:ogc:def:crs:EPSG::4326">
                  <gml:pos>21.4 -157.8</gml:pos></gml:Point></honua:geometry>
              </honua:native_rich_edits_wfs>
                <fes:Filter><fes:ResourceId rid="{{fid}}" /></fes:Filter>
              </wfs:Replace>
            </wfs:Transaction>
            """, HttpStatusCode.OK);
        await AssertStoredAsync(objectId, -157.8, 21.4, "replaced-rich-feature", 42);

        await SendAsync(LegacyTransaction(Update(fid,
            "<Property><Name>honua:geometry</Name><Value xsi:nil=\"true\" /></Property>")), HttpStatusCode.OK);
        using var nilState = await ReadStoredAsync(objectId);
        nilState.RootElement.GetProperty("x").ValueKind.Should().Be(JsonValueKind.Null);
        nilState.RootElement.GetProperty("attributes").GetProperty("name").GetString().Should().Be("replaced-rich-feature");
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs10, "Transaction")]
    public async Task Transaction_UnknownProperty_IsRejectedWithoutChangingSql(bool update)
    {
        var before = await ReadLayerStateAsync();
        var action = update
            ? Update($"{TypeName}.1", Property("missing_property", Point("-157.8,21.3")))
            : $"<Insert><honua:{TypeName}><honua:missing_property>{Point("-157.8,21.3")}</honua:missing_property></honua:{TypeName}></Insert>";
        var response = await SendAsync(LegacyTransaction(action), HttpStatusCode.BadRequest);
        response.Should().Contain("InvalidParameterValue");
        (await ReadLayerStateAsync()).Should().Be(before);
    }

    [IntegrationTest]
    [Operation(Operations.Update, Operations.ErrorHandling)]
    [Endpoint("POST /wfs")]
    [InterfaceOperation(TestProtocols.Wfs10, "Transaction")]
    public async Task Transaction_Fallback_DoesNotShadowAttributesOrCreateNonspatialGeometry()
    {
        _fixture.UpdateV2ResourceSchemaField(0,
            new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.String, Nullable = true, Hidden = true });
        await SendAsync(LegacyTransaction(Update($"{TypeName}.1", Property("shape", "survey-point"))), HttpStatusCode.OK);
        using var before = await ReadStoredAsync(1);
        before.RootElement.GetProperty("attributes").GetProperty("shape").GetString().Should().Be("survey-point");

        _fixture.UpdateV2ResourceMetadata(0, spatial: new MetadataV2ResourceSpatial
        {
            SpatialReference = MetadataV2SpatialReference.Wgs84,
            GeometryType = MetadataV2GeometryType.None
        });
        await SendAsync(LegacyTransaction(Update($"{TypeName}.1", Property("geometry", Point("-157.8,21.3")))),
            HttpStatusCode.BadRequest);
        using var after = await ReadStoredAsync(1);
        after.RootElement.GetRawText().Should().Be(before.RootElement.GetRawText());
    }

    private async Task<string> SendAsync(string xml, HttpStatusCode expectedStatus)
    {
        using var content = new StringContent(xml, Encoding.UTF8, "text/xml");
        using var response = await _fixture.Client.PostAsync("/wfs?SERVICE=WFS&REQUEST=Transaction", content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expectedStatus, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        return body;
    }

    private async Task<JsonDocument> ReadStoredAsync(long objectId)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT jsonb_build_object('x', ST_X(geometry), 'y', ST_Y(geometry),
              'srid', ST_SRID(geometry), 'attributes', attributes)::text
            FROM features WHERE layer_id = 0 AND objectid = @id
            """;
        command.Parameters.AddWithValue("id", objectId);
        return JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
    }

    private async Task<string> ReadLayerStateAsync()
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT jsonb_agg(jsonb_build_object('objectid', objectid,
              'geometry', encode(ST_AsEWKB(geometry), 'hex'), 'attributes', attributes)
              ORDER BY objectid)::text FROM features WHERE layer_id = 0
            """;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task AssertStoredAsync(long objectId, double x, double y, string name, int rank)
    {
        using var state = await ReadStoredAsync(objectId);
        state.RootElement.GetProperty("x").GetDouble().Should().BeApproximately(x, 1e-10);
        state.RootElement.GetProperty("y").GetDouble().Should().BeApproximately(y, 1e-10);
        state.RootElement.GetProperty("srid").GetInt32().Should().Be(4326);
        var attributes = state.RootElement.GetProperty("attributes");
        attributes.GetProperty("name").GetString().Should().Be(name);
        attributes.GetProperty("rank").GetInt32().Should().Be(rank);
    }

    private static string LegacyTransaction(string action) => $$"""
        <Transaction xmlns="http://www.opengis.net/wfs" version="1.0.0" service="WFS"
            xmlns:honua="http://honua.io/wfs" xmlns:gml="http://www.opengis.net/gml"
            xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">{{action}}</Transaction>
        """;

    private static string Point(string coordinates) => $"""
        <gml:Point srsName="urn:ogc:def:crs:EPSG::4326"><gml:coordinates cs="," ts=" ">{coordinates}</gml:coordinates></gml:Point>
        """;

    private static string Property(string name, string value)
        => $"<Property><Name>honua:{name}</Name><Value>{value}</Value></Property>";

    private static string Update(string fid, string properties) => $"""
        <Update typeName="honua:{TypeName}">{properties}<Filter xmlns="http://www.opengis.net/ogc">
          <FeatureId fid="{fid}" /></Filter></Update>
        """;

    private static TestMetadataV2GraphProvider BuildMetadataProvider()
    {
        var builder = new TestMetadataV2GraphBuilder();
        var policy = new AccessPolicy { AllowAnonymous = true, AllowAnonymousWrite = true };
        builder.AddService("native-edit-service", "native_edits", accessPolicy: policy,
                protocols: [MetadataV2ServiceProtocols.Wfs20])
            .AddResource(TypeName, TypeName, fields:
            [
                new() { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                new() { Name = "name", Type = MetadataV2FieldType.String, Nullable = true },
                new() { Name = "rank", Type = MetadataV2FieldType.Integer, Nullable = false },
                new() { Name = "score", Type = MetadataV2FieldType.Double, Nullable = false },
                new() { Name = "active", Type = MetadataV2FieldType.Boolean, Nullable = false },
                new() { Name = "observed_at", Type = MetadataV2FieldType.DateTime, Nullable = true },
                new() { Name = "cert_owner", Type = MetadataV2FieldType.String, Nullable = false }
            ], accessPolicy: policy, spatial: new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                SupportedCrs = [MetadataV2SpatialReference.Wgs84]
            })
            .AddStorageBinding("native-edit-storage", TypeName, "features", storageLayerId: 0,
                options: new Dictionary<string, JsonElement>
                {
                    ["geometryColumn"] = JsonSerializer.SerializeToElement("geometry"),
                    ["attributesColumn"] = JsonSerializer.SerializeToElement("attributes")
                })
            .AddPublication("native-edit-publication", "native-edit-service", TypeName, layerIndex: 0,
                storageBindingId: "native-edit-storage", publicationType: MetadataV2PublicationType.WfsFeatureType);
        return new TestMetadataV2GraphProvider(builder.Build());
    }
}
