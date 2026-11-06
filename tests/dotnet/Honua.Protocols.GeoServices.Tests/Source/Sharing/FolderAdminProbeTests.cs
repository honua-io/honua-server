// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Sharing;

[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.GeoservicesCatalog)]
[Operation(Operations.GetMetadata)]
public sealed class FolderAdminProbeTests(WebAppFixture fixture) : IClassFixture<WebAppFixture>
{
    private long _revision = 100;

    [IntegrationTheory]
    [InlineData("GET", "/rest/admin")]
    [InlineData("POST", "/rest/admin")]
    [InlineData("GET", "/admin/services")]
    [InlineData("POST", "/admin/services")]
    [Endpoint("GET /rest/admin/{serviceName}.{serviceType}")]
    [Endpoint("POST /rest/admin/{serviceName}.{serviceType}")]
    [Endpoint("GET /admin/services/{serviceName}.{serviceType}")]
    [Endpoint("POST /admin/services/{serviceName}.{serviceType}")]
    [Endpoint("GET /rest/admin/{folderName}/{serviceName}.{serviceType}")]
    [Endpoint("POST /rest/admin/{folderName}/{serviceName}.{serviceType}")]
    [Endpoint("GET /admin/services/{folderName}/{serviceName}.{serviceType}")]
    [Endpoint("POST /admin/services/{folderName}/{serviceName}.{serviceType}")]
    public async Task ServiceProbe_FolderPublicName_ResolvesLeafAndQualifiedSpellings(string method, string prefix)
    {
        var original = fixture.GetCurrentV2GraphSnapshot().Graph;
        SetGraph(Organize(original));
        try
        {
            await AssertPublicServiceAsync($"{prefix}/Parcels.MapServer", method);
            await AssertPublicServiceAsync($"{prefix}/Public/Parcels.MapServer", method);
        }
        finally
        {
            SetGraph(original);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/admin/{serviceName}.{serviceType}")]
    [Endpoint("GET /rest/admin/{folderName}/{serviceName}.{serviceType}")]
    public async Task ServiceProbe_DuplicatePublicLeaves_RequiresQualifiedFolder()
    {
        var original = fixture.GetCurrentV2GraphSnapshot().Graph;
        var graph = Organize(original);
        var other = graph.Services.First(service => service.Metadata.Name == WebAppFixture.TestServiceId) with
        {
            Metadata = new() { Id = "other-service", Name = "other-logical-service" },
            FolderId = "other-folder",
            PublicationIds = []
        };
        SetGraph(graph with
        {
            Folders = [.. graph.Folders, new() { Metadata = new() { Id = "other-folder", Name = "Other" } }],
            Services = [.. graph.Services, other]
        });
        try
        {
            using var response = await fixture.Client.GetAsync("/rest/admin/Parcels.MapServer?f=json");
            await response.AssertGeoServicesErrorAsync(404);
            await AssertPublicServiceAsync("/rest/admin/Public/Parcels.MapServer");
        }
        finally
        {
            SetGraph(original);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/admin/{serviceName}.{serviceType}")]
    public async Task ServiceProbe_RetiredRootReservation_DoesNotRebindToFolderLeaf()
    {
        var original = fixture.GetCurrentV2GraphSnapshot().Graph;
        var graph = Organize(original);
        SetGraph(graph with
        {
            ServicePathAliases = [.. graph.ServicePathAliases, new()
            {
                PublicName = "Parcels", TargetFacetIds = ["retired-service"], Retired = true
            }]
        });
        try
        {
            using var response = await fixture.Client.GetAsync("/rest/admin/Parcels.MapServer?f=json");
            await response.AssertGeoServicesErrorAsync(404);
            await AssertPublicServiceAsync("/rest/admin/Public/Parcels.MapServer");
        }
        finally
        {
            SetGraph(original);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/admin/{serviceName}.{serviceType}")]
    public async Task ServiceProbe_NumericLayerSpelling_RemainsReadableAfterFolderMove()
    {
        var original = fixture.GetCurrentV2GraphSnapshot().Graph;
        SetGraph(Organize(original));
        try
        {
            await AssertPublicServiceAsync($"/rest/admin/{WebAppFixture.TestLayerId}.MapServer");
        }
        finally
        {
            SetGraph(original);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /rest/admin/{serviceName}.{serviceType}")]
    public async Task ServiceProbe_MetadataIdentity_RemainsReadableAfterFolderMove()
    {
        var original = fixture.GetCurrentV2GraphSnapshot().Graph;
        var metadataId = original.Services.First(service => service.Metadata.Name == WebAppFixture.TestServiceId).Metadata.Id;
        SetGraph(Organize(original));
        try
        {
            await AssertPublicServiceAsync($"/rest/admin/{Uri.EscapeDataString(metadataId)}.MapServer");
        }
        finally
        {
            SetGraph(original);
        }
    }

    private async Task AssertPublicServiceAsync(string path, string method = "GET")
    {
        using var client = fixture.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path + "?f=json");
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("serviceName").GetString().Should().Be("Parcels");
    }

    private static MetadataV2Graph Organize(MetadataV2Graph graph)
        => graph with
        {
            Folders = [new() { Metadata = new() { Id = "folder", Name = "Public" } }],
            Services = graph.Services.Select(service => service.Metadata.Name == WebAppFixture.TestServiceId
                ? service with { FolderId = "folder", PublicName = "Parcels" } : service).ToArray()
        };

    private void SetGraph(MetadataV2Graph graph)
        => ((TestMetadataV2GraphProvider)fixture.GetService<IMetadataV2GraphProvider>())
            .SetGraph(graph with { Revision = ++_revision }, schema: fixture.CurrentSchema);
}
