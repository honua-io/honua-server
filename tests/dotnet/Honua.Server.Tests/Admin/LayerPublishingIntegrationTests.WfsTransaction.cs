// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    // A WFS client takes its type names from GetCapabilities. The advertised
    // type of an editable layer must accept the Transaction that the other protocols accept.
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Protocol(TestProtocols.Admin, TestProtocols.Wfs20)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishLayer_IntoManagedStore_AcceptsWfsTransactionThroughTheAdvertisedType()
    {
        await UseServerFeatureStoreConnectionAsync();
        var publishedLayer = await PublishLayerAsync(new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = $"Layer {_tableName}",
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = _idNamePopulationFields,
            ServiceName = _serviceName,
            Enabled = true,
            StorageMode = "managed",
            Capabilities = ["Query", "Create", "Update", "Delete"]
        });
        _layerId = publishedLayer.StorageLayerId;

        var typeName = await GetAdvertisedWfsTypeNameAsync(_layerId.Value);
        typeName.Should().NotContain("pub-stac-", "the STAC publication does not carry the edit capabilities");

        var inserted = await PostWfsTransactionAsync(
            $"<wfs:Insert><{typeName}><honua:name>Wfs Feature</honua:name></{typeName}></wfs:Insert>");
        inserted.Should().Contain("<wfs:totalInserted>1</wfs:totalInserted>");
        (await GetManagedStoreRowCountAsync(_layerId.Value)).Should().Be(2);

        var deleted = await PostWfsTransactionAsync(
            $"<wfs:Delete typeName=\"{typeName}\"><fes:Filter><fes:PropertyIsEqualTo>" +
            "<fes:ValueReference>name</fes:ValueReference><fes:Literal>Wfs Feature</fes:Literal>" +
            "</fes:PropertyIsEqualTo></fes:Filter></wfs:Delete>");
        deleted.Should().Contain("<wfs:totalDeleted>1</wfs:totalDeleted>");
        (await GetManagedStoreRowCountAsync(_layerId.Value)).Should().Be(1);
    }

    private async Task<string> GetAdvertisedWfsTypeNameAsync(int layerId)
    {
        var response = await _client.GetAsync("/wfs?service=WFS&version=2.0.0&request=GetCapabilities");
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {payload}");

        var suffix = $"-layer-{layerId}";
        return XDocument.Parse(payload)
            .Descendants()
            .Where(static element => element.Name.LocalName == "FeatureType")
            .Select(static featureType => featureType.Elements().First(static child => child.Name.LocalName == "Name").Value)
            .Should().ContainSingle(name => name.EndsWith(suffix, StringComparison.Ordinal), $"capabilities: {payload}")
            .Which;
    }

    private async Task<string> PostWfsTransactionAsync(string action)
    {
        using var content = new StringContent(
            "<wfs:Transaction service=\"WFS\" version=\"2.0.0\" " +
            "xmlns:wfs=\"http://www.opengis.net/wfs/2.0\" " +
            "xmlns:fes=\"http://www.opengis.net/fes/2.0\" " +
            "xmlns:honua=\"http://honua.io/wfs\">" + action + "</wfs:Transaction>",
            Encoding.UTF8,
            "application/xml");
        var response = await _client.PostAsync("/wfs", content);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"response: {payload}");
        return payload;
    }
}
