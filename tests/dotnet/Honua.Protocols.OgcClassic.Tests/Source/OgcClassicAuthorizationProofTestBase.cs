// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic;

public abstract class OgcClassicAuthorizationProofTestBase : IAsyncLifetime
{
    protected const string ViewerRole = "map-viewer";
    protected const string OutsiderRole = "unrelated-role";

    protected static readonly string[] SeededAttributeValues =
        ["Test Feature", "Another Feature", "Fifth Feature", "A test feature for integration tests"];

    protected static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47];

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
        .ConfigureWebHost(builder => builder
            .UseSetting("HONUA_DEV_AUTH", "false")
            .UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false"))
        .ConfigureServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.PostConfigureAll<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                options.DefaultScheme = TestAuthHandler.SchemeName;
            });
        });

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        // The default test graph publishes the "test" service as AllowAnonymous; close
        // it and require a single read role so a denied principal is a real principal
        // that simply lacks the grant, not merely an anonymous caller.
        _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            accessPolicy: new AccessPolicy
            {
                AllowAnonymous = false,
                AllowAnonymousWrite = false,
                AllowedRoles = [ViewerRole]
            });

        // The committed WMS baselines render layer 0 without a TIME default; clear the
        // temporal slot so GetMap/GetFeatureInfo return the seeded points rather than
        // an empty time slice.
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, clearTemporal: true);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    protected WebAppFixture Fixture => _fixture;

    protected HttpClient Viewer() => CreateClient(ViewerRole);

    protected HttpClient Outsider() => CreateClient(OutsiderRole);

    private HttpClient CreateClient(params string[] roles) => _fixture.CreateClient(client =>
    {
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "ogc-classic-proof-user");
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
    });

    protected static async Task<string> AssertRefusedWithoutPayloadAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var body = Encoding.UTF8.GetString(bytes);

        response.StatusCode.Should().Be(expectedStatus, body);
        response.Content.Headers.ContentType?.MediaType.Should().NotStartWith("image/",
            "a refusal must not be served as imagery");
        bytes.Take(PngMagic.Length).Should().NotEqual(PngMagic, "no PNG may be emitted on a refusal");
        bytes.Take(2).Should().NotEqual([(byte)0xFF, (byte)0xD8], "no JPEG may be emitted on a refusal");
        bytes.Take(2).Should().NotEqual([(byte)'I', (byte)'I'], "no TIFF may be emitted on a refusal");

        foreach (var value in SeededAttributeValues)
        {
            body.Should().NotContain(value, "a refusal must disclose no attribute data");
        }

        return body;
    }

    protected static async Task<byte[]> AssertImageAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, Encoding.UTF8.GetString(bytes.Take(512).ToArray()));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        bytes.Take(PngMagic.Length).Should().Equal(PngMagic);
        return bytes;
    }
}
