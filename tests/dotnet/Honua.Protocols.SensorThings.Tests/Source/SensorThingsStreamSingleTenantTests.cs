// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsStreamSingleTenantTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureWebHost(builder =>
    {
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
    });

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("GET /sta/v1.1/ObservationsStream")]
    public async Task ObservationsStream_NonAdminWithoutTenantClaim_IsAdmittedInSingleTenantMode()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var issuer = _fixture.GetService<IPortalTokenIssuer>();
        const string referer = "https://observation-proof.example/";
        var credential = await issuer.IssueAsync(new PortalTokenIssueRequest("single-tenant-proof", "Single-tenant proof",
            null, ["reader"], PortalTokenClientType.Referer, referer,
            DateTimeOffset.UtcNow.AddMinutes(1)), cts.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/sta/v1.1/ObservationsStream?datastreamId=1&token={credential.Token}");
        request.Headers.Referrer = new Uri(referer);
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await _fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        string? line;
        var sawConnected = false;
        while (!sawConnected && (line = await reader.ReadLineAsync(cts.Token)) is not null)
        {
            sawConnected = line.Contains("connected", StringComparison.Ordinal);
        }

        sawConnected.Should().BeTrue();
    }
}
