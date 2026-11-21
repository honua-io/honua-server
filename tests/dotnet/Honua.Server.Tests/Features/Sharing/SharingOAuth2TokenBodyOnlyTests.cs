// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Sharing;

// Token request parameters are read from the form body only, never from the URL.
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class SharingOAuth2TokenBodyOnlyTests : IAsyncLifetime
{
    private const string ClientId = "arcgispro";
    private const string RedirectUri = "https://app.example.com/oauth/redirect";
    private const string Verifier = "test-pkce-verifier-value-0123456789-abcdefghij";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
        });

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /sharing/rest/oauth2/token")]
    public async Task Token_GrantInQueryString_IsNotRedeemed()
    {
        var code = await SeedAuthorizationCodeAsync();
        using var client = _fixture.CreateClient();
        var url = "/sharing/rest/oauth2/token?grant_type=authorization_code" +
            $"&code={Uri.EscapeDataString(code)}&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&client_id={ClientId}&code_verifier={Verifier}";

        using var response = await client.PostAsync(url, content: null);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("access_token").And.Contain("invalid_request");
        using var formResponse = await RedeemWithFormAsync(client, code);
        formResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the code stayed unused by the URL attempt");
    }

    private async Task<HttpResponseMessage> RedeemWithFormAsync(HttpClient client, string code)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = Verifier,
        });
        return await client.PostAsync("/sharing/rest/oauth2/token", content);
    }

    private async Task<string> SeedAuthorizationCodeAsync()
    {
        var store = _fixture.Services.GetRequiredService<PortalOAuthStore>();
        var principal = new PortalCredentialPrincipal("named.user@example.com", "Named User", null, ["org_user"]);
        var record = new PortalOAuthAuthorizationCode
        {
            ClientId = ClientId,
            RedirectUri = RedirectUri,
            CodeChallenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier))),
            CodeChallengeMethod = "S256",
            ExpirationMinutes = null,
            Principal = PortalOAuthPrincipal.From(principal),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        return await store.CreateAuthorizationCodeAsync(record, CancellationToken.None);
    }
}
