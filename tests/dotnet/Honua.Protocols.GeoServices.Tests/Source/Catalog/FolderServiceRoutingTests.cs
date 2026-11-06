// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FolderServiceRoutingTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public FolderServiceRoutingTests(WebAppFixture fixture) => _fixture = fixture;

    [IntegrationTheory]
    [InlineData("GET")]
    [InlineData("POST")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}/FeatureServer")]
    [Endpoint("POST /rest/services/{folderName}/{serviceId}/FeatureServer")]
    public async Task ServiceMetadata_PublicLeafDiffersFromLogicalIdentity_ResolvesFolderPath(string method)
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        SetGraph(OrganizeService(snapshot.Graph));
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), "/rest/services/Public/Parcels/FeatureServer?f=json");
            using var response = await _fixture.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            using var document = JsonDocument.Parse(body);
            document.RootElement.TryGetProperty("serviceName", out var serviceName).Should().BeTrue(body);
            serviceName.GetString().Should().Be("Parcels");
            using var internalResponse = await _fixture.Client.GetAsync($"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer?f=json");
            await internalResponse.AssertGeoServicesErrorAsync(404);
            using var mismatchedResponse = await _fixture.Client.GetAsync("/rest/services/Unknown/Parcels/FeatureServer?f=json");
            await mismatchedResponse.AssertGeoServicesErrorAsync(404);
        }
        finally
        {
            SetGraph(snapshot.Graph);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    [Endpoint("GET /rest/services/{folderName}")]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}")]
    public async Task ServicesDirectory_PublicFolder_ReturnsOnlyCanonicalQualifiedEntries()
    {
        var original = _fixture.GetCurrentV2GraphSnapshot().Graph;
        SetGraph(OrganizeService(original));
        try
        {
            using var root = await ReadJsonAsync("/rest/services?f=json");
            root.RootElement.GetProperty("folders").EnumerateArray().Select(item => item.GetString()).Should().Contain("Public");
            root.RootElement.GetProperty("services").EnumerateArray()
                .Should().NotContain(item => item.GetProperty("name").GetString() == "Public/Parcels");
            using var folder = await ReadJsonAsync("/rest/services/Public?f=json");
            var entries = folder.RootElement.GetProperty("services").EnumerateArray().ToArray();
            entries.Should().NotBeEmpty();
            foreach (var entry in entries)
            {
                entry.GetProperty("name").GetString().Should().Be("Public/Parcels");
                entry.GetProperty("url").GetString().Should().Contain("/rest/services/Public/Parcels/");
            }
            using var node = await ReadJsonAsync("/rest/services/Public/Parcels?f=json");
            node.RootElement.GetProperty("services").GetRawText().Should().Be(folder.RootElement.GetProperty("services").GetRawText());
        }
        finally
        {
            SetGraph(original);
        }
    }

    [IntegrationTheory]
    [InlineData("WMS", "1.3.0")]
    [InlineData("WMTS", "1.0.0")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}/MapServer/WMS")]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}/MapServer/WMTS")]
    public async Task Capabilities_PublicFolder_AdvertisesReachableIncomingBase(string protocol, string version)
    {
        var original = _fixture.GetCurrentV2GraphSnapshot().Graph;
        SetGraph(OrganizeService(original));
        try
        {
            var path = $"/rest/services/Public/Parcels/MapServer/{protocol}";
            using var response = await _fixture.Client.GetAsync($"{path}?SERVICE={protocol}&REQUEST=GetCapabilities&VERSION={version}");
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            var document = XDocument.Parse(body);
            document.Root!.Name.LocalName.Should().Be(protocol == "WMS" ? "WMS_Capabilities" : "Capabilities");
            document.Descendants().Attributes(XName.Get("href", "http://www.w3.org/1999/xlink"))
                .Should().Contain(attribute => attribute.Value.Contains(path, StringComparison.Ordinal));
            body.Should().NotContain($"/rest/services/{WebAppFixture.TestServiceId}/");
        }
        finally
        {
            SetGraph(original);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [InterfaceOperation(TestProtocols.GeoservicesCatalog, "GetFolders")]
    [InterfaceOperation(TestProtocols.GeoservicesCatalog, "GetServiceDescriptionsEx")]
    [Endpoint("POST /services")]
    public async Task SoapCatalog_PublicFolder_ScopesQualifiedNamesAndAddresses()
    {
        var original = _fixture.GetCurrentV2GraphSnapshot().Graph;
        SetGraph(OrganizeService(original));
        try
        {
            var folders = await ReadSoapAsync("GetFolders", string.Empty);
            folders.Descendants().Where(element => element.Name.LocalName == "String")
                .Select(element => element.Value).Should().Contain("Public");
            var descriptions = await ReadSoapAsync("GetServiceDescriptionsEx", "<FolderName>Public</FolderName>");
            var names = descriptions.Descendants().Where(element => element.Name.LocalName == "Name").ToArray();
            names.Should().NotBeEmpty();
            names.Should().OnlyContain(element => element.Value == "Public/Parcels");
            descriptions.Descendants().Where(element => element.Name.LocalName is "Url" or "RestUrl")
                .Should().OnlyContain(element => element.Value.Contains("/Public/Parcels/", StringComparison.Ordinal));
        }
        finally
        {
            SetGraph(original);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{folderName}")]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}/FeatureServer")]
    public async Task FolderAccess_HiddenService_DoesNotExposeFolderOrBypassPolicy()
    {
        var original = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var organized = OrganizeService(original);
        SetGraph(organized with
        {
            Services = organized.Services.Select(service => service.Metadata.Name == WebAppFixture.TestServiceId
                ? service with { AccessPolicy = new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["folder-reader"] } }
                : service).ToArray()
        });
        try
        {
            using var anonymous = _fixture.CreateClient();
            using var folder = await anonymous.GetAsync("/rest/services/Public?f=json");
            await folder.AssertGeoServicesErrorAsync(404);
            using var service = await anonymous.GetAsync("/rest/services/Public/Parcels/FeatureServer?f=json");
            await service.AssertGeoServicesErrorAsync(401);
        }
        finally
        {
            SetGraph(original);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{folderName}/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_MoveRenameAndReturnToRoot_PreservesDataAtHistoricalPaths()
    {
        var original = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var rootPath = $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query";
        try
        {
            var expectedIds = await QueryIdsAsync(rootPath);
            var folder = new MetadataV2Folder { Metadata = new() { Id = "folder-routing", Name = "Public" } };
            var graph = MetadataV2FolderMutations.Create(original, folder);
            graph = MetadataV2FolderMutations.Move(graph, WebAppFixture.TestServiceId, folder.Metadata.Id);
            SetGraph(graph);
            var folderPath = $"/rest/services/Public/{WebAppFixture.TestServiceId}/FeatureServer/{WebAppFixture.TestLayerId}/query";
            (await QueryIdsAsync(folderPath)).Should().Be(expectedIds);
            (await QueryIdsAsync(rootPath)).Should().Be(expectedIds);

            graph = MetadataV2FolderMutations.Update(graph, folder with { Metadata = folder.Metadata with { Name = "Shared" } });
            SetGraph(graph);
            var renamedPath = folderPath.Replace("/Public/", "/Shared/", StringComparison.Ordinal);
            (await QueryIdsAsync(renamedPath)).Should().Be(expectedIds);
            (await QueryIdsAsync(folderPath)).Should().Be(expectedIds);

            graph = MetadataV2FolderMutations.Move(graph, WebAppFixture.TestServiceId, null);
            SetGraph(graph);
            (await QueryIdsAsync(rootPath)).Should().Be(expectedIds);
            (await QueryIdsAsync(folderPath)).Should().Be(expectedIds);
            (await QueryIdsAsync(renamedPath)).Should().Be(expectedIds);
            using var emptyFolder = await _fixture.Client.GetAsync("/rest/services/Shared?f=json");
            await emptyFolder.AssertGeoServicesErrorAsync(404);
            using var root = await ReadJsonAsync("/rest/services?f=json");
            root.RootElement.GetProperty("folders").EnumerateArray().Select(item => item.GetString()).Should().NotContain("Shared");
        }
        finally
        {
            SetGraph(original);
        }
    }

    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task NativeFeatures_FolderMove_PreservesCollectionIdsAndData()
    {
        var original = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var path = $"/ogc/features/collections/{WebAppFixture.TestLayerId}/items?limit=1";
        using var before = await ReadJsonAsync(path);
        before.RootElement.GetProperty("features").GetArrayLength().Should().BeGreaterThan(0);
        SetGraph(OrganizeService(original));
        try
        {
            using var after = await ReadJsonAsync(path);
            JsonElement.DeepEquals(before.RootElement.GetProperty("features"), after.RootElement.GetProperty("features"))
                .Should().BeTrue();
        }
        finally
        {
            SetGraph(original);
        }
    }

    private async Task<string> QueryIdsAsync(string path)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["where"] = "1=1", ["returnIdsOnly"] = "true", ["f"] = "json"
        });
        using var response = await _fixture.Client.PostAsync(path, form);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("objectIds", out var ids).Should().BeTrue(body);
        ids.GetArrayLength().Should().BeGreaterThan(0);
        return ids.GetRawText();
    }

    private async Task<JsonDocument> ReadJsonAsync(string path)
    {
        using var response = await _fixture.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    private async Task<XDocument> ReadSoapAsync(string operation, string arguments)
    {
        using var body = new StringContent(
            $"<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><{operation} xmlns=\"http://www.esri.com/schemas/ArcGIS/10.8\">{arguments}</{operation}></soap:Body></soap:Envelope>",
            Encoding.UTF8, "text/xml");
        using var response = await _fixture.Client.PostAsync("/services", body);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return XDocument.Parse(text);
    }

    private static MetadataV2Graph OrganizeService(MetadataV2Graph graph)
        => graph with
        {
            Folders = [new() { Metadata = new() { Id = "folder-routing", Name = "Public" } }],
            Services = graph.Services.Select(service => service.Metadata.Name == WebAppFixture.TestServiceId
                ? service with { FolderId = "folder-routing", PublicName = "Parcels" } : service).ToArray()
        };

    private void SetGraph(MetadataV2Graph graph)
        => ((TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>())
            .SetGraph(graph, schema: _fixture.CurrentSchema);
}
