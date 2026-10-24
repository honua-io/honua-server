// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// A layer published from an existing PostGIS table keeps its rows in that table.
/// WFS and WMS must read those rows through the storage binding instead of honua.features.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Wfs20)]
public sealed class WfsWmsStorageBoundReadTests : IAsyncLifetime
{
    private const string Marker = "source-row-6356";
    private const string SourceTable = "source_places_6356";
    private const string ServiceName = "source6356";
    private const string TypeName = "source_places";

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Protocol(TestProtocols.Wfs20)]
    [Protocol(TestProtocols.Wfs11)]
    [Protocol(TestProtocols.Wms13)]
    [Operation(Operations.Query)]
    [Operation(Operations.Wms)]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    [InterfaceOperation(TestProtocols.Wfs11, "GetFeature")]
    [InterfaceOperation(TestProtocols.Wfs20, "GetPropertyValue")]
    [InterfaceOperation(TestProtocols.Wms13, "GetFeatureInfo")]
    [InterfaceOperation(TestProtocols.Wms13, "GetMap")]
    [Endpoint("GET /wfs")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task Wfs_GetFeature_And_Wms_GetFeatureInfo_ReadRowsFromPublishedSourceTable()
    {
        var schema = _fixture.CurrentSchema;
        schema.Should().NotBeNullOrWhiteSpace();

        await using (var connection = await _fixture.Postgres.GetConnectionAsync(schema))
        {
            await using var create = connection.CreateCommand();
            create.CommandText = $"""
                CREATE TABLE {SourceTable} (
                    objectid bigint PRIMARY KEY,
                    name text NOT NULL,
                    geom geometry(Point, 4326) NOT NULL
                );
                INSERT INTO {SourceTable} (objectid, name, geom)
                VALUES (1, '{Marker}', ST_SetSRID(ST_MakePoint(10, 20), 4326));
                """;
            await create.ExecuteNonQueryAsync();

            await using var copied = connection.CreateCommand();
            copied.CommandText = $"""
                SELECT COUNT(*)
                FROM "{schema}".features
                WHERE attributes->>'name' = 'source-row-6356'
                """;
            var copiedCount = (long)(await copied.ExecuteScalarAsync())!;
            copiedCount.Should().Be(0, "the source row must not be copied into the managed features table");
        }

        _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>()
            .SetGraph(BuildSourceTableGraph(schema!), schema: schema);

        var wfs20 = await ReadAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES={TypeName}&OUTPUTFORMAT=application/json");
        wfs20.Should().Contain(Marker, wfs20);

        var wfs20Gml = await ReadAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=2.0.0&TYPENAMES={TypeName}");
        wfs20Gml.Should().Contain(Marker, wfs20Gml);
        wfs20Gml.Should().Contain("<gml:Point", wfs20Gml);

        var wfs11 = await ReadAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION=1.1.0&TYPENAME={TypeName}");
        wfs11.Should().Contain(Marker, wfs11);

        var propertyValue = await ReadAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetPropertyValue&VERSION=2.0.0&TYPENAMES={TypeName}&VALUEREFERENCE=name");
        propertyValue.Should().Contain(Marker, propertyValue);

        var featureInfo = await ReadAsync(
            $"/rest/services/{ServiceName}/MapServer/WMS?SERVICE=WMS&REQUEST=GetFeatureInfo&VERSION=1.3.0" +
            "&BBOX=9,19,11,21&CRS=CRS:84&WIDTH=10&HEIGHT=10&LAYERS=1&QUERY_LAYERS=1" +
            "&INFO_FORMAT=text/plain&I=4&J=4&FEATURE_COUNT=10");
        featureInfo.Should().Contain(Marker, featureInfo);

        var mapResponse = await _fixture.Client.GetAsync(
            $"/rest/services/{ServiceName}/MapServer/WMS?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0" +
            "&BBOX=9,19,11,21&CRS=CRS:84&WIDTH=10&HEIGHT=10&LAYERS=1&STYLES=&FORMAT=image/png");
        var mapBody = await mapResponse.Content.ReadAsStringAsync();
        mapResponse.StatusCode.Should().Be(HttpStatusCode.OK, mapBody);
        mapResponse.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    private async Task<string> ReadAsync(string url)
    {
        var response = await _fixture.Client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return body;
    }

    private static MetadataV2Graph BuildSourceTableGraph(string schema)
    {
        const string resourceId = "res-source-places";
        const string bindingId = "binding-source-places";
        const string serviceId = "svc-source-places";

        return new TestMetadataV2GraphBuilder()
            .AddResource(
                resourceId,
                TypeName,
                MetadataV2ResourceType.FeatureDataset,
                accessPolicy: new AccessPolicy { AllowAnonymous = true },
                fields:
                [
                    new MetadataV2Field
                    {
                        Name = "objectid",
                        Type = MetadataV2FieldType.BigInteger,
                        Nullable = false,
                        SemanticRoles = ["id.primary"]
                    },
                    new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = false },
                    new MetadataV2Field
                    {
                        Name = "geom",
                        Type = MetadataV2FieldType.Geometry,
                        Nullable = false,
                        SemanticRoles = ["geometry.primary"]
                    }
                ],
                spatial: new MetadataV2ResourceSpatial
                {
                    SpatialReference = MetadataV2SpatialReference.Wgs84,
                    GeometryType = MetadataV2GeometryType.Point,
                    PrimaryGeometryField = "geom",
                    SupportedCrs = [MetadataV2SpatialReference.Wgs84]
                })
            .AddStorageBinding(
                bindingId,
                resourceId,
                SourceTable,
                storageLayerId: 6356,
                options: new Dictionary<string, JsonElement>
                {
                    ["schemaName"] = JsonSerializer.SerializeToElement(schema),
                    ["tableName"] = JsonSerializer.SerializeToElement(SourceTable),
                    ["primaryKeyColumn"] = JsonSerializer.SerializeToElement("objectid"),
                    ["geometryColumn"] = JsonSerializer.SerializeToElement("geom"),
                    ["sourceBacked"] = JsonSerializer.SerializeToElement(true)
                })
            .AddService(
                serviceId,
                ServiceName,
                accessPolicy: new AccessPolicy { AllowAnonymous = true },
                protocols: [MetadataV2ServiceProtocols.Wfs20, MetadataV2ServiceProtocols.Wms])
            .AddPublication(
                "pub-source-places",
                serviceId,
                resourceId,
                layerIndex: 1,
                storageBindingId: bindingId,
                serviceLocalId: TypeName,
                publicationType: MetadataV2PublicationType.WfsFeatureType)
            .Build();
    }
}
