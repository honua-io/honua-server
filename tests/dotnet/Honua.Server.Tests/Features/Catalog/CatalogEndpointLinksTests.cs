// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Catalog;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.GetMetadata)]
public sealed class CatalogEndpointLinksTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Enterprise);

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var provider = (TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(snapshot.Graph with { Catalogs = [new()
        {
            Metadata = new() { Id = "links", Name = "Links" }, Target = "discovery",
            Configuration = new() { DefaultListing = "automatic", Scope = new() { Kinds = ["service", "layer"] } }
        }] }, schema: _fixture.CurrentSchema);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items")]
    public async Task ListItems_ManifestProtocolEndpoints_EveryAdvertisedLinkResolves()
    {
        using var response = await _fixture.Client.GetAsync("/api/v1/catalogs/links/items?kind=service");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var endpoints = document.RootElement.GetProperty("items").EnumerateArray()
            .SelectMany(item => item.GetProperty("endpoints").EnumerateArray()).ToArray();
        Assert.NotEmpty(endpoints);
        Assert.Contains(endpoints, endpoint => endpoint.GetProperty("protocol").GetString() == ServiceProtocols.FeatureServer);
        Assert.Contains(endpoints, endpoint => endpoint.GetProperty("protocol").GetString() == ServiceProtocols.OgcFeatures);
        foreach (var href in endpoints.Select(endpoint => endpoint.GetProperty("href").GetString()!).Distinct(StringComparer.Ordinal))
        {
            using var target = await _fixture.Client.GetAsync(href);
            await AssertSuccessfulMetadataAsync(target);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/catalogs/{catalogId}/items")]
    public async Task ListItems_ServiceInFolder_UsesSharedPublicPathAndResolvableEndpoint()
    {
        var snapshot = _fixture.GetCurrentV2GraphSnapshot();
        var graph = snapshot.Graph;
        var service = snapshot.FindService(WebAppFixture.TestServiceId);
        Assert.NotNull(service);
        var provider = (TestMetadataV2GraphProvider)_fixture.GetService<IMetadataV2GraphProvider>();
        provider.SetGraph(graph with
        {
            Folders = [.. graph.Folders, new() { Metadata = new() { Id = "survey", Name = "survey" } }],
            Services = graph.Services.Select(current => current.Metadata.Name == service.Metadata.Name &&
                MetadataV2ServicePaths.SameTenant(current.Metadata.Tenant, service.Metadata.Tenant)
                ? current with { FolderId = "survey", PublicName = "parcels" } : current).ToArray()
        }, schema: _fixture.CurrentSchema);
        using var response = await _fixture.Client.GetAsync("/api/v1/catalogs/links/items/service?id=" + Uri.EscapeDataString(service.Metadata.Id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var endpoint = Assert.Single(document.RootElement.GetProperty("endpoints").EnumerateArray(),
            endpoint => endpoint.GetProperty("protocol").GetString() == ServiceProtocols.FeatureServer);
        var href = endpoint.GetProperty("href").GetString()!;
        Assert.Contains("/rest/services/survey/parcels/FeatureServer", href, StringComparison.Ordinal);
        using var target = await _fixture.Client.GetAsync(href);
        await AssertSuccessfulMetadataAsync(target);
    }

    private static async Task AssertSuccessfulMetadataAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var body = await response.Content.ReadAsStringAsync();
        if (mediaType.EndsWith("json", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(body);
            Assert.False(document.RootElement.TryGetProperty("error", out _));
            return;
        }
        Assert.True(mediaType.EndsWith("xml", StringComparison.OrdinalIgnoreCase));
        var root = XDocument.Parse(body).Root;
        Assert.NotNull(root);
        Assert.DoesNotContain(root.Name.LocalName, new[] { "ExceptionReport", "ServiceExceptionReport" });
    }
}
