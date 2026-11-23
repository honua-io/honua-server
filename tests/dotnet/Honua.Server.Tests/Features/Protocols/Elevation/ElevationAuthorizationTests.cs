// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Tests.Features.Protocols.Terrain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.Elevation;

[Collection("Database")]
[Protocol(TestProtocols.Elevation)]
public sealed class ElevationAuthorizationTests : IAsyncLifetime
{
    private const string ValuePath = "/elevation/0/value?x=10&y=10";
    private const string ProfilePath = "/elevation/0/profile?line=LINESTRING(0%200%2C%2010%2010)&sampleCount=5";
    private const string CollectionValuePath = "/elevation/Test%20Layer/value?x=10&y=10";

    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureWebHost(builder =>
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
    });

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData(ValuePath)]
    [InlineData(ProfilePath)]
    [InlineData(CollectionValuePath)]
    [Operation(Operations.Security)]
    [Endpoint("GET /elevation/{datasetId}/value")]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevation_ProtectedDataset_WithoutCredentials_IsDeniedWithoutElevation(string path)
    {
        await using var heldRasters = await TerrainTestRasters.HoldFullWorldAsync(_fixture, 321);
        RestrictService();
        using var anonymous = _fixture.CreateClient();

        var response = await anonymous.GetAsync(path);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("application/json");
    }

    [IntegrationTheory]
    [InlineData(ValuePath)]
    [InlineData(ProfilePath)]
    [Operation(Operations.Security)]
    [Endpoint("GET /elevation/{datasetId}/value")]
    [Endpoint("GET /elevation/{datasetId}/profile")]
    public async Task GetElevation_ProtectedDataset_WithAdministratorKey_ReturnsElevation(string path)
    {
        await using var heldRasters = await TerrainTestRasters.HoldFullWorldAsync(_fixture, 321);
        RestrictService();
        using var administrator = _fixture.CreateAdminClient();

        var response = await administrator.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("321");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /elevation/{datasetId}/value")]
    public async Task GetElevationValue_AnonymousDataset_WithoutCredentials_ReturnsElevation()
    {
        await using var heldRasters = await TerrainTestRasters.HoldFullWorldAsync(_fixture, 321);
        _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy { AllowAnonymous = true });
        using var anonymous = _fixture.CreateClient();

        var response = await anonymous.GetAsync(ValuePath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("elevation").GetDouble().Should().BeApproximately(321, 0.01);
    }

    private void RestrictService()
        => _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["elevation-reader"] });
}
