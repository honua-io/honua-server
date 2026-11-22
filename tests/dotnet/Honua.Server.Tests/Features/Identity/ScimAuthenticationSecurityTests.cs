// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Identity;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.IdentityManagement)]
public sealed class ScimAuthenticationSecurityTests
{
    private const string Token = "scim-security-integration-token-32-characters";

    [IntegrationTest]
    [Endpoint("GET /scim/v2/ServiceProviderConfig")]
    public async Task Discovery_RepeatedInvalidCredentials_RefusesSourceWithRetryAfter()
    {
        await using var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("Licensing:DevGrantEdition", "Enterprise");
            builder.UseSetting("Scim:BearerToken", Token);
            builder.UseSetting("Scim:OidcIssuer", "https://issuer.example.com");
            builder.UseSetting("Authentication:FailedAttempts:Threshold", "3");
        }).ConfigureServices(services => services.AddSingleton<IStartupFilter>(new ClientAddressFilter()));
        await fixture.InitializeAsync();
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "incorrect");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await client.GetAsync("/scim/v2/ServiceProviderConfig");
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var refused = await client.GetAsync("/scim/v2/ServiceProviderConfig");
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.Zero);
    }

    private sealed class ClientAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.82");
                await continuation(context);
            });
            next(app);
        };
    }
}
