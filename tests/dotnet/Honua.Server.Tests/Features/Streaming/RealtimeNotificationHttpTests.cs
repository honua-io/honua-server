// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Streaming;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class RealtimeNotificationHttpTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/jobs")]
    public async Task Jobs_AuthenticatedRequestWithoutJobIds_ReturnsBadRequest()
    {
        await using var fixture = CreateFixture();
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();

        using var response = await client.GetAsync("/api/v1/streaming/jobs");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/jobs")]
    [Endpoint("GET /api/v1/streaming/layers")]
    public async Task Notifications_AnonymousRequests_ReturnUnauthorized()
    {
        await using var fixture = CreateFixture();
        await fixture.InitializeAsync();
        using var client = fixture.CreateClient();

        using var jobs = await client.GetAsync("/api/v1/streaming/jobs?jobId=private-job");
        using var layers = await client.GetAsync("/api/v1/streaming/layers?serviceId=private-service");

        Assert.Equal(HttpStatusCode.Unauthorized, jobs.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, layers.StatusCode);
    }

    private static WebAppFixture CreateFixture() => new WebAppFixture()
        .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
}
