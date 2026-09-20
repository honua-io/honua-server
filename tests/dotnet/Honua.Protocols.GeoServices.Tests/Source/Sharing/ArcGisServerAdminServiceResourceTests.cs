// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Sharing;

/// <summary>
/// The read-only ArcGIS Server Admin API service resource (#5036). arcpy's
/// branch-versioning tools validate a feature-service workspace through
/// <c>admin/services/{service}.MapServer</c>; with it absent they fail
/// "ERROR 000301: The workspace is of the wrong type". The tests sign in the way
/// arcpy does: a referer-bound portal token presented as <c>token=</c>.
/// </summary>
[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class ArcGisServerAdminServiceResourceTests : IAsyncLifetime
{
    private const string ServiceId = WebAppFixture.TestServiceId;
    private const string AdminPassword = WebAppFixture.SharedAdminPassword;
    private const string Referer = "https://pro.example.com/";

    // Development auth would sign every caller in; the Admin API must see real anonymity.
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
        });

    private string _token = string.Empty;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _token = await IssueTokenAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /admin/services/{serviceName}.{serviceType}")]
    public async Task ServiceResource_DescribesTheFeatureServerExtensionOfAReadableService()
    {
        using var response = await SendSignedInAsync(HttpMethod.Get, $"/admin/services/{ServiceId}.MapServer?f=json&token={_token}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("serviceName").GetString().Should().Be(ServiceId);
        root.GetProperty("type").GetString().Should().Be("MapServer");
        root.GetProperty("configuredState").GetString().Should().Be("STARTED");
        root.GetProperty("properties").GetProperty("isBranchVersioned").GetString().Should().BeOneOf("true", "false");

        var extensions = root.GetProperty("extensions").EnumerateArray().ToArray();
        var featureServer = extensions.Single(e => e.GetProperty("typeName").GetString() == "FeatureServer");
        featureServer.GetProperty("enabled").GetString().Should().Be("true");
        featureServer.GetProperty("capabilities").GetString().Should().Contain("Query");
        featureServer.GetProperty("properties").GetProperty("isBranchVersioned").GetString()
            .Should().Be(root.GetProperty("properties").GetProperty("isBranchVersioned").GetString(),
                "the service and its FeatureServer extension agree on branch versioning");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /admin/services/{serviceName}.{serviceType}")]
    public async Task ServiceResource_AnswersTheFormPostArcpyIssues()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/services/{ServiceId}.FeatureServer")
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "pjson"),
                new KeyValuePair<string, string>("token", _token),
            ]),
        };
        request.Headers.Referrer = new Uri(Referer);
        using var response = await _fixture.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("type").GetString().Should().Be("FeatureServer");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /admin/services/{serviceName}.{serviceType}")]
    public async Task ServiceResource_RequiresAToken_AndHidesUnknownServices()
    {
        // No credential at all: the host refuses with its plain 401, because the Admin
        // API path lies outside the GeoServices envelope rules.
        using var denied = await _fixture.Client.GetAsync($"/admin/services/{ServiceId}.MapServer?f=json");
        denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the Admin API refuses an anonymous read");
        (await denied.Content.ReadAsStringAsync()).Should().NotContain("\"serviceName\"");

        using var unknown = await SendSignedInAsync(HttpMethod.Get, $"/admin/services/no-such-service.MapServer?f=json&token={_token}");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var wrongType = await SendSignedInAsync(HttpMethod.Get, $"/admin/services/{ServiceId}.GeocodeServer?f=json&token={_token}");
        wrongType.StatusCode.Should().Be(HttpStatusCode.NotFound, "the service publishes no GeocodeServer extension");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/admin/{serviceName}.{serviceType}")]
    [Endpoint("POST /rest/admin/{serviceName}.{serviceType}")]
    public async Task ServiceResource_AnswersTheSitelessSpellingArcGisProUses()
    {
        // From a connection without a site segment, Pro 3.7.1 reads
        // /rest/admin/{service}.MapServer three times per layer add.
        using var response = await SendSignedInAsync(HttpMethod.Get, $"/rest/admin/{ServiceId}.MapServer?f=json&token={_token}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("serviceName").GetString().Should().Be(ServiceId);
        document.RootElement.GetProperty("extensions").EnumerateArray()
            .Should().Contain(e => e.GetProperty("typeName").GetString() == "FeatureServer");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/rest/admin/{ServiceId}.MapServer")
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("token", _token),
            ]),
        };
        request.Headers.Referrer = new Uri(Referer);
        using var posted = await _fixture.Client.SendAsync(request);
        posted.StatusCode.Should().Be(HttpStatusCode.OK);

        using var anonymous = await _fixture.Client.GetAsync($"/rest/admin/{ServiceId}.MapServer?f=json");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<HttpResponseMessage> SendSignedInAsync(HttpMethod method, string url)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Referrer = new Uri(Referer);
        return await _fixture.Client.SendAsync(request);
    }

    private async Task<string> IssueTokenAsync()
    {
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("username", "admin"),
            new KeyValuePair<string, string>("password", AdminPassword),
            new KeyValuePair<string, string>("client", "referer"),
            new KeyValuePair<string, string>("referer", Referer),
            new KeyValuePair<string, string>("f", "json"),
        ]);
        using var response = await _fixture.Client.PostAsync("/sharing/rest/generateToken", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.TryGetProperty("token", out var token).Should().BeTrue(
            $"generateToken must issue a token, got {document.RootElement}");
        return token.GetString()!;
    }
}
