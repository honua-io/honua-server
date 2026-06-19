// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.GeoservicesCatalog)]
public sealed class GeoservicesCatalogEndpointTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public GeoservicesCatalogEndpointTests(WebAppFixture fixture) => _fixture = fixture;

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_DefaultFormat_ReturnsCatalogPayload()
    {
        var response = await _fixture.Client.GetAsync("/rest/services");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // Honua does not advertise an ArcGIS Server version (see NoArcGisServerVersionTests).
        payload.RootElement.TryGetProperty("currentVersion", out _).Should().BeFalse();
        payload.RootElement.TryGetProperty("fullVersion", out _).Should().BeFalse();
        payload.RootElement.TryGetProperty("folders", out var folders).Should().BeTrue();
        folders.ValueKind.Should().Be(JsonValueKind.Array);
        payload.RootElement.TryGetProperty("services", out var services).Should().BeTrue();
        services.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_PjsonFormat_ReturnsCatalogPayload()
    {
        var response = await _fixture.Client.GetAsync("/rest/services?f=pjson");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_InvalidFormat_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync("/rest/services?f=xml");

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/info")]
    public async Task GetRestInfo_DefaultFormat_ReturnsRootInfo()
    {
        var response = await _fixture.Client.GetAsync("/rest/info");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // Honua does not advertise an ArcGIS Server version (see NoArcGisServerVersionTests).
        payload.RootElement.TryGetProperty("currentVersion", out _).Should().BeFalse();
        payload.RootElement.TryGetProperty("fullVersion", out _).Should().BeFalse();
        payload.RootElement.TryGetProperty("authInfo", out var authInfo).Should().BeTrue();
        authInfo.TryGetProperty("isTokenBasedSecurity", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_IncludesServicesWithExpectedStructure()
    {
        var response = await _fixture.Client.GetAsync("/rest/services?f=json");

        response.Be200Ok();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;

        root.TryGetProperty("services", out var services).Should().BeTrue();
        services.ValueKind.Should().Be(JsonValueKind.Array);

        if (services.GetArrayLength() > 0)
        {
            var firstService = services[0];
            firstService.TryGetProperty("name", out _).Should().BeTrue();
            firstService.TryGetProperty("type", out _).Should().BeTrue();
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_IncludesVectorTileServerEntry()
    {
        var response = await _fixture.Client.GetAsync("/rest/services?f=json");

        response.Be200Ok();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var vectorTileEntries = payload.RootElement
            .GetProperty("services")
            .EnumerateArray()
            .Where(service =>
                service.TryGetProperty("type", out var type) &&
                string.Equals(type.GetString(), "VectorTileServer", StringComparison.Ordinal))
            .ToArray();

        // The default test graph seeds an EsriVectorTileLayer publication on the "test"
        // service, so the catalog must advertise a VectorTileServer entry with a
        // service-name-scoped URL, matching every other service type.
        vectorTileEntries.Should().NotBeEmpty();
        vectorTileEntries.Should().OnlyContain(service =>
            service.GetProperty("url").GetString()!.EndsWith(
                "/rest/services/test/VectorTileServer", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services")]
    public async Task GetServicesDirectory_ImageServerEntriesUseServiceScopedUrls()
    {
        var rasterStore = Substitute.For<IRasterStore>();
        rasterStore.ListRastersAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(new[]
            {
                new RasterInfo
                {
                    Id = 1,
                    LayerId = callInfo.ArgAt<int>(0),
                    Name = "test-raster",
                    Width = 256,
                    Height = 256,
                    BandCount = 3,
                    PixelType = "8BUI",
                    Srid = 4326,
                    CreatedAt = DateTimeOffset.UtcNow
                }
            }));

        var fixture = new WebAppFixture()
            .ConfigureServices(services => services.AddSingleton(rasterStore));

        await fixture.InitializeAsync();
        try
        {
            var response = await fixture.Client.GetAsync("/rest/services?f=json");

            response.Be200Ok();

            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var imageServerUrls = payload.RootElement
                .GetProperty("services")
                .EnumerateArray()
                .Where(service =>
                    service.TryGetProperty("type", out var type) &&
                    string.Equals(type.GetString(), "ImageServer", StringComparison.Ordinal))
                .Select(service => service.GetProperty("url").GetString())
                .ToArray();

            imageServerUrls.Should().NotBeEmpty();
            // ImageServer URLs are service-name scoped (canonical ArcGIS addressing,
            // matching every other service type), not numeric-layer-id scoped.
            imageServerUrls.Should().OnlyContain(url =>
                !string.IsNullOrWhiteSpace(url) &&
                System.Text.RegularExpressions.Regex.IsMatch(url, @".*/rest/services/[^/]+/ImageServer$"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
