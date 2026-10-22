// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Spec;

[Protocol(TestProtocols.Spec)]
public sealed class GaSpecEndpointTests
{
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /v1/spec/ga/plans")]
    public async Task GaRoutes_AreAbsentByDefault()
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/v1/spec/ga/plans", Json("{}"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/ga/plans/{id}/apply")]
    public async Task HumanApply_RejectsAuthenticatedNonSessionCredential()
    {
        using var factory = new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("GaSpec:Enabled", "true");
            builder.UseSetting("HONUA_DEV_AUTH", "true");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "true");
        });
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/v1/spec/ga/plans/unknown/apply", Json("{}"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("human-session-required", await response.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /v1/spec/ga/plans")]
    public async Task Plan_RejectsUnknownCredentialAndAttributionFields()
    {
        using var factory = new TestWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("GaSpec:Enabled", "true");
            builder.UseSetting("HONUA_DEV_AUTH", "true");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "true");
        });
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/v1/spec/ga/plans", Json("""{"as":"admin","secret":{"provider":"env","key":"CANARY"}}"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid-request", body);
        Assert.DoesNotContain("CANARY", body);
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
}
