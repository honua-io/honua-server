// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.Terrain;

[Collection("Database")]
[Protocol(TestProtocols.Terrain)]
public sealed class TerrainAuthorizationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureWebHost(builder =>
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
    });

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("/terrain/0/tile.json")]
    [InlineData("/terrain/0/0/0/0.png")]
    [InlineData("/terrain/0/layer.json")]
    [InlineData("/terrain/0/0/0/0.terrain")]
    [InlineData("/terrain/Test%20Layer/0/0/0.terrain")]
    [Operation(Operations.Security)]
    [Endpoint("GET /terrain/{datasetId}/tile.json")]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.png")]
    [Endpoint("GET /terrain/{datasetId}/layer.json")]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.terrain")]
    public async Task GetTerrain_ProtectedDataset_WithoutCredentials_IsDeniedWithoutTerrainData(string path)
    {
        await using var heldRasters = await TerrainTestRasters.HoldFullWorldAsync(_fixture, 321);
        RestrictService();
        using var anonymous = _fixture.CreateClient();

        var response = await anonymous.GetAsync(path);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        response.Content.Headers.ContentEncoding.Should().NotContain("gzip");
        new[] { "image/png", "application/vnd.quantized-mesh", "application/json" }
            .Should().NotContain(response.Content.Headers.ContentType?.MediaType);
    }

    [IntegrationTheory]
    [InlineData("/terrain/0/tile.json")]
    [InlineData("/terrain/0/0/0/0.png")]
    [InlineData("/terrain/0/layer.json")]
    [InlineData("/terrain/0/0/0/0.terrain")]
    [Operation(Operations.Security)]
    [Endpoint("GET /terrain/{datasetId}/tile.json")]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.png")]
    [Endpoint("GET /terrain/{datasetId}/layer.json")]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.terrain")]
    public async Task GetTerrain_ProtectedDataset_WithAdministratorKey_IsServed(string path)
    {
        await using var heldRasters = await TerrainTestRasters.HoldFullWorldAsync(_fixture, 321);
        RestrictService();
        using var administrator = _fixture.CreateAdminClient();

        var response = await administrator.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.Public.Should().BeFalse("an authorized response must not enter a shared cache");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /terrain/{datasetId}/{z}/{x}/{y}.terrain")]
    public async Task GetTerrainQuantizedMeshTile_AnonymousDataset_WithoutCredentials_IsServed()
    {
        await using var heldRasters = await TerrainTestRasters.HoldFullWorldAsync(_fixture, 321);
        _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy { AllowAnonymous = true });
        using var anonymous = _fixture.CreateClient();

        var response = await anonymous.GetAsync("/terrain/0/0/0/0.terrain");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.Should().Contain("gzip");
    }

    private void RestrictService()
        => _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["admin"] });
}
