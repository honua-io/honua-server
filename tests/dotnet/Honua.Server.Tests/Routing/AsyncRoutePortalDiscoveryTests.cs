// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
using System.Text.Json;
using System.Net;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Routing;

[Collection("Database")]
[Protocol(TestProtocols.GPServer)]
[Operation(Operations.Security)]
public sealed class AsyncRoutePortalDiscoveryTests
{
    [IntegrationTheory]
    [InlineData("test", "mock", true)]
    [InlineData("missing", "mock", false)]
    [InlineData("Routing", "mock", false)]
    [InlineData("test", "unavailable", false)]
    [Endpoint("GET /sharing/rest/portals/self")]
    public async Task AsyncRoute_RequiresConfiguredPublishedUsableService(string service, string provider, bool expected)
    {
        var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("Routing:Provider", provider);
            builder.UseSetting("Routing:AsyncGPServiceId", service);
        });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var response = await client.GetAsync("/sharing/rest/portals/self?f=json");
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var helpers = body.RootElement.GetProperty("helperServices");
            Assert.Equal(expected, helpers.TryGetProperty("asyncRoute", out var route));
            if (expected)
            {
                var uri = new Uri(route.GetProperty("url").GetString()!);
                Assert.Equal("/rest/services/test/GPServer", uri.AbsolutePath);
                using var task = await client.GetAsync(uri.AbsolutePath + "/FindRoutes?f=json");
                task.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await task.Content.ReadAsStringAsync());
                Assert.Equal("esriExecutionTypeAsynchronous", document.RootElement.GetProperty("executionType").GetString());
            }
        }
        finally { await fixture.DisposeAsync(); }
    }
    [IntegrationTest]
    [Endpoint("GET /sharing/rest/portals/self")]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task ProtectedService_IsNotDisclosedOrRunnableAnonymously()
    {
        var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("Routing:Provider", "mock");
            builder.UseSetting("Routing:AsyncGPServiceId", "test");
        });
        await fixture.InitializeAsync();
        try
        {
            fixture.UpdateV2ServiceMetadata("test", accessPolicy: new AccessPolicy { AllowedRoles = ["route-reader"] });
            using var client = fixture.CreateClient();
            using var response = await client.GetAsync("/sharing/rest/portals/self?f=json");
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(document.RootElement.GetProperty("helperServices").TryGetProperty("asyncRoute", out _));
            using var content = new StringContent("invalid", System.Text.Encoding.UTF8, "application/json");
            using var denied = await client.PostAsync("/rest/services/test/GPServer/FindRoutes/submitJob", content);
            using var denial = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
            Assert.Contains(denial.RootElement.GetProperty("error").GetProperty("code").GetInt32(), new[] { 403, 404, 498, 499 });
            Assert.False(denial.RootElement.TryGetProperty("jobId", out _));
        }
        finally { await fixture.DisposeAsync(); }
    }

}

