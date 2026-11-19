// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Honua.Server.Features.Identity.Saml;
using Honua.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Server.Tests.Features.Identity;

internal sealed class SamlIssuedSignIn
{
    public required string RequestId { get; init; }

    // "name=value" of the correlation cookie, ready for a Cookie request header.
    public required string CorrelationCookie { get; init; }

    public required string SetCookieHeader { get; init; }

    public required Uri Location { get; init; }
}

// Drives SP-initiated SAML sign-in against a test host: GET /saml/login, then POST /saml/acs.
internal static class SamlSignInTestFlow
{
    public const string IdpSignOnUrl = "https://idp.example.com/sso";

    public static WebAppFixture CreateFixture(X509Certificate2 certificate, IReadOnlyDictionary<string, string> extraSettings)
    {
        var base64Cert = SamlTestAssertions.ToBase64Der(certificate);
        return new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("Licensing:DevGrantEdition", "Enterprise");
                builder.UseSetting("Saml:Enabled", "true");
                builder.UseSetting("Saml:EntityId", SamlTestAssertions.Audience);
                builder.UseSetting("Saml:IdpEntityId", SamlTestAssertions.Issuer);
                builder.UseSetting("Saml:AssertionConsumerServiceUrl", SamlTestAssertions.AcsUrl);
                builder.UseSetting("Saml:IdpSigningCertificate", base64Cert);
                foreach (var (key, value) in extraSettings)
                {
                    builder.UseSetting(key, value);
                }
            });
    }

    public static async Task<SamlIssuedSignIn> StartAsync(WebAppFixture fixture)
    {
        using var client = fixture.CreateClient(allowAutoRedirect: false);
        using var response = await client.GetAsync("/saml/login");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        var setCookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            header => header.StartsWith(SamlRequestCorrelation.CookieName + "=", StringComparison.Ordinal));
        var requestXml = DecodeRedirectRequest(QueryHelpers.ParseQuery(location.Query)["SAMLRequest"].ToString());
        var requestId = XDocument.Parse(requestXml).Root!.Attribute("ID")!.Value;
        return new SamlIssuedSignIn
        {
            RequestId = requestId,
            CorrelationCookie = setCookie.Split(';')[0],
            SetCookieHeader = setCookie,
            Location = location,
        };
    }

    public static string CreateResponseTo(X509Certificate2 certificate, string? inResponseTo)
        => SamlTestAssertions.CreateSignedResponse(
            certificate,
            "solicited@example.com",
            "solicited@example.com",
            "Solicited User",
            ["viewer"],
            responseInResponseTo: inResponseTo,
            confirmationInResponseTo: inResponseTo);

    // A fresh client per post keeps the correlation cookie under the test's control.
    public static async Task<HttpResponseMessage> PostAcsAsync(WebAppFixture fixture, string samlResponse, string? cookie)
    {
        using var client = fixture.CreateClient(allowAutoRedirect: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/saml/acs")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = samlResponse }),
        };
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await client.SendAsync(request);
    }

    public static bool SetsSessionCookie(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(cookie => cookie.StartsWith("honua_admin_session=", StringComparison.Ordinal)
                && !cookie.Contains("1970", StringComparison.Ordinal));

    public static string DecodeRedirectRequest(string encoded)
    {
        using var input = new MemoryStream(Convert.FromBase64String(encoded));
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(inflate, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
