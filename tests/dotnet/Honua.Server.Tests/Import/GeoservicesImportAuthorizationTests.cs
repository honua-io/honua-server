// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Import;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
[SecurityTest]
public sealed class GeoservicesImportAuthorizationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .WithProductionJobSubstrate()
        .WithTestLicense(HonuaEdition.Enterprise)
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [Endpoint("POST /api/v1/admin/import/geoservices/discover")]
    [Endpoint("POST /api/v1/admin/import/geoservices/start")]
    [Endpoint("GET /api/v1/admin/import/geoservices/jobs")]
    [Endpoint("GET /api/v1/admin/import/geoservices/jobs/{jobId}")]
    [Endpoint("POST /api/v1/admin/import/geoservices/jobs/{jobId}/cancel")]
    [InlineData("POST", "discover")]
    [InlineData("POST", "start")]
    [InlineData("GET", "jobs")]
    [InlineData("GET", "jobs/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "jobs/00000000-0000-0000-0000-000000000001/cancel")]
    public async Task Import_AnonymousRequest_ReturnsUnauthorized(string method, string route)
    {
        using var client = _fixture.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/v1/admin/import/geoservices/{route}");
        if (request.Method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(new { });
        }

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
