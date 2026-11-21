// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Middleware;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.FeatureStore;

// A layer served from a source table must refuse edits even when its metadata declares them: the
// managed writer would store the feature where the layer never reads it back.
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class SourceBackedLayerEditRefusalTests : IAsyncLifetime
{
    private const string Marker = "source-edit-refused";
    private const string SourceTable = "source_edit_refusal";
    private const string ServiceName = "sourceedits";
    private const int StorageLayerId = 6357;

    // The test host swaps in its own feature writer, so the production guard is applied on top of it.
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
        .ConfigureServices(static services => services.AddSourceBackedWriteGuard());

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_LayerServedFromSourceTable_IsRefusedAndStoresNothing()
    {
        var schema = await PublishSourceTableAsync();
        var adds = JsonSerializer.Serialize(new[]
        {
            new { geometry = new { x = 10.5, y = 20.5, spatialReference = new { wkid = 4326 } }, attributes = new { name = Marker } }
        });

        using var client = _fixture.CreateAdminClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json", ["adds"] = adds });
        using var response = await client.PostAsync($"/rest/services/{ServiceName}/FeatureServer/1/applyEdits", content);
        var body = await response.Content.ReadAsStringAsync();

        // GeoServices reports errors in the body of an HTTP 200, as Esri clients expect.
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(405, body);
        json.RootElement.TryGetProperty("addResults", out _).Should().BeFalse(body);
        body.Should().NotContain(SourceTable, "the refusal must not disclose the internal table name");
        (await CountAsync(schema, $"SELECT COUNT(*) FROM \"{schema}\".features WHERE attributes->>'name' = '{Marker}'"))
            .Should().Be(0, "a refused edit must not reach the managed features table");
        (await CountAsync(schema, $"SELECT COUNT(*) FROM {SourceTable}")).Should().Be(1);
    }

    // Every other edit kind is refused like an add, before any target features are read: the pre-reads
    // would report a visible row as missing, or a where-clause edit as a successful edit of nothing.
    [IntegrationTheory]
    [Operation(Operations.Update)]
    [Operation(Operations.Delete)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateFeatures")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/deleteFeatures")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    [InlineData("updateFeatures", "features=%5B%7B%22attributes%22%3A%7B%22objectid%22%3A1%2C%22name%22%3A%22renamed%22%7D%7D%5D")]
    [InlineData("deleteFeatures", "objectIds=1")]
    [InlineData("deleteFeatures", "where=1%3D1")]
    [InlineData("applyEdits", "updates=%5B%7B%22attributes%22%3A%7B%22objectid%22%3A1%2C%22name%22%3A%22renamed%22%7D%7D%5D")]
    [InlineData("applyEdits", "deletes=1")]
    [InlineData("calculate", "where=1%3D1&calcExpression=%5B%7B%22field%22%3A%22name%22%2C%22value%22%3A%22renamed%22%7D%5D")]
    public async Task Edit_ExistingRowOfLayerServedFromSourceTable_IsRefusedBeforeTargetsAreRead(string operation, string form)
    {
        var schema = await PublishSourceTableAsync();

        using var client = _fixture.CreateAdminClient();
        using var content = new StringContent($"f=json&{form}", Encoding.UTF8, "application/x-www-form-urlencoded");
        using var response = await client.PostAsync($"/rest/services/{ServiceName}/FeatureServer/1/{operation}", content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.TryGetProperty("error", out var error).Should().BeTrue(body);
        error.GetProperty("code").GetInt32().Should().Be(405, body);
        foreach (var results in new[] { "updateResults", "deleteResults", "updatedFeatureCount" })
        {
            json.RootElement.TryGetProperty(results, out _).Should().BeFalse(body);
        }

        body.Should().NotContain(SourceTable, "the refusal must not disclose the internal table name");
        await AssertSeedRowUnchangedAsync(schema);
    }

    [IntegrationTheory]
    [Operation(Operations.Update)]
    [Operation(Operations.Delete)]
    [Protocol(TestProtocols.Wfs20)]
    [Endpoint("POST /wfs")]
    [InlineData("<wfs:Update typeName=\"{0}\"><wfs:Property><wfs:ValueReference>name</wfs:ValueReference><wfs:Value>renamed</wfs:Value></wfs:Property></wfs:Update>")]
    [InlineData("<wfs:Delete typeName=\"{0}\"><fes:Filter><fes:PropertyIsEqualTo><fes:ValueReference>name</fes:ValueReference><fes:Literal>seed</fes:Literal></fes:PropertyIsEqualTo></fes:Filter></wfs:Delete>")]
    public async Task WfsTransaction_LayerServedFromSourceTable_IsRefusedNotReportedAsZeroEdits(string actionTemplate)
    {
        var schema = await PublishSourceTableAsync();
        using var client = _fixture.CreateAdminClient();
        var typeName = await GetWfsTypeNameAsync(client);

        using var content = new StringContent(
            "<wfs:Transaction service=\"WFS\" version=\"2.0.0\" xmlns:wfs=\"http://www.opengis.net/wfs/2.0\" " +
            "xmlns:fes=\"http://www.opengis.net/fes/2.0\">" + string.Format(CultureInfo.InvariantCulture, actionTemplate, typeName) +
            "</wfs:Transaction>",
            Encoding.UTF8,
            "application/xml");
        using var response = await client.PostAsync("/wfs", content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented, body);
        body.Should().Contain("OperationNotSupported").And.NotContain("TransactionResponse").And.NotContain(SourceTable);
        await AssertSeedRowUnchangedAsync(schema);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    [Endpoint("POST /services")]
    public async Task Metadata_LayerServedFromSourceTable_DoesNotAdvertiseEditing()
    {
        await PublishSourceTableAsync();
        using var client = _fixture.CreateAdminClient();

        foreach (var path in new[] { "FeatureServer", "FeatureServer/1" })
        {
            var body = await client.GetStringAsync($"/rest/services/{ServiceName}/{path}?f=json");
            using var json = JsonDocument.Parse(body);
            var capabilities = json.RootElement.GetProperty("capabilities").GetString()!.Split(',');

            capabilities.Should().Contain("Query", body);
            capabilities.Should().NotContain(["Create", "Update", "Delete", "Editing"], body);
        }

        var listed = await ListedFeatureServerCapabilitiesAsync(client);
        listed.Should().Contain("Query").And.NotContain(["Create", "Update", "Delete", "Editing"]);
    }

    // The SOAP service catalog carries each FeatureServer's capabilities string.
    private static async Task<string[]> ListedFeatureServerCapabilitiesAsync(HttpClient client)
    {
        const string request = """
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body><GetServiceDescriptions xmlns="http://www.esri.com/schemas/ArcGIS/10.8"/></soap:Body>
            </soap:Envelope>
            """;
        using var content = new StringContent(request, Encoding.UTF8, "text/xml");
        using var response = await client.PostAsync("/services", content);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var description = XDocument.Parse(body).Descendants()
            .Where(static element => element.Name.LocalName == "ServiceDescription")
            .Single(element => Child(element, "Name") == ServiceName && Child(element, "Type") == "FeatureServer");
        return Child(description, "Capabilities")!.Split(',');
    }

    private async Task AssertSeedRowUnchangedAsync(string schema)
        => (await CountAsync(schema, $"SELECT COUNT(*) FROM {SourceTable} WHERE objectid = 1 AND name = 'seed'"))
            .Should().Be(1, "a refused edit must leave the source row as it was");

    private static async Task<string> GetWfsTypeNameAsync(HttpClient client)
    {
        var payload = await client.GetStringAsync("/wfs?service=WFS&version=2.0.0&request=GetCapabilities");
        return XDocument.Parse(payload).Descendants()
            .Where(static element => element.Name.LocalName == "FeatureType")
            .Select(static featureType => featureType.Elements().First(static child => child.Name.LocalName == "Name").Value)
            .Single();
    }

    private static string? Child(XElement element, string name)
        => element.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value;

    private async Task<string> PublishSourceTableAsync()
    {
        var schema = _fixture.CurrentSchema!;
        await ExecuteAsync(schema, $"""
            CREATE TABLE {SourceTable} (objectid bigint PRIMARY KEY, name text NOT NULL, geom geometry(Point, 4326) NOT NULL);
            INSERT INTO {SourceTable} VALUES (1, 'seed', ST_SetSRID(ST_MakePoint(10, 20), 4326));
            """);
        _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>()
            .SetGraph(BuildGraph(schema), schema: schema);
        return schema;
    }

    private async Task ExecuteAsync(string schema, string sql)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string schema, string sql)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static MetadataV2Graph BuildGraph(string schema)
    {
        const string resourceId = "res-source-edits";
        const string bindingId = "binding-source-edits";
        const string serviceId = "svc-source-edits";
        var open = new AccessPolicy { AllowAnonymous = true, AllowAnonymousWrite = true };

        return new TestMetadataV2GraphBuilder()
            .AddResource(resourceId, "source_edits", MetadataV2ObjectType.FeatureDataset, accessPolicy: open,
                fields: Fields(), spatial: PointSpatial())
            .AddStorageBinding(
                bindingId,
                resourceId,
                SourceTable,
                storageLayerId: StorageLayerId,
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
                accessPolicy: open,
                protocols: [MetadataV2ServiceProtocols.FeatureServer, MetadataV2ServiceProtocols.Wfs20],
                options: new Dictionary<string, JsonElement>
                {
                    ["capabilities"] = JsonSerializer.SerializeToElement(new[] { "Query", "Create", "Update", "Delete", "Editing" })
                })
            .AddPublication("source_edits", serviceId, resourceId, layerIndex: 1, storageBindingId: bindingId,
                publicationType: MetadataV2LayerType.EsriFeatureLayer)
            .Build();
    }

    private static MetadataV2Field[] Fields() =>
    [
        new() { Name = "objectid", Type = MetadataV2FieldType.BigInteger, Nullable = false, SemanticRoles = ["id.primary"] },
        new() { Name = "name", Type = MetadataV2FieldType.String, Nullable = false },
        new() { Name = "geom", Type = MetadataV2FieldType.Geometry, Nullable = false, SemanticRoles = ["geometry.primary"] }
    ];

    private static MetadataV2ObjectSpatial PointSpatial() => new()
    {
        SpatialReference = MetadataV2SpatialReference.Wgs84,
        GeometryType = MetadataV2GeometryType.Point,
        PrimaryGeometryField = "geom",
        SupportedCrs = [MetadataV2SpatialReference.Wgs84]
    };
}
