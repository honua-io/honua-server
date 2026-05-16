// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Server.Features.Collaboration.FeatureLocks;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Collaboration.FeatureLocks;

[Protocol(Honua.TestKit.Constants.Protocols.Streaming)]
[Operation(Honua.TestKit.Constants.Operations.Update)]
public sealed class FeatureLockEndpointsTests
{
    private const string AdminPassword = "feature-lock-test-key";

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/feature-locks/claim")]
    public async Task Claim_WithAuthorizedEditor_ReturnsLease()
    {
        using var factory = CreateFactory(allowWrite: true);
        using var client = CreateClient(factory);

        using var response = await client.PostAsync(
            "/api/v1/saved-maps/saved-map:ops/collaboration/feature-locks/claim",
            JsonBody(Request("alice", "session-a")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("status").GetString().Should().Be("Claimed");
        data.GetProperty("lease").GetProperty("feature").GetProperty("serviceName").GetString().Should().Be("parcels");
        data.GetProperty("lease").GetProperty("holder").GetProperty("holderId").GetString().Should().Be("alice");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/feature-locks/claim")]
    public async Task Claim_WhenFeatureAlreadyLocked_ReturnsTypedConflict()
    {
        using var factory = CreateFactory(allowWrite: true);
        using var client = CreateClient(factory);

        using var first = await client.PostAsync(
            "/api/v1/saved-maps/saved-map:ops/collaboration/feature-locks/claim",
            JsonBody(Request("alice", "session-a")));
        using var second = await client.PostAsync(
            "/api/v1/saved-maps/saved-map:ops/collaboration/feature-locks/claim",
            JsonBody(Request("bob", "session-b")));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var document = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("status").GetString().Should().Be("HeldByOther");
        data.GetProperty("error").GetProperty("code").GetString().Should().Be("feature-lock-held");
        data.GetProperty("error").GetProperty("holder").GetProperty("holderId").GetString().Should().Be("alice");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/feature-locks/renew")]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/feature-locks/release")]
    public async Task RenewAndRelease_WithSameHolder_ReturnSuccess()
    {
        using var factory = CreateFactory(allowWrite: true);
        using var client = CreateClient(factory);
        var request = Request("alice", "session-a");
        using var claim = await client.PostAsync(
            "/api/v1/saved-maps/saved-map:ops/collaboration/feature-locks/claim",
            JsonBody(request));

        using var renew = await client.PostAsync(
            "/api/v1/saved-maps/saved-map:ops/collaboration/feature-locks/renew",
            JsonBody(request));
        using var release = await client.PostAsync(
            "/api/v1/saved-maps/saved-map:ops/collaboration/feature-locks/release",
            JsonBody(request));

        claim.StatusCode.Should().Be(HttpStatusCode.OK);
        renew.StatusCode.Should().Be(HttpStatusCode.OK);
        release.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await release.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("data").GetProperty("status").GetString().Should().Be("Released");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/saved-maps/{mapId}/collaboration/feature-locks/claim")]
    public async Task Claim_WithoutFeatureLockAuthorization_ReturnsForbidden()
    {
        using var factory = CreateFactory(allowWrite: false);
        using var client = CreateClient(factory);

        using var response = await client.PostAsync(
            "/api/v1/saved-maps/saved-map:ops/collaboration/feature-locks/claim",
            JsonBody(Request("alice", "session-a")));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static WebApplicationFactory<Program> CreateFactory(bool allowWrite)
    {
        return new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["HONUA_DEV_AUTH"] = "false",
                        ["HONUA_ADMIN_PASSWORD"] = AdminPassword
                    });
                });

                if (allowWrite)
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IFeatureLockAuthorizer>();
                        services.AddSingleton<IFeatureLockAuthorizer, AllowFeatureLockAuthorizer>();
                    });
                }
            });
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        return client;
    }

    private static FeatureLockMutationRequest Request(string holderId, string sessionId) =>
        new()
        {
            ServiceName = "parcels",
            LayerId = 7,
            FeatureId = "42",
            HolderId = holderId,
            DisplayName = holderId,
            SessionId = sessionId,
            TenantId = "tenant-1",
            LeaseSeconds = 120
        };

    private static StringContent JsonBody(FeatureLockMutationRequest request)
    {
        var json = JsonSerializer.Serialize(
            request,
            FeatureLockJsonContext.Default.FeatureLockMutationRequest);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private sealed class AllowFeatureLockAuthorizer : IFeatureLockAuthorizer
    {
        public ValueTask<FeatureLockAuthorizationResult> AuthorizeAsync(
            string mapId,
            FeatureRef feature,
            ClaimsPrincipal principal,
            string operation,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(FeatureLockAuthorizationResult.AllowWrite());
    }
}
