// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using FluentAssertions.Execution;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class AdminAuthEndpointsTests
{
    [Protocol(TestProtocols.Admin)]
    [Operation(Operations.Configuration)]
    public sealed class FrontChannelLogout(BaseSessionHost baseHost) : IClassFixture<BaseSessionHost>
    {
        // An IdP loads the front-channel logout URL in an iframe when the user signs out there.
        // With no matching Honua session it must be a no-op 200 that is never cached, for
        // every configured provider, and it must not touch cookies.
        [IntegrationTheory]
        [InlineData("azuread", "")]
        [InlineData("azuread", "?iss=https%3A%2F%2Fidp.example.com&sid=unknown-session")]
        [InlineData("oidc", "")]
        [InlineData("oidc", "?iss=https%3A%2F%2Fauth.example.com&sid=unknown-session")]
        [Endpoint("GET /signout-oidc")]
        public async Task FrontChannelLogout_WithoutMatchingSession_IsNoOpOk(string provider, string query)
        {
            var host = provider == "azuread"
                ? await baseHost.GetAzureAdHostAsync()
                : await baseHost.GetGenericOidcHostAsync();
            using var client = host.CreateBrowser();
            client.BaseAddress = new Uri("https://localhost");

            using var response = await client.GetAsync($"/signout-oidc{query}");

            using var scope = new AssertionScope();
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.CacheControl?.NoStore.Should().BeTrue();
            response.Headers.Contains("Set-Cookie").Should().BeFalse();
        }
    }
}
