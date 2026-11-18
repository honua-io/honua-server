// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Sharing;

// Credentials in a generateToken URL are refused unless the deployment opts in.
public sealed partial class SharingRestTokenTests
{
    [IntegrationTheory]
    [InlineData(TokenEndpoint)]
    [InlineData(TokenEndpoint + "/")]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/generateToken")]
    public async Task GenerateToken_QueryStringPassword_IsRefusedByDefault(string path)
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync(QueryStringCredentialUrl(path));

        await AssertQueryStringRefusalAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_PostWithoutFormBody_QueryStringPassword_IsRefusedByDefault()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.PostAsync(QueryStringCredentialUrl(TokenEndpoint), content: null);

        await AssertQueryStringRefusalAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/generateToken")]
    public async Task GenerateToken_QueryStringPortalToken_IsRefusedByDefault()
    {
        using var client = _fixture.CreateClient();
        var url = $"{TokenEndpoint}?token=portal-token-in-url" +
            $"&serverUrl={Uri.EscapeDataString("https://server.example.com/services")}" +
            $"&client=referer&referer={Uri.EscapeDataString(SecureRefererA)}&f=json";
        using var response = await client.GetAsync(url);

        await AssertQueryStringRefusalAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/generateToken")]
    public async Task GenerateToken_QueryStringPassword_WithExplicitOptIn_ReturnsToken()
    {
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
                builder.UseSetting("Authentication:PortalToken:AllowQueryStringCredentials", "true");
            });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var response = await client.GetAsync(QueryStringCredentialUrl(TokenEndpoint));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var payload = await ReadTokenPayloadAsync(response);
            payload.Token.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static string QueryStringCredentialUrl(string path)
        => $"{path}?username=admin&password={Uri.EscapeDataString(AdminPassword)}" +
            $"&client=referer&referer={Uri.EscapeDataString(SecureRefererA)}&f=json";

    private static async Task AssertQueryStringRefusalAsync(HttpResponseMessage response)
    {
        await response.AssertGeoServicesErrorAsync(400);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("POST form body").And.NotContain("\"token\"");
    }
}
