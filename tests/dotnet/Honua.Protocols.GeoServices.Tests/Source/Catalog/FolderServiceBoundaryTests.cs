// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class FolderServiceBoundaryTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public FolderServiceBoundaryTests(WebAppFixture fixture) => _fixture = fixture;

    [IntegrationTheory]
    [InlineData("MapServer/WMS", "WMS", "1.3.0")]
    [InlineData("MapServer/WMTS", "WMTS", "1.0.0")]
    [InlineData("ImageServer/WCS", "WCS", "2.0.1")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}/MapServer/WMS")]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}/MapServer/WMTS")]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}/ImageServer/WCS")]
    [InterfaceOperation(TestProtocols.Wms13, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wmts10, "GetCapabilities")]
    [InterfaceOperation(TestProtocols.Wcs201, "GetCapabilities")]
    public async Task Capabilities_HistoricalAliasWithNewProtocolFacet_DoesNotRebind(string suffix, string protocol, string version)
    {
        var original = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var graph = FolderServiceTestCatalog.WithPublicParcels(original);
        var priorFacet = graph.Services.First(service => service.Metadata.Name == WebAppFixture.TestServiceId);
        var futureFacet = priorFacet with
        {
            Metadata = priorFacet.Metadata with { Id = "future-protocol-facet" },
            Protocols = [ServiceProtocols.Wms, ServiceProtocols.Wmts, ServiceProtocols.Wcs],
            PublicationIds = []
        };
        FolderServiceTestCatalog.ReplaceGraph(_fixture, graph with
        {
            Services = [futureFacet, .. graph.Services.Select(service => service.Metadata.Name == WebAppFixture.TestServiceId
                ? service with { Protocols = [] } : service)],
            ServicePathAliases = [new() { FolderName = "Previous", PublicName = "Parcels", TargetFacetIds = [priorFacet.Metadata.Id] }]
        });
        try
        {
            using var response = await _fixture.Client.GetAsync(
                $"/rest/services/Previous/Parcels/{suffix}?SERVICE={protocol}&REQUEST=GetCapabilities&VERSION={version}");
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, body);
            var document = XDocument.Parse(body);
            document.Root!.Name.LocalName.Should().BeOneOf("ServiceExceptionReport", "ExceptionReport");
            body.Should().Contain("Service was not found.");
            body.Should().NotContain($"'{WebAppFixture.TestServiceId}'");
        }
        finally
        {
            FolderServiceTestCatalog.ReplaceGraph(_fixture, original);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ValidateSql)]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}/FeatureServer/validateSQL")]
    public async Task ValidateSql_EmptyFolderService_DoesNotExposeLogicalName()
    {
        var original = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var graph = FolderServiceTestCatalog.WithPublicParcels(original);
        var serviceIds = graph.Services.Where(service => service.Metadata.Name == WebAppFixture.TestServiceId)
            .Select(service => service.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        var featureFacetId = graph.Services.First(service => serviceIds.Contains(service.Metadata.Id) &&
            service.Protocols.Contains(ServiceProtocols.FeatureServer)).Metadata.Id;
        FolderServiceTestCatalog.ReplaceGraph(_fixture, graph with
        {
            Services = graph.Services.Select(service => serviceIds.Contains(service.Metadata.Id)
                ? service with { PublicationIds = [], Protocols = service.Metadata.Id == featureFacetId ? [ServiceProtocols.FeatureServer] : [] }
                : service).ToArray(),
            Publications = graph.Publications.Where(publication => !serviceIds.Contains(publication.ServiceId)).ToArray()
        });
        try
        {
            using var response = await _fixture.Client.GetAsync("/rest/services/Public/Parcels/FeatureServer/validateSQL?sql=1%3D1&f=json");
            await AssertPublicServiceErrorAsync(response);
        }
        finally
        {
            FolderServiceTestCatalog.ReplaceGraph(_fixture, original);
        }
    }

    [IntegrationTest]
    [Operation(Operations.QueryRelatedRecords)]
    [Endpoint("GET /rest/services/{folderName}/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords")]
    public async Task RelatedRecords_UnavailableRelatedLayer_DoesNotExposeLogicalName()
    {
        var original = _fixture.GetCurrentV2GraphSnapshot().Graph;
        var graph = FolderServiceTestCatalog.WithPublicParcels(original);
        var service = graph.Services.First(service => service.Metadata.Name == WebAppFixture.TestServiceId &&
            service.Protocols.Contains(ServiceProtocols.FeatureServer));
        var publication = graph.Publications.First(candidate => candidate.ServiceId == service.Metadata.Id &&
            candidate.LayerIndex == WebAppFixture.TestLayerId);
        FolderServiceTestCatalog.ReplaceGraph(_fixture, graph with
        {
            Resources = graph.Resources.Select(resource => resource.Metadata.Id == publication.ResourceId
                ? resource with { Relationships = [new() { Id = "missing-related", EsriRelationshipId = 7, RelatedResourceId = "absent" }] }
                : resource).ToArray()
        });
        try
        {
            using var response = await _fixture.Client.GetAsync(
                $"/rest/services/Public/Parcels/FeatureServer/{WebAppFixture.TestLayerId}/queryRelatedRecords?objectIds=1&relationshipId=7&f=json");
            await AssertPublicServiceErrorAsync(response);
        }
        finally
        {
            FolderServiceTestCatalog.ReplaceGraph(_fixture, original);
        }
    }

    private static async Task AssertPublicServiceErrorAsync(HttpResponseMessage response)
    {
        await response.AssertGeoServicesErrorAsync(404);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var details = document.RootElement.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty).ToArray();
        details.Should().Contain(message => message.Contains("Parcels", StringComparison.Ordinal));
        details.Should().NotContain(message => message.Contains($"'{WebAppFixture.TestServiceId}'", StringComparison.Ordinal));
    }
}
