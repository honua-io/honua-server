// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.Zarr;

/// <summary>
/// Endpoint-level coverage for the datacube (Zarr coverage) slice -> tile render route (#1835),
/// exercising the public GET so it is backed by a real HTTP request (EndpointRegistryDriftTests).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Tile)]
public sealed class DatacubeTileEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private readonly WebAppFixture _anonymousFixture = new WebAppFixture()
        .UseSeed("tests/seed/server.yaml")
        .ReplaceService<IMetadataV2GraphProvider>(BuildProtectedLayerGraphProvider())
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
        });
    private HttpClient _client = null!;
    private HttpClient _anonymousClient = null!;

    private static TestMetadataV2GraphProvider BuildProtectedLayerGraphProvider()
        => new TestMetadataV2GraphBuilder()
            // An unrelated public resource has a storage id matching the protected
            // publication index. Its policy must never authorize the tile request.
            .AddResource(
                "res-public-collision",
                "Unrelated Public Raster",
                MetadataV2ResourceType.RasterDataset,
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddStorageBinding(
                "binding-public-collision",
                "res-public-collision",
                "public.zarr",
                storageLayerId: WebAppFixture.TestLayerId,
                storageType: MetadataV2StorageType.Zarr)
            .AddResource(
                "res-zarr-layer-0",
                "Protected Zarr Layer",
                MetadataV2ResourceType.RasterDataset)
            .AddStorageBinding(
                "binding-zarr-layer-0",
                "res-zarr-layer-0",
                "protected.zarr",
                // Deliberately differ from the service-local publication index. The
                // route must authorize the resolved publication/resource, not treat
                // its index as a storage-layer id.
                storageLayerId: WebAppFixture.TestLayerId + 1000,
                storageType: MetadataV2StorageType.Zarr)
            .AddService(
                "svc-zarr",
                "zarr",
                protocols: ["OGC-API-Coverages"],
                accessPolicy: new AccessPolicy { AllowAnonymous = false })
            .AddPublication(
                "pub-zarr-layer-0",
                "svc-zarr",
                "res-zarr-layer-0",
                layerIndex: WebAppFixture.TestLayerId,
                storageBindingId: "binding-zarr-layer-0",
                publicationType: MetadataV2PublicationType.OgcCollection)
            .BuildProvider();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
        await _anonymousFixture.InitializeAsync();
        _anonymousClient = _anonymousFixture.Client;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        await _anonymousFixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/datacubes/{layerId}/tiles/{tileMatrixSetId}/{z}/{x}/{y}")]
    public async Task DatacubeTile_LayerWithoutServableCoverage_Returns404()
    {
        // The default test layer has no registered Zarr coverage, so the datacube tile
        // handler resolves no servable coverage and returns 404.
        var response = await _client.GetAsync(
            $"/api/v1/datacubes/{WebAppFixture.TestLayerId}/tiles/WebMercatorQuad/0/0/0");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/datacubes/{layerId}/tiles/{tileMatrixSetId}/{z}/{x}/{y}")]
    public async Task DatacubeTile_WithoutAuthentication_Returns401BeforeStoreLookup()
    {
        // Before the per-layer authorization guard, this request reached the Zarr
        // store and returned 404. A valid layer id must not reveal registration
        // state or pixels to an anonymous caller.
        var response = await _anonymousClient.GetAsync(
            $"/api/v1/datacubes/{WebAppFixture.TestLayerId}/tiles/WebMercatorQuad/0/0/0");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
