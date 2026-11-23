// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Server.Tests.Features.Sharing;

// The userinfo endpoint must refuse every token that is not a live, correctly bound
// access token, and never return an identity or a server error for one.
public sealed partial class SharingOAuth2Tests
{
    private const string NamedUser = "named.user@example.com";

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/oauth2/userinfo")]
    public async Task UserInfo_RevokedAccessToken_Returns401WithoutIdentity()
    {
        using var client = _fixture.CreateClient();
        var accessToken = await IssueAccessTokenAsync(client, "userinfo-revoked-verifier-0123456789-abcdefghijklmnop");
        using var revoke = await PostRevokeAsync(client, ("token", accessToken));
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await GetUserInfoAsync(client, accessToken, new Uri(RedirectUri));

        await AssertInvalidTokenAsync(response, accessToken);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/oauth2/userinfo")]
    public async Task UserInfo_BrowserTokenFromForeignReferer_Returns401WithoutIdentity()
    {
        using var client = _fixture.CreateClient();
        var accessToken = await IssueAccessTokenAsync(client, "userinfo-referer-verifier-0123456789-abcdefghijklmnop");

        using var response = await GetUserInfoAsync(client, accessToken, new Uri("https://attacker.example.net/page"));

        await AssertInvalidTokenAsync(response, accessToken);
    }

    [IntegrationTheory]
    [InlineData("0000")]
    [InlineData("not-a-portal-token")]
    [InlineData("eyJhbGciOiJub25lIn0.eyJzdWIiOiJhZG1pbiJ9.")]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/oauth2/userinfo")]
    public async Task UserInfo_ForgedOrMalformedToken_Returns401WithoutIdentity(string forged)
    {
        using var client = _fixture.CreateClient();

        using var response = await GetUserInfoAsync(client, forged, new Uri(RedirectUri));

        await AssertInvalidTokenAsync(response, forged);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/oauth2/userinfo")]
    public async Task UserInfo_TamperedAccessToken_Returns401WithoutIdentity()
    {
        using var client = _fixture.CreateClient();
        var accessToken = await IssueAccessTokenAsync(client, "userinfo-tamper-verifier-0123456789-abcdefghijklmnopq");
        var tampered = accessToken[..^1] + (accessToken[^1] == 'a' ? 'b' : 'a');

        using var response = await GetUserInfoAsync(client, tampered, new Uri(RedirectUri));

        await AssertInvalidTokenAsync(response, tampered);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/oauth2/userinfo")]
    public async Task UserInfo_WithoutToken_Returns400InvalidRequest()
    {
        using var client = _fixture.CreateClient();

        using var response = await client.GetAsync("/sharing/rest/oauth2/userinfo?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Error.Should().Be("invalid_request");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/userinfo")]
    public async Task UserInfo_OversizedFormToken_IsRefusedWithoutIdentityOrServerError()
    {
        using var client = _fixture.CreateClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["access_token"] = new string('a', 5 * 1024 * 1024),
        });

        using var response = await client.PostAsync("/sharing/rest/oauth2/userinfo", content);

        // Input validation refuses the form before the endpoint runs. On the ArcGIS sharing
        // surface its error travels in the GeoServices envelope, so the code is in the body.
        var body = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).Should().BeLessThan(500, body);
        body.Should().Contain("\"code\":400").And.NotContain(NamedUser);
    }

    private async Task<string> IssueAccessTokenAsync(HttpClient client, string verifier)
    {
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var code = await SeedAuthorizationCodeAsync(challenge, "S256");
        using var issued = await PostFormAsync(
            client,
            ("grant_type", "authorization_code"),
            ("code", code),
            ("redirect_uri", RedirectUri),
            ("client_id", ClientId),
            ("code_verifier", verifier));
        issued.StatusCode.Should().Be(HttpStatusCode.OK, await issued.Content.ReadAsStringAsync());
        return (await ReadTokenAsync(issued)).AccessToken;
    }

    private static async Task<HttpResponseMessage> GetUserInfoAsync(HttpClient client, string accessToken, Uri referer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sharing/rest/oauth2/userinfo?f=json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Referrer = referer;
        return await client.SendAsync(request);
    }

    private static async Task AssertInvalidTokenAsync(HttpResponseMessage response, string presented)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);
        body.Should().NotContain(NamedUser).And.NotContain(presented);
        (await ReadErrorAsync(response)).Error.Should().Be("invalid_token");
    }
}
