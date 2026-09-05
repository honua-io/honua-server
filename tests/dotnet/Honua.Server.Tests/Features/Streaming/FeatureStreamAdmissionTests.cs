// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Streaming;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Streaming;

[Collection("Database")]
[Protocol(TestProtocols.Streaming)]
[Operation(Operations.Streaming)]
public sealed class FeatureStreamAdmissionTests
{
    [IntegrationTheory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task SaturatedPartition_DoesNotRejectAnotherTenantOrPrincipal(bool tenancyEnabled, bool webSocket)
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FeatureStreaming:MaxConcurrentSessions"] = "1",
                    ["MultiTenancy:Enabled"] = tenancyEnabled.ToString(),
                    ["MultiTenancy:MultiTenantAdminRoles:0"] = "admin"
                })))
            .ConfigureServices(services =>
            {
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, RlsClaimsTestAuthHandler>(
                    RlsClaimsTestAuthHandler.SchemeName, _ => { });
                services.PostConfigureAll<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = RlsClaimsTestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = RlsClaimsTestAuthHandler.SchemeName;
                    options.DefaultScheme = RlsClaimsTestAuthHandler.SchemeName;
                });
            });
        await fixture.InitializeAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var first = CreateClient("alice", "tenant-a");
            using var second = CreateClient(tenancyEnabled ? "alice" : "bob", "tenant-b");
            using var held = await OpenSseAsync(first, cts.Token);
            held.StatusCode.Should().Be(HttpStatusCode.OK);
            using var rejected = await OpenSseAsync(first, cts.Token);
            rejected.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await rejected.Content.ReadAsStringAsync(cts.Token)).Should().Contain("session limit");

            if (tenancyEnabled)
            {
                using var sameTenant = CreateClient("bob", "tenant-a");
                using var sameTenantRejected = await OpenSseAsync(sameTenant, cts.Token);
                sameTenantRejected.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            }

            var manager = fixture.GetService<FeatureStreamSessionManager>();
            if (webSocket)
            {
                var client = fixture.CreateWebSocketClient();
                client.ConfigureRequest = request =>
                {
                    request.Headers[RlsClaimsTestAuthHandler.UserHeader] = tenancyEnabled ? "alice" : "bob";
                    request.Headers[RlsClaimsTestAuthHandler.RolesHeader] = "admin";
                    request.Headers["X-Honua-Tenant"] = "tenant-b";
                };
                using var socket = await client.ConnectAsync(new Uri("ws://localhost/api/v1/streaming/features"), cts.Token);
                manager.SessionCount.Should().Be(2);
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    client.ConnectAsync(new Uri("ws://localhost/api/v1/streaming/features"), cts.Token));
                exception.Message.Should().Contain("503");
            }
            else
            {
                using var admitted = await OpenSseAsync(second, cts.Token);
                admitted.StatusCode.Should().Be(HttpStatusCode.OK);
                manager.SessionCount.Should().Be(2);
                using var secondRejected = await OpenSseAsync(second, cts.Token);
                secondRejected.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            }

            HttpClient CreateClient(string user, string tenant) => fixture.CreateClient(client =>
            {
                client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.UserHeader, user);
                client.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.RolesHeader, "admin");
                client.DefaultRequestHeaders.Add("X-Honua-Tenant", tenant);
            });
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<HttpResponseMessage> OpenSseAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
}
