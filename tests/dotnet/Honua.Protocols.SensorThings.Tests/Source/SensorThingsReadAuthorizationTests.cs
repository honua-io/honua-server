// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

// Sensing reads need a signed-in caller unless the deployment opts into open reads.
[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsReadAuthorizationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1")]
    [Endpoint("GET /sta/v1.1/Things")]
    [Endpoint("GET /sta/v1.1/Observations")]
    [Endpoint("GET /sta/v1.1/Datastreams({id})/Observations")]
    [Endpoint("GET /sta/v1.1/{**resourcePath}")]
    [InlineData("/sta/v1.1")]
    [InlineData("/sta/v1.1/Things")]
    [InlineData("/sta/v1.1/Observations")]
    [InlineData("/sta/v1.1/Datastreams(1)/Observations")]
    [InlineData("/sta/v1.1/Things(1)/Datastreams(1)/Observations")]
    public async Task Read_Anonymous_ReturnsUnauthorized(string path)
    {
        using var response = await _fixture.Client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things")]
    public async Task Read_NonAdminWithoutTenantClaim_IsAdmittedInSingleTenantMode()
    {
        const string referer = "https://sensing-read-proof.example/";
        var credential = await _fixture.GetService<IPortalTokenIssuer>().IssueAsync(
            new PortalTokenIssueRequest("sensing-read-proof", "Sensing read proof", null, ["reader"],
                PortalTokenClientType.Referer, referer, DateTimeOffset.UtcNow.AddMinutes(1)),
            CancellationToken.None);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/sta/v1.1/Things?token={credential.Token}");
        request.Headers.Referrer = new Uri(referer);

        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [IntegrationTheory]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1")]
    [Endpoint("GET /sta/v1.1/Things")]
    [Endpoint("GET /sta/v1.1/Observations")]
    [InlineData("/sta/v1.1")]
    [InlineData("/sta/v1.1/Things")]
    [InlineData("/sta/v1.1/Observations")]
    public async Task Read_SignedIn_ReturnsOk(string path)
    {
        using var client = _fixture.CreateAdminClient();
        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }
}

[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsOpenReadOptInTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("SensorThings:AllowAnonymousReads", "true");
        });

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1")]
    [Endpoint("GET /sta/v1.1/Things")]
    [InlineData("/sta/v1.1")]
    [InlineData("/sta/v1.1/Things")]
    public async Task Read_AnonymousWhenOpenReadsEnabled_ReturnsOk(string path)
    {
        using var response = await _fixture.Client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Things")]
    public async Task Write_AnonymousWhenOpenReadsEnabled_StillReturnsUnauthorized()
    {
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await _fixture.Client.PostAsync("/sta/v1.1/Things", content);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
